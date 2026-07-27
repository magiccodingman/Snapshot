using System.IO.Compression;
using System.Xml.Linq;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Paths;
using Snapshot.Protocol.Routes;

namespace Snapshot.Protocol.Metadata;

public sealed record SnapshotSitemapAnnotationResult(
    IReadOnlyDictionary<string, byte[]> Entries,
    IReadOnlyList<SnapshotDiagnostic> Diagnostics);

public static class SnapshotSitemapAnnotator
{
    private static readonly XNamespace RenderingNamespace = SnapshotRepresentationMetadata.XmlNamespaceUri;

    public static async Task<SnapshotSitemapAnnotationResult> CreateAsync(
        string sourceDirectory,
        IReadOnlyCollection<SnapshotRoute> canonicalRoutes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentNullException.ThrowIfNull(canonicalRoutes);

        var diagnostics = new List<SnapshotDiagnostic>();
        var documents = new Dictionary<string, SitemapDocument>(StringComparer.Ordinal);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                     .Where(IsXmlCandidate)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = SnapshotArchivePath.Normalize(Path.GetRelativePath(sourceDirectory, sourcePath));
            var parsed = await TryLoadAsync(sourcePath, archivePath, diagnostics, cancellationToken).ConfigureAwait(false);
            if (parsed is not null)
            {
                documents[archivePath] = parsed;
            }
        }

        if (documents.Count == 0)
        {
            return new SnapshotSitemapAnnotationResult(
                new Dictionary<string, byte[]>(StringComparer.Ordinal),
                diagnostics);
        }

        var canonicalPaths = canonicalRoutes.Select(static route => route.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var document in documents.Values.Where(static document => document.Kind == SitemapDocumentKind.UrlSet))
        {
            AnnotateUrlSet(document, canonicalPaths, diagnostics);
        }

        foreach (var document in documents.Values.Where(static document => document.Kind == SitemapDocumentKind.Index))
        {
            ComputeIndexState(document, documents, new HashSet<string>(StringComparer.Ordinal), diagnostics);
        }

