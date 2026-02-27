namespace Malie.Models;

public sealed record MeshyGenerationResult(
    bool UsedMeshy,
    IReadOnlyList<PointOfInterestImage> GeneratedReferences,
    IReadOnlyList<PointOfInterestMesh> GeneratedMeshes,
    IReadOnlyList<string> ModelUrls,
    string Message);
