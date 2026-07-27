using System.IO.Compression;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Metadata;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Protocol.Tests;

public sealed class RenderingMetadataTests
{
    [Fact]
    public void Html_annotation_adds_both_metadata_declarations_without_changing_page_semantics()
    {
        const string html = """
            <!doctype html>
            <html><head><title>Example</title><script>window.value = "<head>safe</head>";</script></head>
            <body><main data-page="docs">Hello</main></body></html>
            """;

        var result = SnapshotHtmlMetadataAnnotator.Annotate(html);

        Assert.True(result.Changed, result.FailureReason);
        Assert.Null(result.FailureReason);
        var document = new HtmlParser().ParseDocument(result.Html);
        Assert.Equal(
            SnapshotRepresentationMetadata.StaticPrerendered,
            document.QuerySelector("meta[name=rendering-mode]")?.GetAttribute("content"));
        Assert.Equal(
            SnapshotRepresentationMetadata.SnapshotProtocolMetaContent,
            document.QuerySelector("meta[name=snapshot-protocol]")?.GetAttribute("content"));
        Assert.Equal("window.value = \"<head>safe</head>\";", document.QuerySelector("script")?.TextContent);
        Assert.Equal("Hello", document.QuerySelector("main")?.TextContent);
    }

    [Fact]
    public void Html_annotation_is_idempotent_and_preserves_matching_existing_tags()
    {
        const string html = """
            <!doctype html><html><head>
            <meta name="rendering-mode" content="static-prerendered">
            <meta name="snapshot-protocol" content="1">
            </head><body>Ready</body></html>
            """;

        var result = SnapshotHtmlMetadataAnnotator.Annotate(html);

        Assert.False(result.Changed);
        Assert.Null(result.FailureReason);
        Assert.Equal(html, result.Html);
    }

    [Fact]
    public void Html_annotation_preserves_original_when_existing_metadata_conflicts()
    {
        const string html = """
            <!doctype html><html><head>
            <meta name="rendering-mode" content="client-rendered">
            </head><body>Ready</body></html>
            """;

        var result = SnapshotHtmlMetadataAnnotator.Annotate(html);

        Assert.False(result.Changed);
        Assert.NotNull(result.FailureReason);
        Assert.Equal(html, result.Html);
    }

