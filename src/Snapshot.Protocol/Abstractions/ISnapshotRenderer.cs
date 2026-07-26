using Snapshot.Protocol.Build;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Abstractions;

public interface ISnapshotRenderer
{
    IAsyncEnumerable<SnapshotRenderResult> RenderAsync(
        SnapshotRenderRequest request,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record SnapshotRenderRequest(
    string SourceDirectory,
    IReadOnlyList<SnapshotRoute> Routes,
    SnapshotTimeoutOptions Timeouts,
    SnapshotRetryOptions Retry,
    int? Concurrency);

public sealed record SnapshotRenderResult(
    SnapshotRoute Route,
    bool Succeeded,
    string? Html,
    int Attempts,
    TimeSpan Elapsed,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? ConsoleErrors = null,
    IReadOnlyList<string>? FailedRequests = null);
