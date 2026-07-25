namespace Snapshot.Protocol.Build;

public enum SnapshotProgressStage
{
    Validating,
    DiscoveringRoutes,
    Planning,
    StartingBrowser,
    Rendering,
    WritingArchive,
    ValidatingArchive,
    Completed
}

public sealed record SnapshotProgress(
    SnapshotProgressStage Stage,
    string Message,
    int Completed = 0,
    int Total = 0,
    string? Route = null);
