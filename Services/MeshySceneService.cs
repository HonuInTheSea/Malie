using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Malie.Infrastructure;
using Malie.Models;

namespace Malie.Services;

public sealed class MeshySceneService
{
    public const string CacheVirtualHostName = "meshy-cache.local";
    public const int MaxCustomPromptLength = 800;
    private const string TextTo3DEndpoint = "/openapi/v2/text-to-3d";
    private const string ImageTo3DEndpoint = "/openapi/v1/image-to-3d";
    private const string MultiImageTo3DEndpoint = "/openapi/v1/multi-image-to-3d";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(6);
    private static readonly Regex GoogleImageUrlRegex = new(
        @"https?:\\?/\\?/[^""'\s>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions CacheSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HashSet<string> SuccessfulStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUCCEEDED"
    };

    private static readonly HashSet<string> FailedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILED",
        "CANCELED",
        "CANCELLED"
    };
    private static readonly TimeSpan CachedMeshesSnapshotTtl = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private readonly string _failedPoiTargetsPath;
    private readonly string _poiNameOverridesPath;
    private readonly object _failedPoiTargetsSync = new();
    private readonly HashSet<string> _failedPoiTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _poiNameOverridesSync = new();
    private readonly Dictionary<string, string> _poiNameOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cachedMeshesSync = new();
    private IReadOnlyList<PointOfInterestMesh>? _cachedMeshesSnapshot;
    private DateTimeOffset _cachedMeshesSnapshotAtUtc = DateTimeOffset.MinValue;
    private bool _cachedMeshesDirty = true;

    public MeshySceneService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
        _cacheRoot = Path.Combine(AppBranding.GetLocalAppDataRoot(), "cache", "meshy-poi");
        Directory.CreateDirectory(_cacheRoot);
        _failedPoiTargetsPath = Path.Combine(_cacheRoot, "failed-poi-targets.json");
        _poiNameOverridesPath = Path.Combine(_cacheRoot, "poi-name-overrides.json");
        LoadFailedPoiTargets();
        LoadPoiNameOverrides();
    }

    public string CacheRootPath => _cacheRoot;

    public void ClearCache()
    {
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }

        Directory.CreateDirectory(_cacheRoot);
        lock (_failedPoiTargetsSync)
        {
            _failedPoiTargets.Clear();
            if (File.Exists(_failedPoiTargetsPath))
            {
                File.Delete(_failedPoiTargetsPath);
            }
        }

        lock (_poiNameOverridesSync)
        {
            _poiNameOverrides.Clear();
            if (File.Exists(_poiNameOverridesPath))
            {
                File.Delete(_poiNameOverridesPath);
            }
        }

        InvalidateCachedMeshesSnapshot();
    }

    public IReadOnlyList<string> ApplyPoiNameOverrides(IReadOnlyList<string> pointsOfInterest)
    {
        if (pointsOfInterest.Count == 0)
        {
            return Array.Empty<string>();
        }

        var resolved = pointsOfInterest
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .Select(point => ResolvePoiName(point))
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return resolved;
    }

    public void SetPoiNameOverride(string originalPoiName, string renamedPoiName)
    {
        var originalKey = NormalizeTargetKey(originalPoiName);
        var renamed = renamedPoiName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(originalKey) || string.IsNullOrWhiteSpace(renamed))
        {
            return;
        }

        lock (_poiNameOverridesSync)
        {
            _poiNameOverrides[originalKey] = renamed;
            PersistPoiNameOverridesUnsafe();
        }
    }

    public string ResolvePoiName(string poiName)
    {
        var current = poiName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(current))
        {
            return string.Empty;
        }

        lock (_poiNameOverridesSync)
        {
            var guard = 0;
            while (guard < 8)
            {
                var key = NormalizeTargetKey(current);
                if (string.IsNullOrWhiteSpace(key) ||
                    !_poiNameOverrides.TryGetValue(key, out var mappedName) ||
                    string.IsNullOrWhiteSpace(mappedName))
                {
                    break;
                }

                var trimmedMapped = mappedName.Trim();
                if (string.Equals(trimmedMapped, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = trimmedMapped;
                guard += 1;
            }
        }

        return current;
    }

    public IReadOnlyList<PointOfInterestMesh> GetCachedMeshes()
    {
        lock (_cachedMeshesSync)
        {
            if (!_cachedMeshesDirty &&
                _cachedMeshesSnapshot is not null &&
                DateTimeOffset.UtcNow - _cachedMeshesSnapshotAtUtc <= CachedMeshesSnapshotTtl)
            {
                return _cachedMeshesSnapshot;
            }
        }

        var loadedMeshes = LoadCachedMeshesFromDisk();
        lock (_cachedMeshesSync)
        {
            _cachedMeshesSnapshot = loadedMeshes;
            _cachedMeshesSnapshotAtUtc = DateTimeOffset.UtcNow;
            _cachedMeshesDirty = false;
        }

        return loadedMeshes;
    }

    private IReadOnlyList<PointOfInterestMesh> LoadCachedMeshesFromDisk()
    {
        if (!Directory.Exists(_cacheRoot))
        {
            return Array.Empty<PointOfInterestMesh>();
        }

        var meshes = new List<PointOfInterestMesh>();
        foreach (var targetFolder in Directory.EnumerateDirectories(_cacheRoot))
        {
            var manifestPath = Path.Combine(targetFolder, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<CachedTargetManifest>(manifestJson, CacheSerializerOptions);
                if (manifest is null)
                {
                    continue;
                }

                foreach (var model in manifest.Models)
                {
                    var modelPath = Path.Combine(targetFolder, "models", model.FileName);
                    if (!File.Exists(modelPath))
                    {
                        continue;
                    }

                    var relativePath = BuildModelRelativePath(manifest.CacheKey, model.FileName);
                    meshes.Add(
                        new PointOfInterestMesh(
                            manifest.TargetName,
                            model.SourceTitle,
                            model.SourceUrl,
                            relativePath,
                            BuildLocalWebUrl(relativePath),
                            model.MimeType));
                }
            }
            catch
            {
                // ignore malformed cache entry
            }
        }

        return meshes
            .OrderBy(mesh => mesh.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mesh => mesh.LocalRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryDeleteCachedModel(string localRelativePath, out string message)
    {
        message = string.Empty;
        if (!TryParseModelRelativePath(localRelativePath, out var cacheKey, out var modelFileName))
        {
            message = "Delete failed: model path is not a cache model path.";
            return false;
        }

        var targetFolder = Path.Combine(_cacheRoot, cacheKey);
        var manifestPath = Path.Combine(targetFolder, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            message = "Delete failed: cache manifest was not found.";
            return false;
        }

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<CachedTargetManifest>(manifestJson, CacheSerializerOptions);
            if (manifest is null)
            {
                message = "Delete failed: cache manifest is invalid.";
                return false;
            }

            var remainingModels = manifest.Models
                .Where(model => !string.Equals(model.FileName, modelFileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (remainingModels.Length == manifest.Models.Count)
            {
                message = $"Delete skipped: model '{modelFileName}' not found in cache.";
                return false;
            }

            var modelPath = Path.Combine(targetFolder, "models", modelFileName);
            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }

            if (remainingModels.Length == 0)
            {
                if (Directory.Exists(targetFolder))
                {
                    Directory.Delete(targetFolder, recursive: true);
                }

                InvalidateCachedMeshesSnapshot();
                message = $"Deleted '{modelFileName}' and removed empty cache target '{manifest.TargetName}'.";
                return true;
            }

            var updatedManifest = manifest with
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Models = remainingModels
            };
            var updatedJson = JsonSerializer.Serialize(updatedManifest, CacheSerializerOptions);
            File.WriteAllText(manifestPath, updatedJson);

            InvalidateCachedMeshesSnapshot();
            message = $"Deleted cached model '{modelFileName}'.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Delete failed: {ex.Message}";
            return false;
        }
    }

    public bool TryResolveCachedModelFilePath(
        string localRelativePath,
        out string absoluteFilePath,
        out string message)
    {
        absoluteFilePath = string.Empty;
        message = string.Empty;
        if (!TryParseModelRelativePath(localRelativePath, out var cacheKey, out var modelFileName))
        {
            message = "Export failed: model path is not a cache model path.";
            return false;
        }

        var targetPath = Path.Combine(_cacheRoot, cacheKey, "models", modelFileName);
        if (!File.Exists(targetPath))
        {
            message = $"Export failed: cached model '{modelFileName}' was not found.";
            return false;
        }

        absoluteFilePath = targetPath;
        return true;
    }

    public bool TryRenameCachedTargetByModelPath(
        string localRelativePath,
        string newTargetName,
        out string message)
    {
        message = string.Empty;
        var trimmedName = newTargetName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            message = "Rename failed: POI name cannot be empty.";
            return false;
        }

        if (!TryParseModelRelativePath(localRelativePath, out var cacheKey, out _))
        {
            message = "Rename failed: model path is not a cache model path.";
            return false;
        }

        var manifestPath = Path.Combine(_cacheRoot, cacheKey, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            message = "Rename failed: cache manifest was not found.";
            return false;
        }

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<CachedTargetManifest>(manifestJson, CacheSerializerOptions);
            if (manifest is null)
            {
                message = "Rename failed: cache manifest is invalid.";
                return false;
            }

            if (string.Equals(manifest.TargetName, trimmedName, StringComparison.Ordinal))
            {
                message = $"POI name unchanged ('{trimmedName}').";
                return true;
            }

            var updatedManifest = manifest with
            {
                TargetName = trimmedName,
                CachedAtUtc = DateTimeOffset.UtcNow
            };
            var updatedJson = JsonSerializer.Serialize(updatedManifest, CacheSerializerOptions);
            File.WriteAllText(manifestPath, updatedJson);

            InvalidateCachedMeshesSnapshot();
            message = $"Updated POI name to '{trimmedName}'.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Rename failed: {ex.Message}";
            return false;
        }
    }

    public bool TryImportGlbModel(
        string sourceFilePath,
        string? poiName,
        out PointOfInterestMesh? importedMesh,
        out string message)
    {
        importedMesh = null;
        message = string.Empty;

        var sourcePath = sourceFilePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            message = "Import failed: source GLB file was not found.";
            return false;
        }

        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
        {
            message = "Import failed: only .glb files are supported for import.";
            return false;
        }

        var targetName = string.IsNullOrWhiteSpace(poiName)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : poiName.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = "Imported Model";
        }

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var keySeed =
                $"import::{targetName}::{sourceInfo.Name}::{sourceInfo.Length}::{sourceInfo.LastWriteTimeUtc:O}";
            var cacheKey = BuildCacheKey(keySeed);
            var targetFolder = Path.Combine(_cacheRoot, cacheKey);
            var modelsFolder = Path.Combine(targetFolder, "models");

            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, recursive: true);
            }

            Directory.CreateDirectory(modelsFolder);
            var targetFileName = "model_01.glb";
            var targetFilePath = Path.Combine(modelsFolder, targetFileName);
            File.Copy(sourcePath, targetFilePath, overwrite: true);

            var modelItem = new CachedModelItem(
                SourceTitle: "Imported GLB",
                SourceUrl: sourcePath,
                FileName: targetFileName,
                MimeType: "model/gltf-binary");
            var manifest = new CachedTargetManifest(
                TargetName: targetName,
                CacheKey: cacheKey,
                CachedAtUtc: DateTimeOffset.UtcNow,
                Images: Array.Empty<CachedImageItem>(),
                Models: new[] { modelItem });
            var manifestJson = JsonSerializer.Serialize(manifest, CacheSerializerOptions);
            File.WriteAllText(Path.Combine(targetFolder, "manifest.json"), manifestJson);

            var relativePath = BuildModelRelativePath(cacheKey, targetFileName);
            importedMesh = new PointOfInterestMesh(
                Name: targetName,
                SourceTitle: modelItem.SourceTitle,
                SourceUrl: modelItem.SourceUrl,
                LocalRelativePath: relativePath,
                LocalWebUrl: BuildLocalWebUrl(relativePath),
                MimeType: modelItem.MimeType);

            InvalidateCachedMeshesSnapshot();
            message = $"Imported GLB as '{targetName}' ({targetFileName}).";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Import failed: {ex.Message}";
            return false;
        }
    }

    public async Task<MeshyGenerationResult> GenerateTexturedLandmarkReferencesAsync(
        string cityName,
        WeatherSnapshot weather,
        IReadOnlyList<string> pointsOfInterest,
        string apiKey,
        CancellationToken cancellationToken,
        bool forceRetrySkippedTargets = false,
        Action<string>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                "Meshy API key is empty.");
        }

        var targets = BuildTargets(cityName, pointsOfInterest);
        if (targets.Count == 0)
        {
            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                "No targets available for Meshy generation.");
        }

        var generatedImages = new List<PointOfInterestImage>();
        var generatedMeshes = new List<PointOfInterestMesh>();
        var modelUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var cachedMeshesByPoiName = GetCachedMeshes()
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.Name))
            .GroupBy(mesh => NormalizeTargetKey(mesh.Name))
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var poiTargetSet = pointsOfInterest
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .Select(point => NormalizeTargetKey(point))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedTarget = NormalizeTargetKey(target);
            var isPointOfInterestTarget = poiTargetSet.Contains(normalizedTarget);
            try
            {
                var cacheHit = await TryLoadCachedTargetAsync(target, cancellationToken);
                if (cacheHit is not null)
                {
                    generatedImages.AddRange(cacheHit.Images);
                    generatedMeshes.AddRange(cacheHit.Meshes);
                    foreach (var cachedMesh in cacheHit.Meshes)
                    {
                        if (!string.IsNullOrWhiteSpace(cachedMesh.SourceUrl))
                        {
                            modelUrls.Add(cachedMesh.SourceUrl);
                        }
                    }

                    progressCallback?.Invoke(
                        $"Meshy cache hit for '{target}'. Reusing {cacheHit.Images.Count} image(s) and {cacheHit.Meshes.Count} mesh file(s).");
                    continue;
                }

                if (isPointOfInterestTarget && IsTargetMarkedToSkip(target))
                {
                    if (forceRetrySkippedTargets)
                    {
                        ClearTargetSkipMark(target);
                        progressCallback?.Invoke(
                            $"Meshy target '{target}' was previously skipped. Retrying due to manual queue request.");
                    }
                    else
                    {
                        progressCallback?.Invoke(
                            $"Meshy target '{target}' skipped due to prior generation failure.");
                        continue;
                    }
                }

                if (isPointOfInterestTarget &&
                    cachedMeshesByPoiName.TryGetValue(normalizedTarget, out var existingPoiMeshes) &&
                    existingPoiMeshes.Length > 0)
                {
                    generatedMeshes.AddRange(existingPoiMeshes);
                    foreach (var cachedPoiMesh in existingPoiMeshes)
                    {
                        if (!string.IsNullOrWhiteSpace(cachedPoiMesh.SourceUrl))
                        {
                            modelUrls.Add(cachedPoiMesh.SourceUrl);
                        }
                    }

                    progressCallback?.Invoke(
                        $"Meshy target '{target}' already has cached GLB mesh(es). Skipping Meshy API for this POI.");
                    continue;
                }

                try
                {
                    progressCallback?.Invoke($"Meshy target '{target}': text-to-3D requested.");
                    await GenerateAndCacheFromTextAsync(target, apiKey, cancellationToken, progressCallback);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception textError)
                {
                    progressCallback?.Invoke($"Meshy target '{target}': text-to-3D failed ({textError.Message}). Trying Google-image fallback.");
                    try
                    {
                        await GenerateAndCacheFromImageFallbackAsync(target, apiKey, cancellationToken, progressCallback);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception fallbackError)
                    {
                        errors.Add($"{target}: text failed ({Truncate(textError.Message, 120)}), image fallback failed ({Truncate(fallbackError.Message, 120)})");
                        progressCallback?.Invoke($"Meshy target '{target}': image fallback failed ({fallbackError.Message}).");
                        if (isPointOfInterestTarget)
                        {
                            MarkTargetToSkip(target, progressCallback);
                        }
                        continue;
                    }
                }

                var cachedAfterGeneration = await TryLoadCachedTargetAsync(target, cancellationToken);
                if (cachedAfterGeneration is null)
                {
                    errors.Add($"{target}: generated task completed but cache was empty.");
                    if (isPointOfInterestTarget)
                    {
                        MarkTargetToSkip(target, progressCallback);
                    }
                    continue;
                }

                generatedImages.AddRange(cachedAfterGeneration.Images);
                generatedMeshes.AddRange(cachedAfterGeneration.Meshes);
                foreach (var generatedMesh in cachedAfterGeneration.Meshes)
                {
                    if (!string.IsNullOrWhiteSpace(generatedMesh.SourceUrl))
                    {
                        modelUrls.Add(generatedMesh.SourceUrl);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (isPointOfInterestTarget)
                {
                    MarkTargetToSkip(target, progressCallback);
                    progressCallback?.Invoke($"Meshy target '{target}' canceled and marked to skip for future runs.");
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        errors.Add($"{target}: canceled during generation and marked to skip.");
                        continue;
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"{target}: canceled during generation.");
                    progressCallback?.Invoke($"Meshy target '{target}' canceled. Moving to next target.");
                    continue;
                }

                throw;
            }
        }

        if (generatedImages.Count == 0 && generatedMeshes.Count == 0)
        {
            var noOutputMessage = errors.Count == 0
                ? "Meshy returned no model or texture output."
                : $"Meshy returned no model or texture output. {string.Join(" | ", errors)}";

            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                noOutputMessage);
        }

        var dedupedImages = generatedImages
            .DistinctBy(image => $"{image.Name}::{image.SourceUrl}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dedupedMeshes = generatedMeshes
            .DistinctBy(mesh => mesh.LocalRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var message = $"Meshy generated/reused {dedupedImages.Length} texture image(s) and {dedupedMeshes.Length} mesh file(s).";
        if (errors.Count > 0)
        {
            message = $"{message} Partial failures: {string.Join(" | ", errors.Take(3))}";
        }

        return new MeshyGenerationResult(
            true,
            dedupedImages,
            dedupedMeshes,
            modelUrls.ToArray(),
            message);
    }

    public async Task<MeshyGenerationResult> GenerateFromCustomPromptAsync(
        string prompt,
        string apiKey,
        CancellationToken cancellationToken,
        Action<string>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                "Meshy API key is empty.");
        }

        var normalizedPrompt = (prompt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                "Custom Meshy prompt is empty.");
        }

        if (normalizedPrompt.Length > MaxCustomPromptLength)
        {
            normalizedPrompt = normalizedPrompt[..MaxCustomPromptLength];
        }

        var target = BuildCustomPromptTargetName(normalizedPrompt);
        cancellationToken.ThrowIfCancellationRequested();

        var cacheHit = await TryLoadCachedTargetAsync(target, cancellationToken);
        if (cacheHit is not null)
        {
            var cachedModelUrls = cacheHit.Meshes
                .Where(mesh => !string.IsNullOrWhiteSpace(mesh.SourceUrl))
                .Select(mesh => mesh.SourceUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            progressCallback?.Invoke(
                $"Meshy cache hit for custom prompt '{target}'. Reusing {cacheHit.Images.Count} image(s) and {cacheHit.Meshes.Count} mesh file(s).");
            return new MeshyGenerationResult(
                true,
                cacheHit.Images,
                cacheHit.Meshes,
                cachedModelUrls,
                $"Meshy cache reused custom prompt '{target}'.");
        }

        try
        {
            progressCallback?.Invoke($"Meshy custom prompt '{target}': text-to-3D requested.");
            await GenerateAndCacheFromTextAsync(target, normalizedPrompt, apiKey, cancellationToken, progressCallback);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                $"Meshy custom prompt failed: {Truncate(ex.Message, 240)}");
        }

        var cachedAfterGeneration = await TryLoadCachedTargetAsync(target, cancellationToken);
        if (cachedAfterGeneration is null || cachedAfterGeneration.Meshes.Count == 0)
        {
            return new MeshyGenerationResult(
                false,
                Array.Empty<PointOfInterestImage>(),
                Array.Empty<PointOfInterestMesh>(),
                Array.Empty<string>(),
                "Meshy returned no model or texture output.");
        }

        var modelUrls = cachedAfterGeneration.Meshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.SourceUrl))
            .Select(mesh => mesh.SourceUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MeshyGenerationResult(
            true,
            cachedAfterGeneration.Images,
            cachedAfterGeneration.Meshes,
            modelUrls,
            $"Meshy custom prompt generated {cachedAfterGeneration.Meshes.Count} mesh file(s).");
    }

    private async Task GenerateAndCacheFromTextAsync(
        string target,
        string apiKey,
        CancellationToken cancellationToken,
        Action<string>? progressCallback)
    {
        var prompt = BuildPoiPrompt(target);
        await GenerateAndCacheFromTextAsync(target, prompt, apiKey, cancellationToken, progressCallback);
    }

    private async Task GenerateAndCacheFromTextAsync(
        string target,
        string prompt,
        string apiKey,
        CancellationToken cancellationToken,
        Action<string>? progressCallback)
    {
        progressCallback?.Invoke($"Meshy text preview submit: {target}");
        var previewTaskId = await SubmitTaskAsync(
            TextTo3DEndpoint,
            new Dictionary<string, object?>
            {
                ["mode"] = "preview",
                ["prompt"] = prompt
            },
            apiKey,
            cancellationToken);
        var preview = await WaitForTaskCompletionAsync(TextTo3DEndpoint, apiKey, previewTaskId, target, cancellationToken, progressCallback);

        progressCallback?.Invoke($"Meshy text refine submit: {target}");
        var refineTaskId = await SubmitTaskAsync(
            TextTo3DEndpoint,
            new Dictionary<string, object?>
            {
                ["mode"] = "refine",
                ["preview_task_id"] = previewTaskId,
                ["enable_pbr"] = true,
                ["texture_prompt"] = prompt
            },
            apiKey,
            cancellationToken);
        var refine = await WaitForTaskCompletionAsync(TextTo3DEndpoint, apiKey, refineTaskId, target, cancellationToken, progressCallback);

        var preferred = refine.ModelUrls.Count > 0 ? refine : preview;
        if (preferred.ModelUrls.Count == 0)
        {
            throw new InvalidOperationException("Text-to-3D produced no model URLs.");
        }

        var fallbackImageUrls = refine.ImageUrls.Count > 0 ? refine.ImageUrls : preview.ImageUrls;
        await CacheTaskOutputsAsync(
            target,
            "Meshy text-to-3D",
            preferred,
            fallbackImageUrls,
            progressCallback,
            cancellationToken);
    }

    private async Task GenerateAndCacheFromImageFallbackAsync(
        string target,
        string apiKey,
        CancellationToken cancellationToken,
        Action<string>? progressCallback)
    {
        var googleImageUrls = await SearchGoogleImageUrlsAsync($"{target} landmark", cancellationToken);
        if (googleImageUrls.Count == 0)
        {
            throw new InvalidOperationException($"No Google image results found for '{target}'.");
        }

        MeshyTaskState taskState;
        if (googleImageUrls.Count >= 2)
        {
            progressCallback?.Invoke($"Meshy image fallback submit (multi-image): {target}");
            var taskId = await SubmitTaskAsync(
                MultiImageTo3DEndpoint,
                new Dictionary<string, object?>
                {
                    ["image_urls"] = googleImageUrls.Take(4).ToArray(),
                    ["should_texture"] = true,
                    ["enable_pbr"] = true
                },
                apiKey,
                cancellationToken);
            taskState = await WaitForTaskCompletionAsync(MultiImageTo3DEndpoint, apiKey, taskId, target, cancellationToken, progressCallback);
        }
        else
        {
            progressCallback?.Invoke($"Meshy image fallback submit (single-image): {target}");
            var taskId = await SubmitTaskAsync(
                ImageTo3DEndpoint,
                new Dictionary<string, object?>
                {
                    ["image_url"] = googleImageUrls[0],
                    ["should_texture"] = true,
                    ["enable_pbr"] = true
                },
                apiKey,
                cancellationToken);
            taskState = await WaitForTaskCompletionAsync(ImageTo3DEndpoint, apiKey, taskId, target, cancellationToken, progressCallback);
        }

        if (taskState.ModelUrls.Count == 0)
        {
            throw new InvalidOperationException("Image-to-3D fallback produced no model URLs.");
        }

        await CacheTaskOutputsAsync(
            target,
            "Meshy image-to-3D (Google fallback)",
            taskState,
            googleImageUrls,
            progressCallback,
            cancellationToken);
    }

    private async Task CacheTaskOutputsAsync(
        string target,
        string sourceTitle,
        MeshyTaskState taskState,
        IReadOnlyList<string> fallbackImageUrls,
        Action<string>? progressCallback,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(target);
        var targetCacheFolder = Path.Combine(_cacheRoot, cacheKey);
        if (Directory.Exists(targetCacheFolder))
        {
            Directory.Delete(targetCacheFolder, recursive: true);
        }

        var imageCacheFolder = Path.Combine(targetCacheFolder, "images");
        var modelCacheFolder = Path.Combine(targetCacheFolder, "models");
        Directory.CreateDirectory(imageCacheFolder);
        Directory.CreateDirectory(modelCacheFolder);

        var cachedImages = new List<CachedImageItem>();
        var cachedModels = new List<CachedModelItem>();

        var imageUrls = taskState.ImageUrls.Count > 0 ? taskState.ImageUrls : fallbackImageUrls;
        var imageCounter = 0;
        foreach (var imageUrl in imageUrls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (imageCounter >= 6)
            {
                break;
            }

            progressCallback?.Invoke($"Meshy download image ({target}) [{imageCounter + 1}]: {imageUrl}");
            var imageAsset = await DownloadAssetAsync(imageUrl, maxBytes: 4_500_000, requireImageContentType: true, cancellationToken);
            if (imageAsset is null)
            {
                progressCallback?.Invoke($"Meshy image download skipped ({target}): unable to fetch '{imageUrl}'.");
                continue;
            }

            imageCounter += 1;
            var imageFileName = $"img_{imageCounter:00}{ResolveImageExtension(imageUrl, imageAsset.MimeType)}";
            var imagePath = Path.Combine(imageCacheFolder, imageFileName);
            await File.WriteAllBytesAsync(imagePath, imageAsset.Bytes, cancellationToken);
            progressCallback?.Invoke($"Meshy image cached ({target}): {imageFileName} ({imageAsset.Bytes.Length} bytes).");
            cachedImages.Add(
                new CachedImageItem(
                    sourceTitle,
                    imageUrl,
                    imageFileName,
                    imageAsset.MimeType));
        }

        var modelCounter = 0;
        foreach (var modelUrl in taskState.ModelUrls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (modelCounter >= 4)
            {
                break;
            }

            progressCallback?.Invoke($"Meshy download model ({target}) [{modelCounter + 1}]: {modelUrl}");
            var modelAsset = await DownloadAssetAsync(modelUrl, maxBytes: 200_000_000, requireImageContentType: false, cancellationToken);
            if (modelAsset is null)
            {
                progressCallback?.Invoke($"Meshy model download skipped ({target}): unable to fetch '{modelUrl}'.");
                continue;
            }

            var modelExtension = ResolveModelExtension(modelUrl, modelAsset.MimeType);
            if (!IsSupportedModelExtension(modelExtension))
            {
                progressCallback?.Invoke($"Meshy model skipped ({target}): unsupported extension '{modelExtension}' from '{modelUrl}'.");
                continue;
            }

            modelCounter += 1;
            var modelFileName = $"model_{modelCounter:00}{modelExtension}";
            var modelPath = Path.Combine(modelCacheFolder, modelFileName);
            await File.WriteAllBytesAsync(modelPath, modelAsset.Bytes, cancellationToken);
            progressCallback?.Invoke($"Meshy model cached ({target}): {modelFileName} ({modelAsset.Bytes.Length} bytes).");
            cachedModels.Add(
                new CachedModelItem(
                    sourceTitle,
                    modelUrl,
                    modelFileName,
                    modelAsset.MimeType));
        }

        if (cachedModels.Count == 0)
        {
            throw new InvalidOperationException("No supported GLB/GLTF model files were cached from Meshy output.");
        }

        var manifest = new CachedTargetManifest(
            target,
            cacheKey,
            DateTimeOffset.UtcNow,
            cachedImages,
            cachedModels);
        var manifestPath = Path.Combine(targetCacheFolder, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(manifest, CacheSerializerOptions);
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        InvalidateCachedMeshesSnapshot();
        progressCallback?.Invoke($"Meshy cache manifest written ({target}): {manifestPath}");
    }

    private async Task<CachedTargetData?> TryLoadCachedTargetAsync(string target, CancellationToken cancellationToken)
    {
        var targetCacheFolder = Path.Combine(_cacheRoot, BuildCacheKey(target));
        var manifestPath = Path.Combine(targetCacheFolder, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<CachedTargetManifest>(manifestJson, CacheSerializerOptions);
            if (manifest is null)
            {
                return null;
            }

            var images = new List<PointOfInterestImage>();
            foreach (var image in manifest.Images)
            {
                var imagePath = Path.Combine(targetCacheFolder, "images", image.FileName);
                if (!File.Exists(imagePath))
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var mimeType = string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType;
                images.Add(
                    new PointOfInterestImage(
                        manifest.TargetName,
                        string.IsNullOrWhiteSpace(image.SourceTitle) ? "Meshy" : image.SourceTitle,
                        image.SourceUrl,
                        BuildDataUri(mimeType, bytes)));
            }

            var meshes = new List<PointOfInterestMesh>();
            foreach (var model in manifest.Models)
            {
                var modelPath = Path.Combine(targetCacheFolder, "models", model.FileName);
                if (!File.Exists(modelPath))
                {
                    continue;
                }

                var relativePath = BuildModelRelativePath(manifest.CacheKey, model.FileName);
                meshes.Add(
                    new PointOfInterestMesh(
                        manifest.TargetName,
                        string.IsNullOrWhiteSpace(model.SourceTitle) ? "Meshy" : model.SourceTitle,
                        model.SourceUrl,
                        relativePath,
                        BuildLocalWebUrl(relativePath),
                        model.MimeType));
            }

            if (images.Count == 0 && meshes.Count == 0)
            {
                return null;
            }

            return new CachedTargetData(images, meshes);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> SearchGoogleImageUrlsAsync(string query, CancellationToken cancellationToken)
    {
        var requestUri = $"https://www.google.com/search?tbm=isch&hl=en&q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<string>();
        }

        var urls = new List<string>();
        foreach (Match match in GoogleImageUrlRegex.Matches(html))
        {
            var candidate = match.Value
                .Replace("\\u003d", "=", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("\\/", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

            if (!LooksLikeUrl(candidate))
            {
                continue;
            }

            if (!IsLikelyImageUrl(candidate))
            {
                continue;
            }

            urls.Add(candidate);
            if (urls.Count >= 12)
            {
                break;
            }
        }

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private async Task<DownloadedAsset?> DownloadAssetAsync(
        string assetUrl,
        int maxBytes,
        bool requireImageContentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("*/*");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
        if (requireImageContentType &&
            (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0 || bytes.Length > maxBytes)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(mimeType))
        {
            mimeType = requireImageContentType ? "image/png" : "application/octet-stream";
        }

        return new DownloadedAsset(assetUrl, mimeType, bytes);
    }

    private async Task<string> SubmitTaskAsync(
        string endpoint,
        Dictionary<string, object?> payload,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Meshy submit failed ({(int)response.StatusCode}): {Truncate(body, 220)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        string? taskId = null;
        if (json.RootElement.TryGetProperty("result", out var resultElement) &&
            resultElement.ValueKind == JsonValueKind.String)
        {
            taskId = resultElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(taskId) &&
            json.RootElement.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String)
        {
            taskId = idElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new InvalidOperationException("Meshy submit response did not include task id.");
        }

        return taskId.Trim();
    }

    private async Task<MeshyTaskState> WaitForTaskCompletionAsync(
        string endpoint,
        string apiKey,
        string taskId,
        string target,
        CancellationToken cancellationToken,
        Action<string>? progressCallback)
    {
        string? lastStatus = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskState = await GetTaskStateAsync(endpoint, apiKey, taskId, cancellationToken);
            if (!string.Equals(taskState.Status, lastStatus, StringComparison.OrdinalIgnoreCase))
            {
                progressCallback?.Invoke($"Meshy task {taskId} ({target}) status: {taskState.Status}");
                lastStatus = taskState.Status;
            }

            if (SuccessfulStatuses.Contains(taskState.Status))
            {
                progressCallback?.Invoke(
                    $"Meshy task {taskId} ({target}) completed with {taskState.ModelUrls.Count} model url(s) and {taskState.ImageUrls.Count} image url(s).");
                return taskState;
            }

            if (FailedStatuses.Contains(taskState.Status))
            {
                var errorText = string.IsNullOrWhiteSpace(taskState.ErrorMessage)
                    ? $"Task ended with status {taskState.Status}."
                    : taskState.ErrorMessage;
                progressCallback?.Invoke($"Meshy task {taskId} ({target}) failed: {Truncate(errorText, 220)}");
                throw new InvalidOperationException(errorText);
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<MeshyTaskState> GetTaskStateAsync(
        string endpoint,
        string apiKey,
        string taskId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/{Uri.EscapeDataString(taskId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Meshy task query failed ({(int)response.StatusCode}): {Truncate(body, 220)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var resultElement = json.RootElement;
        if (json.RootElement.TryGetProperty("result", out var wrappedResultElement) &&
            wrappedResultElement.ValueKind == JsonValueKind.Object)
        {
            resultElement = wrappedResultElement;
        }

        if (resultElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Meshy task response was not an object.");
        }

        var status = TryGetString(resultElement, "status") ?? "UNKNOWN";
        var taskErrorMessage = TryGetNestedString(resultElement, "task_error", "message") ??
                               TryGetNestedString(resultElement, "task_error", "detail") ??
                               TryGetString(json.RootElement, "message") ??
                               string.Empty;

        var models = new List<string>();
        var images = new List<string>();
        ExtractModelUrls(resultElement, models);
        ExtractImageUrls(resultElement, images);
        if (resultElement.TryGetProperty("output", out var outputElement) &&
            outputElement.ValueKind == JsonValueKind.Object)
        {
            ExtractModelUrls(outputElement, models);
            ExtractImageUrls(outputElement, images);
        }

        return new MeshyTaskState(
            status,
            taskErrorMessage,
            models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            images.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private void LoadFailedPoiTargets()
    {
        lock (_failedPoiTargetsSync)
        {
            _failedPoiTargets.Clear();
            if (!File.Exists(_failedPoiTargetsPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_failedPoiTargetsPath);
                var targets = JsonSerializer.Deserialize<string[]>(json, CacheSerializerOptions);
                if (targets is null)
                {
                    return;
                }

                foreach (var target in targets)
                {
                    var key = NormalizeTargetKey(target);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _failedPoiTargets.Add(key);
                    }
                }
            }
            catch
            {
                // Ignore malformed skip list.
            }
        }
    }

    private void LoadPoiNameOverrides()
    {
        lock (_poiNameOverridesSync)
        {
            _poiNameOverrides.Clear();
            if (!File.Exists(_poiNameOverridesPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_poiNameOverridesPath);
                var persisted = JsonSerializer.Deserialize<Dictionary<string, string>>(json, CacheSerializerOptions);
                if (persisted is null)
                {
                    return;
                }

                foreach (var item in persisted)
                {
                    var key = NormalizeTargetKey(item.Key);
                    var value = item.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    _poiNameOverrides[key] = value;
                }
            }
            catch
            {
                // Ignore malformed POI override file.
            }
        }
    }

    private bool IsTargetMarkedToSkip(string target)
    {
        var key = NormalizeTargetKey(target);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_failedPoiTargetsSync)
        {
            return _failedPoiTargets.Contains(key);
        }
    }

    public bool ClearTargetSkipMark(string target)
    {
        var key = NormalizeTargetKey(target);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_failedPoiTargetsSync)
        {
            var removed = _failedPoiTargets.Remove(key);
            if (removed)
            {
                PersistFailedPoiTargetsUnsafe();
            }

            return removed;
        }
    }

    private void MarkTargetToSkip(string target, Action<string>? progressCallback)
    {
        var key = NormalizeTargetKey(target);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var wasAdded = false;
        lock (_failedPoiTargetsSync)
        {
            wasAdded = _failedPoiTargets.Add(key);
            if (wasAdded)
            {
                PersistFailedPoiTargetsUnsafe();
            }
        }

        if (wasAdded)
        {
            progressCallback?.Invoke(
                $"Meshy target '{target}' marked to skip after failure.");
        }
    }

    private void PersistFailedPoiTargetsUnsafe()
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var payload = _failedPoiTargets
                .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var json = JsonSerializer.Serialize(payload, CacheSerializerOptions);
            File.WriteAllText(_failedPoiTargetsPath, json);
        }
        catch
        {
            // Ignore persistence failures for skip list.
        }
    }

    private void PersistPoiNameOverridesUnsafe()
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var payload = _poiNameOverrides
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(payload, CacheSerializerOptions);
            File.WriteAllText(_poiNameOverridesPath, json);
        }
        catch
        {
            // Ignore persistence failures for POI name overrides.
        }
    }

    private void InvalidateCachedMeshesSnapshot()
    {
        lock (_cachedMeshesSync)
        {
            _cachedMeshesSnapshot = null;
            _cachedMeshesSnapshotAtUtc = DateTimeOffset.MinValue;
            _cachedMeshesDirty = true;
        }
    }

    private static string NormalizeTargetKey(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            target
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildCacheKey(string target)
    {
        var trimmed = string.IsNullOrWhiteSpace(target) ? "unknown" : target.Trim();
        var normalizedChars = trimmed
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var normalized = string.Join(
            "-",
            new string(normalizedChars)
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "poi";
        }

        if (normalized.Length > 50)
        {
            normalized = normalized[..50];
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.ToLowerInvariant()));
        var hash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        return $"{normalized}-{hash}";
    }

    private static string BuildModelRelativePath(string cacheKey, string fileName)
    {
        return $"{cacheKey}/models/{fileName}".Replace('\\', '/');
    }

    private static bool TryParseModelRelativePath(
        string localRelativePath,
        out string cacheKey,
        out string modelFileName)
    {
        cacheKey = string.Empty;
        modelFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(localRelativePath))
        {
            return false;
        }

        var normalized = localRelativePath.Trim().Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3)
        {
            return false;
        }

        if (!segments[1].Equals("models", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        cacheKey = segments[0];
        modelFileName = segments[^1];
        return !string.IsNullOrWhiteSpace(cacheKey) && !string.IsNullOrWhiteSpace(modelFileName);
    }

    private static string BuildLocalWebUrl(string relativePath)
    {
        return $"https://{CacheVirtualHostName}/{relativePath.TrimStart('/')}";
    }

    private static string BuildPoiPrompt(string poiName)
    {
        var location = string.IsNullOrWhiteSpace(poiName) ? "location" : poiName.Trim();
        return
            $"Create a clear 45 degree top-down isometric miniature 3D cartoon representation of the {location} with soft refined textures using realistic PBR materials in a 16:9 aspect ratio at high resolution.";
    }

    private static string BuildCustomPromptTargetName(string prompt)
    {
        var normalized = string.Join(
            " ",
            prompt
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var preview = normalized.Length > 56 ? $"{normalized[..56].TrimEnd()}..." : normalized;
        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = "Custom Scene";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..8];
        return $"Custom Prompt - {preview} [{hash}]";
    }

    private static string BuildDataUri(string mimeType, byte[] bytes)
    {
        return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string ResolveImageExtension(string sourceUrl, string mimeType)
    {
        if (mimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (mimeType.Contains("webp", StringComparison.OrdinalIgnoreCase))
        {
            return ".webp";
        }

        if (mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
        {
            return ".jpg";
        }

        return ResolveExtensionFromUrl(sourceUrl, ".png");
    }

    private static string ResolveModelExtension(string sourceUrl, string mimeType)
    {
        if (mimeType.Contains("gltf-binary", StringComparison.OrdinalIgnoreCase))
        {
            return ".glb";
        }

        if (mimeType.Contains("gltf+json", StringComparison.OrdinalIgnoreCase))
        {
            return ".gltf";
        }

        return ResolveExtensionFromUrl(sourceUrl, ".glb");
    }

    private static bool IsSupportedModelExtension(string extension)
    {
        return extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyImageUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        return lower.Contains("gstatic.com", StringComparison.Ordinal) ||
               lower.Contains("googleusercontent.com", StringComparison.Ordinal) ||
               lower.EndsWith(".jpg", StringComparison.Ordinal) ||
               lower.EndsWith(".jpeg", StringComparison.Ordinal) ||
               lower.EndsWith(".png", StringComparison.Ordinal) ||
               lower.EndsWith(".webp", StringComparison.Ordinal) ||
               lower.Contains(".jpg?", StringComparison.Ordinal) ||
               lower.Contains(".jpeg?", StringComparison.Ordinal) ||
               lower.Contains(".png?", StringComparison.Ordinal) ||
               lower.Contains(".webp?", StringComparison.Ordinal);
    }

    private static string ResolveExtensionFromUrl(string sourceUrl, string fallbackExtension)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return fallbackExtension;
        }

        var extension = Path.GetExtension(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return fallbackExtension;
        }

        var lower = extension.ToLowerInvariant();
        return lower.Length > 8 ? fallbackExtension : lower;
    }

    private static IReadOnlyList<string> BuildTargets(string cityName, IReadOnlyList<string> pointsOfInterest)
    {
        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(cityName))
        {
            targets.Add(cityName.Trim());
        }

        foreach (var poi in pointsOfInterest
                     .Where(point => !string.IsNullOrWhiteSpace(point))
                     .Select(point => point.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            targets.Add(poi);
        }

        return targets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ExtractModelUrls(JsonElement outputElement, List<string> destination)
    {
        if (outputElement.TryGetProperty("model_urls", out var modelUrlsElement))
        {
            ExtractUrlsRecursive(modelUrlsElement, destination, IsLikelyModelUrl);
        }

        if (outputElement.TryGetProperty("model_url", out var singleModelUrl))
        {
            ExtractUrlsRecursive(singleModelUrl, destination, IsLikelyModelUrl);
        }

        if (outputElement.TryGetProperty("output", out var nestedOutput))
        {
            ExtractModelUrls(nestedOutput, destination);
        }
    }

    private static void ExtractImageUrls(JsonElement outputElement, List<string> destination)
    {
        if (outputElement.TryGetProperty("thumbnail_url", out var thumbnailElement))
        {
            ExtractUrlsRecursive(thumbnailElement, destination, LooksLikeUrl);
        }

        if (outputElement.TryGetProperty("image_urls", out var imageUrlsElement))
        {
            ExtractUrlsRecursive(imageUrlsElement, destination, LooksLikeUrl);
        }

        if (outputElement.TryGetProperty("texture_urls", out var textureUrlsElement))
        {
            ExtractUrlsRecursive(textureUrlsElement, destination, LooksLikeUrl);
        }

        if (outputElement.TryGetProperty("texture_url", out var textureUrlElement))
        {
            ExtractUrlsRecursive(textureUrlElement, destination, LooksLikeUrl);
        }

        if (outputElement.TryGetProperty("output", out var nestedOutput))
        {
            ExtractImageUrls(nestedOutput, destination);
        }
    }

    private static void ExtractUrlsRecursive(
        JsonElement element,
        List<string> destination,
        Func<string?, bool> predicate)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var value = element.GetString();
                if (predicate(value))
                {
                    destination.Add(value!);
                }

                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ExtractUrlsRecursive(item, destination, predicate);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ExtractUrlsRecursive(property.Value, destination, predicate);
                }

                break;
        }
    }

    private static bool IsLikelyModelUrl(string? value)
    {
        if (!LooksLikeUrl(value))
        {
            return false;
        }

        var lower = value!.ToLowerInvariant();
        return lower.Contains(".glb", StringComparison.Ordinal) ||
               lower.Contains(".gltf", StringComparison.Ordinal) ||
               lower.Contains(".obj", StringComparison.Ordinal) ||
               lower.Contains(".fbx", StringComparison.Ordinal) ||
               lower.Contains(".usdz", StringComparison.Ordinal);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static string? TryGetNestedString(JsonElement parent, string childName, string nestedName)
    {
        return parent.TryGetProperty(childName, out var childElement) &&
               childElement.ValueKind == JsonValueKind.Object &&
               childElement.TryGetProperty(nestedName, out var nestedElement) &&
               nestedElement.ValueKind == JsonValueKind.String
            ? nestedElement.GetString()
            : null;
    }

    private static bool LooksLikeUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text.Trim().Replace(Environment.NewLine, " ");
        return cleaned.Length <= maxLength ? cleaned : $"{cleaned[..maxLength]}...";
    }

    private sealed record MeshyTaskState(
        string Status,
        string ErrorMessage,
        IReadOnlyList<string> ModelUrls,
        IReadOnlyList<string> ImageUrls);

    private sealed record DownloadedAsset(
        string SourceUrl,
        string MimeType,
        byte[] Bytes);

    private sealed record CachedImageItem(
        string SourceTitle,
        string SourceUrl,
        string FileName,
        string MimeType);

    private sealed record CachedModelItem(
        string SourceTitle,
        string SourceUrl,
        string FileName,
        string MimeType);

    private sealed record CachedTargetManifest(
        string TargetName,
        string CacheKey,
        DateTimeOffset CachedAtUtc,
        IReadOnlyList<CachedImageItem> Images,
        IReadOnlyList<CachedModelItem> Models);

    private sealed record CachedTargetData(
        IReadOnlyList<PointOfInterestImage> Images,
        IReadOnlyList<PointOfInterestMesh> Meshes);
}
