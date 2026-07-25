using System.Text;
using Snapshot.Protocol.Output;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Hosting.Netlify;

public sealed record NetlifyArtifacts(string Headers, string Redirects);

public static class NetlifyArtifactGenerator
{
    private const string BeginMarker = "# BEGIN Snapshot Protocol";
    private const string EndMarker = "# END Snapshot Protocol";

    public static NetlifyArtifacts Generate(
        string sourceDirectory,
        IReadOnlyList<SnapshotRoute> canonicalRoutes,
        IReadOnlyList<SnapshotGeneratedEntry> generatedEntries)
    {
        var headersBlock = BuildHeadersBlock(canonicalRoutes, generatedEntries);
        var redirectsBlock = BuildRedirectsBlock(canonicalRoutes, generatedEntries);

        var existingHeaders = ReadIfExists(Path.Combine(sourceDirectory, "_headers"));
        var existingRedirects = ReadIfExists(Path.Combine(sourceDirectory, "_redirects"));

        return new NetlifyArtifacts(
            MergeGeneratedBlock(existingHeaders, headersBlock),
            MergeGeneratedBlock(existingRedirects, redirectsBlock));
    }

    public static string MergeGeneratedBlock(string existing, string generatedBody)
    {
        var start = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);

        if (start >= 0 && end >= start)
        {
            end += EndMarker.Length;
            existing = (existing[..start] + existing[end..]).TrimEnd();
        }

        var block = $"{BeginMarker}{Environment.NewLine}{generatedBody.TrimEnd()}{Environment.NewLine}{EndMarker}";
        return string.IsNullOrWhiteSpace(existing)
            ? block + Environment.NewLine
            : existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
    }

    private static string BuildHeadersBlock(
        IReadOnlyList<SnapshotRoute> canonicalRoutes,
        IReadOnlyList<SnapshotGeneratedEntry> generatedEntries)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/",
            "/index.html",
            "/index/"
        };

        foreach (var route in canonicalRoutes)
        {
            paths.Add(WithTrailingSlash(route.Path));
        }

        foreach (var entry in generatedEntries.Where(static entry => entry.Route is not null))
        {
            paths.Add(WithTrailingSlash(entry.Route!));
        }

        var builder = new StringBuilder();
        foreach (var path in paths.OrderBy(static path => path, StringComparer.Ordinal))
        {
            builder.AppendLine(path)
                .AppendLine("  Cache-Control: no-store")
                .AppendLine("  ETag: \"\"")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildRedirectsBlock(
        IReadOnlyList<SnapshotRoute> canonicalRoutes,
        IReadOnlyList<SnapshotGeneratedEntry> generatedEntries)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var route in canonicalRoutes.Where(static route => route.Path != "/"))
        {
            rules.Add($"{route.Path}  {WithTrailingSlash(route.Path)}  200!");
        }

        foreach (var alias in generatedEntries.Where(static entry => entry.Kind == SnapshotGeneratedEntryKind.CaseAlias))
        {
            rules.Add($"{alias.Route}  {WithTrailingSlash(alias.CanonicalRoute!)}  200!");
            rules.Add($"{WithTrailingSlash(alias.Route!)}  {WithTrailingSlash(alias.CanonicalRoute!)}  200!");
        }

        return string.Join(Environment.NewLine, rules.OrderBy(static rule => rule, StringComparer.Ordinal)) + Environment.NewLine;
    }

    private static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    private static string WithTrailingSlash(string path) => path == "/" || path.EndsWith('/') ? path : path + '/';
}
