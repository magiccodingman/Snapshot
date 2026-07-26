namespace Snapshot.Processing;

public enum SnapshotCanonicalPolicy
{
    Error,
    Warning,
    Disabled
}

public sealed class SnapshotProcessingOptions
{
    public bool Enabled { get; set; } = true;

    public bool MinifyHtml { get; set; } = true;

    public bool RemoveHtmlComments { get; set; } = true;

    public bool MinifyInlineJson { get; set; } = true;

    public bool ValidateInlineJson { get; set; } = true;

    public bool MinifyInlineCss { get; set; } = true;

    public SnapshotCanonicalPolicy CanonicalPolicy { get; set; } = SnapshotCanonicalPolicy.Error;
}
