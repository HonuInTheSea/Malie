namespace Malie.Models;

public sealed record PointOfInterestMesh(
    string Name,
    string SourceTitle,
    string SourceUrl,
    string LocalRelativePath,
    string LocalWebUrl,
    string MimeType);
