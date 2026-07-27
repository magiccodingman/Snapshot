using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Metadata;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Processing.Tests;

public sealed class RenderingMetadataBuildTests
{
    [Fact]
    public async Task Build_annotates_output_loader_snapshots_and_sitemap_but_not_redirects()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "wwwroot");
        var output = Path.Combine(root, "site.zip");
        Directory.CreateDirectory(source);

        const string sourceLoader = """
            <!doctype html><html><head><meta charset="utf-8"><script id="snapshot-protocol"></script></head>
            <body><div id="app">Loader</div></body></html>
            """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "index.html"), sourceLoader);
            await File.WriteAllTextAsync(Path.Combine(source, "sitemap.xml"), """
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://example.test/docs</loc></url>
                </urlset>
                """);

            var route = SnapshotRoute.Parse("/docs");
            var renderer = new FixedRenderer(new SnapshotRenderResult(
                route,
                true,
                "<!doctype html><html><head><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/docs\"></head><body><main>Docs</main></body></html>",
                1,
                TimeSpan.FromMilliseconds(5)));
            var engine = SnapshotEngine.CreateBuilder()
                .UseRenderer(renderer)
                .UseStandardProcessing()
                .Build();

            var result = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = source,
                OutputPath = output,
                Discovery = new SnapshotRouteDiscoveryOptions { Mode = SnapshotRouteDiscoveryMode.SitemapsOnly },
                CaseAliases = new SnapshotCaseAliasOptions { GenerateMissingPrefixGateways = false },
                Concurrency = 1
            });

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(sourceLoader, await File.ReadAllTextAsync(Path.Combine(source, "index.html")));

            await using var archive = await SnapshotArchive.OpenAsync(output);
            AssertMetadata(await archive.ReadTextAsync("index.html"));
            AssertMetadata(await archive.ReadTextAsync("docs/index.html"));

            var redirect = await archive.ReadTextAsync("Docs/index.html");
            Assert.DoesNotContain("name=\"rendering-mode\"", redirect, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("name=\"snapshot-protocol\"", redirect, StringComparison.OrdinalIgnoreCase);

            var sitemap = XDocument.Parse(await archive.ReadTextAsync("sitemap.xml"));
            Assert.Contains(sitemap.Descendants(), static element =>
                element.Name.NamespaceName == SnapshotRepresentationMetadata.XmlNamespaceUri &&
                element.Name.LocalName == SnapshotRepresentationMetadata.RepresentationElementName &&
                element.Value == SnapshotRepresentationMetadata.StaticPrerendered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_canonical_html_in_source_is_annotated_only_in_the_output_copy()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "wwwroot");
        var output = Path.Combine(root, "site.zip");
        Directory.CreateDirectory(Path.Combine(source, "manual"));

        const string sourceLoader = "<!doctype html><html><head><script id=\"snapshot-protocol\"></script></head><body>Loader</body></html>";
        const string manualHtml = "<!doctype html><html><head><title>Manual</title></head><body>Manual page</body></html>";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "index.html"), sourceLoader);
            await File.WriteAllTextAsync(Path.Combine(source, "manual", "index.html"), manualHtml);

            var engine = SnapshotEngine.CreateBuilder()
                .UseRenderer(new EmptyRenderer())
                .UseStandardProcessing()
                .Build();
            var result = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = source,
                OutputPath = output,
                Discovery = new SnapshotRouteDiscoveryOptions
                {
                    Mode = SnapshotRouteDiscoveryMode.ExplicitOnly,
                    AdditionalRoutes = ["/manual"]
                },
                CaseAliases = new SnapshotCaseAliasOptions
                {
                    Enabled = false,
                    GenerateMissingPrefixGateways = false
                }
            });

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(manualHtml, await File.ReadAllTextAsync(Path.Combine(source, "manual", "index.html")));
            await using var archive = await SnapshotArchive.OpenAsync(output);
            AssertMetadata(await archive.ReadTextAsync("manual/index.html"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_rendered_explicit_override_fails_before_renderer_is_called()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "wwwroot");
        var output = Path.Combine(root, "site.zip");
        Directory.CreateDirectory(source);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(source, "index.html"),
                "<!doctype html><html><head><script id=\"snapshot-protocol\"></script></head><body>Loader</body></html>");
            await File.WriteAllTextAsync(Path.Combine(source, "sitemap.xml"), $"""
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
                        xmlns:render="{SnapshotRepresentationMetadata.XmlNamespaceUri}">
                  <url><loc>https://example.test/docs</loc></url>
                  <url><loc>https://example.test/live</loc><render:representation>client-rendered</render:representation></url>
                </urlset>
                """);

            var renderer = new CountingRenderer();
            var engine = SnapshotEngine.CreateBuilder().UseRenderer(renderer).UseStandardProcessing().Build();
            var result = await engine.BuildAsync(new SnapshotBuildRequest
            {
                SourceDirectory = source,
                OutputPath = output,
                Discovery = new SnapshotRouteDiscoveryOptions
                {
                    Mode = SnapshotRouteDiscoveryMode.SitemapsAndExplicit,
                    AdditionalRoutes = ["/live"]
                }
            });

            Assert.False(result.Succeeded);
            Assert.Equal(0, renderer.Calls);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == SnapshotDiagnosticCodes.SitemapRepresentationConflict &&
                diagnostic.Route == "/live");
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertMetadata(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        Assert.Equal(
            SnapshotRepresentationMetadata.StaticPrerendered,
            document.QuerySelector("meta[name=rendering-mode]")?.GetAttribute("content"));
        Assert.Equal(
            SnapshotRepresentationMetadata.SnapshotProtocolMetaContent,
            document.QuerySelector("meta[name=snapshot-protocol]")?.GetAttribute("content"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapshot-rendering-build-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedRenderer : ISnapshotRenderer
    {
        private readonly SnapshotRenderResult _result;

        public FixedRenderer(SnapshotRenderResult result)
        {
            _result = result;
        }

        public async IAsyncEnumerable<SnapshotRenderResult> RenderAsync(
            SnapshotRenderRequest request,
            IProgress<SnapshotProgress>? progress,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield return _result;
        }
    }

    private sealed class EmptyRenderer : ISnapshotRenderer
    {
        public async IAsyncEnumerable<SnapshotRenderResult> RenderAsync(
            SnapshotRenderRequest request,
            IProgress<SnapshotProgress>? progress,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class CountingRenderer : ISnapshotRenderer
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<SnapshotRenderResult> RenderAsync(
            SnapshotRenderRequest request,
            IProgress<SnapshotProgress>? progress,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
