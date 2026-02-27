namespace Malie.Models;

public sealed record MeshyModelRowState(
    string PoiKey,
    string PoiName,
    string ModelFileName,
    string StatusText,
    string StatusKind,
    bool IsCachedModel,
    bool IsActiveModel,
    bool CanQueue,
    bool CanDownloadNow,
    bool CanDelete,
    string LocalRelativePath);

public sealed record MeshyManagerStatePayload(
    string Status,
    string QueueStatus,
    bool IsBusy,
    double RotationMinutes,
    double ProgressPercent,
    string ProgressText,
    IReadOnlyList<MeshyModelRowState> Rows,
    IReadOnlyList<string> Logs);

public sealed record MeshyQueueRequest(
    string PoiName,
    bool Prioritize);

public sealed record MeshyRenameRequest(
    string OldName,
    string NewName,
    string LocalRelativePath);

public sealed record MeshyImportRequest(
    string PoiName,
    string FileName,
    string DataUrl);

public sealed record MeshyExportRequest(
    string LocalRelativePath);
