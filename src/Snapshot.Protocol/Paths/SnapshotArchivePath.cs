namespace Snapshot.Protocol.Paths;

public readonly record struct SnapshotArchivePath
{
    public SnapshotArchivePath(string value)
    {
        Value = Normalize(value);
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static SnapshotArchivePath FromRoute(string route)
    {
        var normalized = route.Trim();
        if (normalized == "/")
        {
            return new SnapshotArchivePath("index/index.html");
        }

        normalized = normalized.Trim('/');
        return new SnapshotArchivePath($"{normalized}/index.html");
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Archive paths must identify a file or directory.", nameof(value));
        }

        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                throw new ArgumentException("Archive paths may not contain traversal segments.", nameof(value));
            }

            if (part.Contains('\0'))
            {
                throw new ArgumentException("Archive paths may not contain null characters.", nameof(value));
            }
        }

        return string.Join('/', parts);
    }
}