        var transformed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var document in documents.Values.Where(static document => document.Changed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                transformed[document.ArchivePath] = await SerializeAsync(document, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SitemapInvalid,
                    SnapshotDiagnosticSeverity.Warning,
                    $"The output sitemap annotation was skipped safely: {exception.Message}",
                    Source: document.SourcePath,
                    OutputPath: document.ArchivePath));
            }
        }

        return new SnapshotSitemapAnnotationResult(transformed, diagnostics);
    }

    private static void AnnotateUrlSet(
        SitemapDocument document,
        IReadOnlySet<string> canonicalPaths,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        var urls = document.Root.Elements()
            .Where(static node => node.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var allPrerendered = urls.Length > 0;

        foreach (var url in urls)
        {
            var location = url.Elements().FirstOrDefault(static node => node.Name.LocalName.Equals("loc", StringComparison.OrdinalIgnoreCase));
            if (location is null || !TryParseRoute(location.Value, out var route))
            {
                allPrerendered = false;
                continue;
            }

            var representations = GetRepresentations(url);
            if (representations.Length > 1)
            {
                allPrerendered = false;
                continue;
            }

            var existing = representations.SingleOrDefault();
            if (existing is not null && SnapshotRepresentationMetadata.IsClientRendered(existing.Value))
            {
                allPrerendered = false;
                continue;
            }

            if (!canonicalPaths.Contains(route.Path))
            {
                allPrerendered = false;
                continue;
            }

            if (existing is null)
            {
                EnsureRenderingNamespace(document.Root);
                url.Add(new XElement(
                    RenderingNamespace + SnapshotRepresentationMetadata.RepresentationElementName,
                    SnapshotRepresentationMetadata.StaticPrerendered));
                document.Changed = true;
                continue;
            }

            if (!SnapshotRepresentationMetadata.IsStaticPrerendered(existing.Value))
            {
                allPrerendered = false;
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SitemapRepresentationInvalid,
                    SnapshotDiagnosticSeverity.Warning,
                    $"Existing sitemap representation '{existing.Value.Trim()}' was preserved and not replaced.",
                    route.Path,
                    document.SourcePath,
                    document.ArchivePath));
            }
        }

        document.AllPrerendered = allPrerendered;
    }

    private static bool ComputeIndexState(
        SitemapDocument document,
        IReadOnlyDictionary<string, SitemapDocument> documents,
        ISet<string> visiting,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        if (document.AllPrerendered is { } completed)
        {
            return completed;
        }

        if (!visiting.Add(document.ArchivePath))
        {
            document.AllPrerendered = false;
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SitemapInvalid,
                SnapshotDiagnosticSeverity.Warning,
                "A sitemap index cycle prevented aggregate prerender metadata from being added.",
                Source: document.SourcePath,
                OutputPath: document.ArchivePath));
            return false;
        }

        var entries = document.Root.Elements()
            .Where(static node => node.Name.LocalName.Equals("sitemap", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var allPrerendered = entries.Length > 0;

        foreach (var entry in entries)
        {
            var location = entry.Elements().FirstOrDefault(static node => node.Name.LocalName.Equals("loc", StringComparison.OrdinalIgnoreCase));
            var child = location is null ? null : ResolveChild(document, location.Value, documents);
            var childPrerendered = child is not null &&
                                     (child.Kind == SitemapDocumentKind.UrlSet
                                         ? child.AllPrerendered == true
                                         : ComputeIndexState(child, documents, visiting, diagnostics));

            var representations = GetRepresentations(entry);
            var existing = representations.Length == 1 ? representations[0] : null;
            if (representations.Length > 1)
            {
                childPrerendered = false;
            }

            if (childPrerendered && existing is null)
            {
                EnsureRenderingNamespace(document.Root);
                entry.Add(new XElement(
                    RenderingNamespace + SnapshotRepresentationMetadata.RepresentationElementName,
                    SnapshotRepresentationMetadata.StaticPrerendered));
                document.Changed = true;
            }
            else if (existing is not null && SnapshotRepresentationMetadata.IsClientRendered(existing.Value))
            {
                childPrerendered = false;
            }
            else if (existing is not null &&
                     SnapshotRepresentationMetadata.IsStaticPrerendered(existing.Value) &&
                     !childPrerendered)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SitemapRepresentationConflict,
                    SnapshotDiagnosticSeverity.Warning,
                    "An existing sitemap-index prerender declaration was preserved, but the referenced local sitemap was not fully prerendered.",
                    Source: document.SourcePath,
                    OutputPath: document.ArchivePath));
            }

            allPrerendered &= childPrerendered;
        }

        visiting.Remove(document.ArchivePath);
        document.AllPrerendered = allPrerendered;
        return allPrerendered;
    }

    private static SitemapDocument? ResolveChild(
        SitemapDocument parent,
        string location,
        IReadOnlyDictionary<string, SitemapDocument> documents)
    {
        string candidate;
        if (Uri.TryCreate(location.Trim(), UriKind.Absolute, out var absolute))
        {
            candidate = Uri.UnescapeDataString(absolute.AbsolutePath).TrimStart('/');
        }
        else
        {
            var clean = location.Split(['?', '#'], 2)[0].Replace('\\', '/');
            var directory = Path.GetDirectoryName(parent.ArchivePath)?.Replace('\\', '/') ?? string.Empty;
            candidate = string.IsNullOrEmpty(directory) ? clean : $"{directory}/{clean}";
        }

        try
        {
            candidate = SnapshotArchivePath.Normalize(candidate);
        }
        catch (Exception)
        {
            return null;
        }

        if (documents.TryGetValue(candidate, out var exact))
        {
            return exact;
        }

        var fileName = Path.GetFileName(candidate);
        var matches = documents.Values
            .Where(document => Path.GetFileName(document.ArchivePath).Equals(fileName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static XElement[] GetRepresentations(XElement parent) =>
        parent.Elements()
            .Where(static node =>
                node.Name.NamespaceName.Equals(SnapshotRepresentationMetadata.XmlNamespaceUri, StringComparison.Ordinal) &&
                node.Name.LocalName.Equals(SnapshotRepresentationMetadata.RepresentationElementName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void EnsureRenderingNamespace(XElement root)
    {
        if (root.GetPrefixOfNamespace(RenderingNamespace) is not null)
        {
            return;
        }

        var prefix = root.GetNamespaceOfPrefix(SnapshotRepresentationMetadata.XmlPrefix) is null
            ? SnapshotRepresentationMetadata.XmlPrefix
            : "snapshot-render";
        root.SetAttributeValue(XNamespace.Xmlns + prefix, SnapshotRepresentationMetadata.XmlNamespaceUri);
    }

    private static bool TryParseRoute(string value, out SnapshotRoute route)
    {
        try
        {
            route = SnapshotRoute.Parse(value);
            return true;
        }
        catch (SnapshotRouteException)
        {
            route = null!;
            return false;
        }
    }

    private static bool IsXmlCandidate(string path) =>
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase);

    private static async Task<SitemapDocument?> TryLoadAsync(
        string sourcePath,
        string archivePath,
        ICollection<SnapshotDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(sourcePath);
            await using Stream input = sourcePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false)
                : file;
            var document = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, cancellationToken).ConfigureAwait(false);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            var kind = root.Name.LocalName.ToLowerInvariant() switch
            {
                "urlset" => SitemapDocumentKind.UrlSet,
                "sitemapindex" => SitemapDocumentKind.Index,
                _ => SitemapDocumentKind.Other
            };
            return kind == SitemapDocumentKind.Other
                ? null
                : new SitemapDocument(sourcePath, archivePath, document, root, kind, sourcePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or IOException)
        {
            if (Path.GetFileName(sourcePath).Contains("sitemap", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SitemapInvalid,
                    SnapshotDiagnosticSeverity.Warning,
                    $"The output sitemap could not be annotated safely: {exception.Message}",
                    Source: sourcePath,
                    OutputPath: archivePath));
            }
            return null;
        }
    }

    private static async Task<byte[]> SerializeAsync(SitemapDocument document, CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream();
        if (document.IsGzip)
        {
            await using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                document.Document.Save(gzip, SaveOptions.DisableFormatting);
                await gzip.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            document.Document.Save(memory, SaveOptions.DisableFormatting);
        }

        return memory.ToArray();
    }

    private enum SitemapDocumentKind
    {
        Other,
        UrlSet,
        Index
    }

    private sealed class SitemapDocument
    {
        public SitemapDocument(
            string sourcePath,
            string archivePath,
            XDocument document,
            XElement root,
            SitemapDocumentKind kind,
            bool isGzip)
        {
            SourcePath = sourcePath;
            ArchivePath = archivePath;
            Document = document;
            Root = root;
            Kind = kind;
            IsGzip = isGzip;
        }

        public string SourcePath { get; }
        public string ArchivePath { get; }
        public XDocument Document { get; }
        public XElement Root { get; }
        public SitemapDocumentKind Kind { get; }
        public bool IsGzip { get; }
        public bool Changed { get; set; }
        public bool? AllPrerendered { get; set; }
    }
}