    [Fact]
    public async Task Sitemap_discovery_skips_client_rendered_and_accepts_existing_prerendered_values()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap.xml"), $"""
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
                        xmlns:render="{SnapshotRepresentationMetadata.XmlNamespaceUri}">
                  <url><loc>https://example.test/default</loc></url>
                  <url><loc>https://example.test/static</loc><render:representation>static-prerendered</render:representation></url>
                  <url><loc>https://example.test/legacy</loc><render:representation>prerendered</render:representation></url>
                  <url><loc>https://example.test/live</loc><render:representation>client-rendered</render:representation></url>
                </urlset>
                """);

            var result = await new SitemapRouteDiscoverer().DiscoverAsync(
                root,
                new SnapshotRouteDiscoveryOptions { Mode = SnapshotRouteDiscoveryMode.SitemapsOnly },
                CancellationToken.None);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
            Assert.Contains(result.Routes, static route => route.Path == "/default");
            Assert.Contains(result.Routes, static route => route.Path == "/static");
            Assert.Contains(result.Routes, static route => route.Path == "/legacy");
            Assert.DoesNotContain(result.Routes, static route => route.Path == "/live");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Explicit_route_conflicting_with_client_rendered_fails_during_discovery()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap.xml"), $"""
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
                        xmlns:render="{SnapshotRepresentationMetadata.XmlNamespaceUri}">
                  <url><loc>https://example.test/docs</loc></url>
                  <url><loc>https://example.test/live</loc><render:representation>client-rendered</render:representation></url>
                </urlset>
                """);

            var result = await new SitemapRouteDiscoverer().DiscoverAsync(
                root,
                new SnapshotRouteDiscoveryOptions
                {
                    Mode = SnapshotRouteDiscoveryMode.SitemapsAndExplicit,
                    AdditionalRoutes = ["/live"]
                },
                CancellationToken.None);

            Assert.DoesNotContain(result.Routes, static route => route.Path == "/live");
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == SnapshotDiagnosticCodes.SitemapRepresentationConflict &&
                diagnostic.Severity == SnapshotDiagnosticSeverity.Error &&
                diagnostic.Route == "/live");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Output_sitemaps_annotate_built_urls_without_duplication_and_summarize_complete_children()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap-pages.xml"), $"""
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
                        xmlns:r="{SnapshotRepresentationMetadata.XmlNamespaceUri}">
                  <url><loc>https://example.test/docs</loc></url>
                  <url><loc>https://example.test/about</loc><r:representation>static-prerendered</r:representation></url>
                </urlset>
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap-mixed.xml"), $"""
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
                        xmlns:render="{SnapshotRepresentationMetadata.XmlNamespaceUri}">
                  <url><loc>https://example.test/news</loc></url>
                  <url><loc>https://example.test/live</loc><render:representation>client-rendered</render:representation></url>
                </urlset>
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "sitemap-index.xml"), """
                <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <sitemap><loc>https://example.test/sitemap-pages.xml</loc></sitemap>
                  <sitemap><loc>https://example.test/sitemap-mixed.xml</loc></sitemap>
                </sitemapindex>
                """);

            var result = await SnapshotSitemapAnnotator.CreateAsync(
                root,
                [SnapshotRoute.Parse("/docs"), SnapshotRoute.Parse("/about"), SnapshotRoute.Parse("/news")],
                CancellationToken.None);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
            var pages = XDocument.Parse(System.Text.Encoding.UTF8.GetString(result.Entries["sitemap-pages.xml"]));
            var pageRepresentations = pages.Descendants()
                .Where(element => element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri && element.Name.LocalName == "representation")
                .ToArray();
            Assert.Equal(2, pageRepresentations.Length);
            Assert.All(pageRepresentations, static element => Assert.Equal("static-prerendered", element.Value));

            var mixed = XDocument.Parse(System.Text.Encoding.UTF8.GetString(result.Entries["sitemap-mixed.xml"]));
            Assert.Equal(1, mixed.Descendants().Count(element =>
                element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri &&
                element.Name.LocalName == "representation" &&
                element.Value == SnapshotRepresentationMetadata.StaticPrerendered));
            Assert.Equal(1, mixed.Descendants().Count(element =>
                element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri &&
                element.Name.LocalName == "representation" &&
                element.Value == SnapshotRepresentationMetadata.ClientRendered));

            var index = XDocument.Parse(System.Text.Encoding.UTF8.GetString(result.Entries["sitemap-index.xml"]));
            var sitemapEntries = index.Root!.Elements().Where(static element => element.Name.LocalName == "sitemap").ToArray();
            Assert.Contains(sitemapEntries[0].Elements(), element =>
                element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri &&
                element.Name.LocalName == "representation" &&
                element.Value == SnapshotRepresentationMetadata.StaticPrerendered);
            Assert.DoesNotContain(sitemapEntries[1].Elements(), element =>
                element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri &&
                element.Name.LocalName == "representation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gzip_sitemap_is_annotated_and_remains_gzip()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "sitemap.xml.gz");
            await using (var output = File.Create(path))
            await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            await using (var writer = new StreamWriter(gzip))
            {
                await writer.WriteAsync("""
                    <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                      <url><loc>https://example.test/docs</loc></url>
                    </urlset>
                    """);
            }

            var result = await SnapshotSitemapAnnotator.CreateAsync(
                root,
                [SnapshotRoute.Parse("/docs")],
                CancellationToken.None);

            var bytes = result.Entries["sitemap.xml.gz"];
            await using var memory = new MemoryStream(bytes);
            await using var gzipInput = new GZipStream(memory, CompressionMode.Decompress);
            var document = await XDocument.LoadAsync(gzipInput, LoadOptions.None, CancellationToken.None);
            Assert.Contains(document.Descendants(), element =>
                element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri &&
                element.Name.LocalName == "representation" &&
                element.Value == SnapshotRepresentationMetadata.StaticPrerendered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapshot-rendering-metadata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
