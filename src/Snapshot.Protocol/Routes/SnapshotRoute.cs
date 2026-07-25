using Snapshot.Protocol.Paths;

namespace Snapshot.Protocol.Routes;

public sealed record SnapshotRoute
{
    private SnapshotRoute(string path, string? source)
    {
        Path = path;
        Source = source;
        OutputPath = SnapshotArchivePath.FromRoute(path);
    }

    public string Path { get; }

    public string? Source { get; }

    public SnapshotArchivePath OutputPath { get; }

    public bool ContainsUppercase => Path.Any(char.IsUpper);

    public static SnapshotRoute Parse(string value, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!string.IsNullOrEmpty(absolute.Query))
            {
                throw new SnapshotRouteException("Query-string routes are not supported because multiple URLs can map to the same output file.");
            }

            path = absolute.AbsolutePath;
        }
        else
        {
            var fragmentIndex = value.IndexOf('#', StringComparison.Ordinal);
            if (fragmentIndex >= 0)
            {
                value = value[..fragmentIndex];
            }

            var queryIndex = value.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex >= 0)
            {
                throw new SnapshotRouteException("Query-string routes are not supported because multiple URLs can map to the same output file.");
            }

            path = value;
        }

        path = path.Trim();
        if (path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnapshotRouteException("Encoded path separators are not allowed in routes.");
        }

        path = Uri.UnescapeDataString(path);
        if (!path.StartsWith('/'))
        {
            path = '/' + path;
        }

        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new SnapshotRouteException("Route traversal segments are not allowed.");
            }

            if (segment.Contains('\\'))
            {
                throw new SnapshotRouteException("Encoded path separators are not allowed in routes.");
            }
        }

        return new SnapshotRoute(path, source);
    }
}

public sealed class SnapshotRouteException : Exception
{
    public SnapshotRouteException(string message) : base(message)
    {
    }
}
