using System.IO.Compression;
using System.Xml.Linq;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Metadata;

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
        var declarations = new Dictionary<string, SitemapRepresentationDeclaration>(StringComparer.Ordinal);

        if (options.Mode is SnapshotRouteDiscoveryMode.SitemapsAndExplicit or SnapshotRouteDiscoveryMode.SitemapsOnly)
        {
            foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                         .Where(IsXmlCandidate)
                         .OrderBy(static path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ParseCandidateAsync(path, routes, declarations, diagnostics, cancellationToken).ConfigureAwait(false);
            }
        }

        if (options.Mode is SnapshotRouteDiscoveryMode.SitemapsAndExplicit or SnapshotRouteDiscoveryMode.ExplicitOnly)
        {
            foreach (var value in options.AdditionalRoutes)
            {
                if (!TryParseRoute(value, "explicit route", diagnostics, out var route))
                {
                    continue;
                }

                if (declarations.TryGetValue(route.Path, out var declaration) &&
                    declaration.Representation == SitemapRepresentation.ClientRendered)
                {
                    diagnostics.Add(new SnapshotDiagnostic(
                        SnapshotDiagnosticCodes.SitemapRepresentationConflict,
                        SnapshotDiagnosticSeverity.Error,
                        $"Explicit route {route.Path} conflicts with a sitemap declaration of {SnapshotRepresentationMetadata.ClientRendered}.",
                        route.Path,
                        declaration.Source,
                        route.OutputPath.Value,
                        "Remove the explicit route override or change the sitemap representation declaration."));
                    continue;
                }

                routes.Add(route);
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
                "No eligible <urlset> sitemap routes or explicit routes were discovered."));
        }

        return (exact.Values.OrderBy(static route => route.Path, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    private static bool IsXmlCandidate(string path) =>
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase);

    private static async Task ParseCandidateAsync(
        string path,
        ICollection<SnapshotRoute> routes,
        IDictionary<string, SitemapRepresentationDeclaration> declarations,
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
            if (root is null ||
                root.Name.LocalName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase) ||
                !root.Name.LocalName.Equals("urlset", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var url in root.Elements().Where(static node => node.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase)))
            {
                var location = url.Elements().FirstOrDefault(static node => node.Name.LocalName.Equals("loc", StringComparison.OrdinalIgnoreCase));
                if (location is null || !TryParseRoute(location.Value, path, diagnostics, out var route))
                {
                    continue;
                }

                var representationNodes = url.Elements()
                    .Where(static node =>
                        node.Name.NamespaceName.Equals(SnapshotRepresentationMetadata.XmlNamespaceUri, StringComparison.Ordinal) &&
                        node.Name.LocalName.Equals(SnapshotRepresentationMetadata.RepresentationElementName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (representationNodes.Length > 1)
                {
                    diagnostics.Add(new SnapshotDiagnostic(
                        SnapshotDiagnosticCodes.SitemapRepresentationInvalid,
                        SnapshotDiagnosticSeverity.Error,
                        "A sitemap URL contains multiple rendering representation declarations.",
                        route.Path,
                        path));
                    continue;
                }

                var representation = ParseRepresentation(representationNodes.SingleOrDefault()?.Value, route, path, diagnostics);
                if (representation is null)
                {
                    continue;
                }

                RegisterDeclaration(route, representation.Value, path, declarations, diagnostics);
                if (representation != SitemapRepresentation.ClientRendered)
                {
                    routes.Add(route);
                }
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

    private static SitemapRepresentation? ParseRepresentation(
        string? value,
        SnapshotRoute route,
        string source,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SitemapRepresentation.DefaultPrerender;
        }

        if (SnapshotRepresentationMetadata.IsStaticPrerendered(value))
        {
            return SitemapRepresentation.StaticPrerendered;
        }

        if (SnapshotRepresentationMetadata.IsClientRendered(value))
        {
            return SitemapRepresentation.ClientRendered;
        }

        diagnostics.Add(new SnapshotDiagnostic(
            SnapshotDiagnosticCodes.SitemapRepresentationInvalid,
            SnapshotDiagnosticSeverity.Error,
            $"Unsupported rendering representation '{value.Trim()}'. Supported values are {SnapshotRepresentationMetadata.StaticPrerendered}, {SnapshotRepresentationMetadata.LegacyPrerendered}, and {SnapshotRepresentationMetadata.ClientRendered}.",
            route.Path,
            source));
        return null;
    }

    private static void RegisterDeclaration(
        SnapshotRoute route,
        SitemapRepresentation representation,
        string source,
        IDictionary<string, SitemapRepresentationDeclaration> declarations,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        if (!declarations.TryGetValue(route.Path, out var existing))
        {
            declarations[route.Path] = new SitemapRepresentationDeclaration(representation, source);
            return;
        }

        var existingIsClient = existing.Representation == SitemapRepresentation.ClientRendered;
        var currentIsClient = representation == SitemapRepresentation.ClientRendered;
        if (existingIsClient == currentIsClient)
        {
            return;
        }

        diagnostics.Add(new SnapshotDiagnostic(
            SnapshotDiagnosticCodes.SitemapRepresentationConflict,
            SnapshotDiagnosticSeverity.Error,
            $"Route {route.Path} is declared both {SnapshotRepresentationMetadata.ClientRendered} and prerenderable across sitemap files.",
            route.Path,
            source,
            route.OutputPath.Value,
            $"Make the representation consistent with {existing.Source}."));
    }

    private static bool TryParseRoute(
        string value,
        string source,
        ICollection<SnapshotDiagnostic> diagnostics,
        out SnapshotRoute route)
    {
        try
        {
            route = SnapshotRoute.Parse(value, source);
            return true;
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
            route = null!;
            return false;
        }
    }

    private enum SitemapRepresentation
    {
        DefaultPrerender,
        StaticPrerendered,
        ClientRendered
    }

    private sealed record SitemapRepresentationDeclaration(
        SitemapRepresentation Representation,
        string Source);
}
