using System.IO.Compression;
using System.Xml.Linq;
using Snapshot.Protocol.Diagnostics;

namespace Snapshot.Protocol.Routes;

public sealed class SitemapRouteDiscoverer
{
    public async Task<(IReadOnlyList<SnapshotRoute> Routes, IReadOnlyList<SnapshotDiagnostic> Diagnostics)> DiscoverAsync(
        string sourceDirectory,
        SnapshotRouteDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var routes = new List<SnapshotRoute>();
        var diagnostics = new List<SnapshotDiagnostic>();

        if (options.Mode is SnapshotRouteDiscoveryMode.SitemapsAndExplicit or SnapshotRouteDiscoveryMode.SitemapsOnly)
        {
            foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                         .Where(IsXmlCandidate)
                         .OrderBy(static path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ParseCandidateAsync(path, routes, diagnostics, cancellationToken).ConfigureAwait(false);
            }
        }

        if (options.Mode is SnapshotRouteDiscoveryMode.SitemapsAndExplicit or SnapshotRouteDiscoveryMode.ExplicitOnly)
        {
            foreach (var route in options.AdditionalRoutes)
            {
                TryAddRoute(route, "explicit route", routes, diagnostics);
            }
        }

        var exact = new Dictionary<string, SnapshotRoute>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            exact.TryAdd(route.Path, route);
        }

        var caseGroups = exact.Values
            .GroupBy(static route => route.Path, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Select(route => route.Path).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToArray();

        foreach (var group in caseGroups)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CaseConflict,
                SnapshotDiagnosticSeverity.Error,
                $"Routes differ only by casing: {string.Join(", ", group.Select(route => route.Path))}",
                Suggestion: "Use one exact casing for each route. Lowercase routes are strongly recommended."));
        }

        foreach (var route in exact.Values.Where(static route => route.ContainsUppercase))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.UppercaseRoute,
                SnapshotDiagnosticSeverity.Warning,
                "The canonical route contains uppercase characters. Exact casing will be preserved and compatibility aliases can be generated.",
                route.Path,
                route.Source,
                route.OutputPath.Value,
                "Prefer lowercase URLs when possible."));
        }

        if (exact.Count == 0)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.NoRoutes,
                SnapshotDiagnosticSeverity.Error,
                "No <urlset> sitemap routes or explicit routes were discovered."));
        }

        return (exact.Values.OrderBy(static route => route.Path, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    private static bool IsXmlCandidate(string path) =>
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase);

    private static async Task ParseCandidateAsync(
        string path,
        ICollection<SnapshotRoute> routes,
        ICollection<SnapshotDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(path);
            await using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false)
                : file;

            var document = await XDocument.LoadAsync(input, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var root = document.Root;
            if (root is null)
            {
                return;
            }

            if (root.Name.LocalName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!root.Name.LocalName.Equals("urlset", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var location in root.Descendants().Where(static node => node.Name.LocalName == "loc"))
            {
                TryAddRoute(location.Value, path, routes, diagnostics);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or IOException)
        {
            if (Path.GetFileName(path).Contains("sitemap", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SitemapInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    $"Could not parse sitemap candidate: {exception.Message}",
                    Source: path));
            }
        }
    }

    private static void TryAddRoute(
        string value,
        string source,
        ICollection<SnapshotRoute> routes,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        try
        {
            routes.Add(SnapshotRoute.Parse(value, source));
        }
        catch (SnapshotRouteException exception)
        {
            var code = value.Contains('?')
                ? SnapshotDiagnosticCodes.QueryRouteUnsupported
                : SnapshotDiagnosticCodes.InvalidRoute;

            diagnostics.Add(new SnapshotDiagnostic(
                code,
                SnapshotDiagnosticSeverity.Error,
                exception.Message,
                value,
                source));
        }
    }
}
