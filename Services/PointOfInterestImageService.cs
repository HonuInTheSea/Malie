using System.Net.Http;
using System.Text.Json;
using Malie.Models;

namespace Malie.Services;

public sealed class PointOfInterestImageService
{
    private static readonly TimeSpan ImageCacheTtl = TimeSpan.FromMinutes(30);
    private const int PoiResolveParallelism = 4;

    private readonly HttpClient _httpClient;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, PoiImageCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PointOfInterestImageService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(35);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Malie/1.0");
    }

    public async Task<IReadOnlyList<PointOfInterestImage>> GetReferenceImagesAsync(
        IReadOnlyList<string> pointsOfInterest,
        CancellationToken cancellationToken)
    {
        if (pointsOfInterest.Count == 0)
        {
            return Array.Empty<PointOfInterestImage>();
        }

        var images = new List<PointOfInterestImage>();
        var missingPoints = new List<string>();
        var normalizedPoints = pointsOfInterest
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        foreach (var pointOfInterest in normalizedPoints)
        {
            if (TryGetCachedImages(pointOfInterest, out var cachedImages))
            {
                images.AddRange(cachedImages);
                continue;
            }

            missingPoints.Add(pointOfInterest);
        }

        if (missingPoints.Count == 0)
        {
            return images;
        }

        using var semaphore = new SemaphoreSlim(PoiResolveParallelism, PoiResolveParallelism);
        var resolveTasks = missingPoints
            .Select(async pointOfInterest =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var resolved = await TryResolveImagesAsync(pointOfInterest, cancellationToken);
                    return new PoiImageResolveResult(pointOfInterest, resolved);
                }
                finally
                {
                    semaphore.Release();
                }
            })
            .ToArray();
        var resolvedResults = await Task.WhenAll(resolveTasks);

        foreach (var result in resolvedResults)
        {
            if (result.Images.Count == 0)
            {
                continue;
            }

            CacheImages(result.PointOfInterest, result.Images);
            images.AddRange(result.Images);
        }

        return images;
    }

    private async Task<IReadOnlyList<PointOfInterestImage>> TryResolveImagesAsync(string pointOfInterest, CancellationToken cancellationToken)
    {
        try
        {
            var sourceTitles = await ResolveWikipediaTitlesAsync(pointOfInterest, cancellationToken);
            if (sourceTitles.Count == 0)
            {
                return Array.Empty<PointOfInterestImage>();
            }

            var resolved = new List<PointOfInterestImage>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceTitle in sourceTitles.Take(3))
            {
                var summaryTask = ResolveSummaryThumbnailUrlAsync(sourceTitle, cancellationToken);
                var pageTask = ResolvePageThumbnailUrlAsync(sourceTitle, cancellationToken);
                var galleryTask = ResolveGalleryImageUrlsAsync(sourceTitle, cancellationToken);
                var commonsTask = ResolveCommonsSearchImageUrlsAsync(sourceTitle, cancellationToken);
                await Task.WhenAll(summaryTask, pageTask, galleryTask, commonsTask);

                var candidateUrls = new List<string>();
                if (!string.IsNullOrWhiteSpace(summaryTask.Result))
                {
                    candidateUrls.Add(summaryTask.Result!);
                }

                if (!string.IsNullOrWhiteSpace(pageTask.Result))
                {
                    candidateUrls.Add(pageTask.Result!);
                }

                candidateUrls.AddRange(galleryTask.Result);
                candidateUrls.AddRange(commonsTask.Result);

                var uniqueUrls = candidateUrls
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(14)
                    .ToArray();

                foreach (var imageUrl in uniqueUrls)
                {
                    if (!seenUrls.Add(imageUrl))
                    {
                        continue;
                    }

                    var dataUri = await DownloadAsDataUriAsync(imageUrl, cancellationToken);
                    if (string.IsNullOrWhiteSpace(dataUri))
                    {
                        continue;
                    }

                    resolved.Add(new PointOfInterestImage(pointOfInterest, sourceTitle, imageUrl, dataUri));
                    if (resolved.Count >= 4)
                    {
                        return resolved;
                    }
                }
            }

            return resolved;
        }
        catch
        {
            return Array.Empty<PointOfInterestImage>();
        }
    }

    private async Task<IReadOnlyList<string>> ResolveWikipediaTitlesAsync(string query, CancellationToken cancellationToken)
    {
        var url =
            $"https://en.wikipedia.org/w/api.php?action=opensearch&search={Uri.EscapeDataString(query)}&limit=3&namespace=0&format=json";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() < 2)
        {
            return Array.Empty<string>();
        }

        var titles = document.RootElement[1];
        if (titles.ValueKind != JsonValueKind.Array || titles.GetArrayLength() == 0)
        {
            return Array.Empty<string>();
        }

        var resolvedTitles = new List<string>();
        foreach (var titleElement in titles.EnumerateArray())
        {
            var title = titleElement.GetString();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            resolvedTitles.Add(title.Trim());
            if (resolvedTitles.Count >= 3)
            {
                break;
            }
        }

        return resolvedTitles;
    }

    private async Task<string?> ResolveSummaryThumbnailUrlAsync(string title, CancellationToken cancellationToken)
    {
        var summaryUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";
        using var summaryResponse = await _httpClient.GetAsync(summaryUrl, cancellationToken);
        if (!summaryResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var summaryStream = await summaryResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var summaryDocument = await JsonDocument.ParseAsync(summaryStream, cancellationToken: cancellationToken);
        if (!summaryDocument.RootElement.TryGetProperty("thumbnail", out var thumbnailElement) ||
            thumbnailElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!thumbnailElement.TryGetProperty("source", out var sourceElement))
        {
            return null;
        }

        var source = sourceElement.GetString();
        return string.IsNullOrWhiteSpace(source) ? null : source;
    }

    private async Task<string?> ResolvePageThumbnailUrlAsync(string title, CancellationToken cancellationToken)
    {
        var url =
            $"https://en.wikipedia.org/w/api.php?action=query&titles={Uri.EscapeDataString(title)}&prop=pageimages&format=json&pithumbsize=640";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var page in pages.EnumerateObject())
        {
            if (!page.Value.TryGetProperty("thumbnail", out var thumbnail) ||
                thumbnail.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!thumbnail.TryGetProperty("source", out var sourceProperty))
            {
                continue;
            }

            var source = sourceProperty.GetString();
            if (!string.IsNullOrWhiteSpace(source))
            {
                return source;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> ResolveGalleryImageUrlsAsync(string title, CancellationToken cancellationToken)
    {
        var listUrl =
            $"https://en.wikipedia.org/w/api.php?action=query&titles={Uri.EscapeDataString(title)}&prop=images&imlimit=24&format=json";

        using var listResponse = await _httpClient.GetAsync(listUrl, cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        await using var listStream = await listResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var listDocument = await JsonDocument.ParseAsync(listStream, cancellationToken: cancellationToken);

        if (!listDocument.RootElement.TryGetProperty("query", out var queryElement) ||
            !queryElement.TryGetProperty("pages", out var pagesElement) ||
            pagesElement.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        var resolvedUrls = new List<string>();
        foreach (var pageElement in pagesElement.EnumerateObject())
        {
            if (!pageElement.Value.TryGetProperty("images", out var imagesElement) ||
                imagesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var imageElement in imagesElement.EnumerateArray())
            {
                if (!imageElement.TryGetProperty("title", out var imageTitleProperty))
                {
                    continue;
                }

                var imageTitle = imageTitleProperty.GetString();
                if (string.IsNullOrWhiteSpace(imageTitle) || !IsSuitableImageTitle(imageTitle))
                {
                    continue;
                }

                var imageInfoUrl =
                    $"https://en.wikipedia.org/w/api.php?action=query&titles={Uri.EscapeDataString(imageTitle)}&prop=imageinfo&iiprop=url&iiurlwidth=640&format=json";

                using var imageInfoResponse = await _httpClient.GetAsync(imageInfoUrl, cancellationToken);
                if (!imageInfoResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var imageInfoStream = await imageInfoResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var imageInfoDocument = await JsonDocument.ParseAsync(imageInfoStream, cancellationToken: cancellationToken);
                var resolvedUrl = TryReadImageInfoUrl(imageInfoDocument);
                if (string.IsNullOrWhiteSpace(resolvedUrl))
                {
                    continue;
                }

                resolvedUrls.Add(resolvedUrl);
                if (resolvedUrls.Count >= 8)
                {
                    break;
                }
            }

            if (resolvedUrls.Count >= 8)
            {
                break;
            }
        }

        return resolvedUrls;
    }

    private async Task<IReadOnlyList<string>> ResolveCommonsSearchImageUrlsAsync(string title, CancellationToken cancellationToken)
    {
        var url =
            $"https://commons.wikimedia.org/w/api.php?action=query&generator=search&gsrsearch={Uri.EscapeDataString(title)}&gsrnamespace=6&gsrlimit=10&prop=imageinfo&iiprop=url&iiurlwidth=720&format=json";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
            !queryElement.TryGetProperty("pages", out var pagesElement) ||
            pagesElement.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        var urls = new List<string>();
        foreach (var page in pagesElement.EnumerateObject())
        {
            if (!page.Value.TryGetProperty("imageinfo", out var imageInfoArray) ||
                imageInfoArray.ValueKind != JsonValueKind.Array ||
                imageInfoArray.GetArrayLength() == 0)
            {
                continue;
            }

            var imageInfo = imageInfoArray[0];
            string? candidateUrl = null;

            if (imageInfo.TryGetProperty("thumburl", out var thumbProperty))
            {
                candidateUrl = thumbProperty.GetString();
            }

            if (string.IsNullOrWhiteSpace(candidateUrl) &&
                imageInfo.TryGetProperty("url", out var fullUrlProperty))
            {
                candidateUrl = fullUrlProperty.GetString();
            }

            if (string.IsNullOrWhiteSpace(candidateUrl))
            {
                continue;
            }

            if (!IsSuitableImageUrl(candidateUrl))
            {
                continue;
            }

            urls.Add(candidateUrl);
            if (urls.Count >= 10)
            {
                break;
            }
        }

        return urls;
    }

    private static string? TryReadImageInfoUrl(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var page in pages.EnumerateObject())
        {
            var pageValue = page.Value;
            if (!pageValue.TryGetProperty("imageinfo", out var imageInfo) ||
                imageInfo.ValueKind != JsonValueKind.Array ||
                imageInfo.GetArrayLength() == 0)
            {
                continue;
            }

            var firstImageInfo = imageInfo[0];
            if (firstImageInfo.TryGetProperty("thumburl", out var thumbUrlProperty))
            {
                var thumbUrl = thumbUrlProperty.GetString();
                if (!string.IsNullOrWhiteSpace(thumbUrl))
                {
                    return thumbUrl;
                }
            }

            if (firstImageInfo.TryGetProperty("url", out var urlProperty))
            {
                var url = urlProperty.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        return null;
    }

    private static bool IsSuitableImageTitle(string imageTitle)
    {
        var lower = imageTitle.ToLowerInvariant();
        if (!lower.StartsWith("file:", StringComparison.Ordinal))
        {
            return false;
        }

        if (lower.Contains("logo") ||
            lower.Contains("map") ||
            lower.Contains("flag") ||
            lower.Contains("seal") ||
            lower.Contains("coat of arms") ||
            lower.Contains("icon"))
        {
            return false;
        }

        return lower.EndsWith(".jpg", StringComparison.Ordinal) ||
               lower.EndsWith(".jpeg", StringComparison.Ordinal) ||
               lower.EndsWith(".png", StringComparison.Ordinal) ||
               lower.EndsWith(".webp", StringComparison.Ordinal);
    }

    private static bool IsSuitableImageUrl(string imageUrl)
    {
        var lower = imageUrl.ToLowerInvariant();
        if (lower.Contains("logo") ||
            lower.Contains("flag") ||
            lower.Contains("coat_of_arms") ||
            lower.Contains("map"))
        {
            return false;
        }

        return lower.EndsWith(".jpg", StringComparison.Ordinal) ||
               lower.EndsWith(".jpeg", StringComparison.Ordinal) ||
               lower.EndsWith(".png", StringComparison.Ordinal) ||
               lower.EndsWith(".webp", StringComparison.Ordinal);
    }

    private bool TryGetCachedImages(string pointOfInterest, out IReadOnlyList<PointOfInterestImage> images)
    {
        images = Array.Empty<PointOfInterestImage>();
        lock (_cacheSync)
        {
            if (!_cache.TryGetValue(pointOfInterest, out var cacheEntry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - cacheEntry.CachedAtUtc > ImageCacheTtl)
            {
                _cache.Remove(pointOfInterest);
                return false;
            }

            images = cacheEntry.Images;
            return true;
        }
    }

    private void CacheImages(string pointOfInterest, IReadOnlyList<PointOfInterestImage> images)
    {
        lock (_cacheSync)
        {
            _cache[pointOfInterest] = new PoiImageCacheEntry(images, DateTimeOffset.UtcNow);
            if (_cache.Count > 256)
            {
                PruneImageCacheUnsafe();
            }
        }
    }

    private void PruneImageCacheUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _cache
            .Where(pair => now - pair.Value.CachedAtUtc > ImageCacheTtl)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expiredKeys)
        {
            _cache.Remove(key);
        }

        if (_cache.Count <= 256)
        {
            return;
        }

        var oldestKeys = _cache
            .OrderBy(pair => pair.Value.CachedAtUtc)
            .Take(_cache.Count - 256)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in oldestKeys)
        {
            _cache.Remove(key);
        }
    }

    private async Task<string?> DownloadAsDataUriAsync(string imageUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(imageUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (imageBytes.Length == 0 || imageBytes.Length > 2_500_000)
        {
            return null;
        }

        var base64 = Convert.ToBase64String(imageBytes);
        return $"data:{mimeType};base64,{base64}";
    }

    private sealed record PoiImageCacheEntry(
        IReadOnlyList<PointOfInterestImage> Images,
        DateTimeOffset CachedAtUtc);

    private sealed record PoiImageResolveResult(
        string PointOfInterest,
        IReadOnlyList<PointOfInterestImage> Images);
}
