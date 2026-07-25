namespace Snapshot.Protocol.Diagnostics;

public enum SnapshotDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record SnapshotDiagnostic(
    string Code,
    SnapshotDiagnosticSeverity Severity,
    string Message,
    string? Route = null,
    string? Source = null,
    string? OutputPath = null,
    string? Suggestion = null);
