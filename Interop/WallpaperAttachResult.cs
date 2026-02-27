namespace Malie.Interop;

public sealed record WallpaperAttachResult(
    bool IsAttached,
    string Strategy,
    IntPtr ProgmanHandle,
    IntPtr WorkerHandle,
    IntPtr TargetParentHandle,
    IntPtr ActualParentHandle,
    string TargetParentClass,
    string ActualParentClass,
    int LastError,
    int PrimaryScreenX,
    int PrimaryScreenY,
    int PrimaryScreenWidth,
    int PrimaryScreenHeight,
    string Message);
