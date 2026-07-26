using AngleSharp.Html.Parser;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Processing;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Processing.Tests;

public sealed class StandardSnapshotProcessorTests
{
    [Fact]
    public async Task Standard_processing_compacts_safe_content_and_preserves_javascript()
    {
        const string javascript = "const untouched = 'a  b'; // keep this exact\nwindow.snapshotValue = untouched;";
        var html = $$"""
            <!doctype html>
            <html>
            <head>
              <!-- ordinary comment should disappear -->
              <!--[if IE]>conditional comment stays<![endif]-->
              <title>Example</title>
              <link rel="canonical" href="https://example.test/docs">
              <style>
                .card {
                  color: #ffffff;
                  margin: 0px 0px 0px 0px;
                }
              </style>
              <script type="application/ld+json">
                {
                  "name": "Example",
                  "items": [1, 2, 3]
                }
              </script>
              <script>{{javascript}}</script>
            </head>
            <body>
              <main> Hello      world </main>
            </body>
            </html>
            """;

        var route = SnapshotRoute.Parse("/docs");
        var result = await new StandardSnapshotProcessor().ProcessAsync(new SnapshotProcessingContext(route, html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
        Assert.True(result.ProcessedUtf8Length < result.OriginalUtf8Length);
        Assert.DoesNotContain("ordinary comment should disappear", result.Html, StringComparison.Ordinal);
        Assert.Contains("conditional comment stays", result.Html, StringComparison.Ordinal);
        Assert.Contains("{\"name\":\"Example\",\"items\":[1,2,3]}", result.Html, StringComparison.Ordinal);

        var document = new HtmlParser().ParseDocument(result.Html);
        var scripts = document.QuerySelectorAll("script").ToArray();
        Assert.Equal(javascript, scripts.Single(script => script.GetAttribute("type") is null).TextContent);
        Assert.Contains(".card", document.QuerySelector("style")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_route_mismatch_is_an_error_by_default()
    {
        var route = SnapshotRoute.Parse("/docs");
        var html = "<!doctype html><html><head><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/other\"></head><body>Docs</body></html>";

        var result = await new StandardSnapshotProcessor().ProcessAsync(new SnapshotProcessingContext(route, html));

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == SnapshotDiagnosticCodes.CanonicalRouteMismatch &&
            diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Canonical_policy_can_be_relaxed_without_disabling_minification()
    {
        var options = new SnapshotProcessingOptions { CanonicalPolicy = SnapshotCanonicalPolicy.Warning };
        var route = SnapshotRoute.Parse("/docs");
        var html = "<!doctype html><html><head><!-- remove --><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/other\"></head><body>Docs</body></html>";

        var result = await new StandardSnapshotProcessor(options).ProcessAsync(new SnapshotProcessingContext(route, html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == SnapshotDiagnosticCodes.CanonicalRouteMismatch &&
            diagnostic.Severity == SnapshotDiagnosticSeverity.Warning);
        Assert.DoesNotContain("remove", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_only_processing_preserves_the_exact_html_string()
    {
        var options = new SnapshotProcessingOptions
        {
            MinifyHtml = false,
            RemoveHtmlComments = false,
            MinifyInlineJson = false,
            MinifyInlineCss = false
        };
        var route = SnapshotRoute.Parse("/docs");
        var html = "<!doctype html>\n<html><head><!-- keep --><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/docs\"><script type=\"application/json\">{ \"value\": 1 }</script></head><body>Docs</body></html>";

        var processor = SnapshotProcessorFactory.Create(options);
        var result = await processor.ProcessAsync(new SnapshotProcessingContext(route, html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
        Assert.Equal(html, result.Html);
        Assert.Equal(result.OriginalUtf8Length, result.ProcessedUtf8Length);
    }

    [Fact]
    public async Task Invalid_inline_json_fails_processing_without_rewriting_the_block()
    {
        var route = SnapshotRoute.Parse("/docs");
        var html = "<!doctype html><html><head><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/docs\"><script type=\"application/ld+json\">{ invalid }</script></head><body>Docs</body></html>";

        var result = await new StandardSnapshotProcessor().ProcessAsync(new SnapshotProcessingContext(route, html));

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == SnapshotDiagnosticCodes.InlineJsonInvalid &&
            diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
        Assert.Contains("{ invalid }", result.Html, StringComparison.Ordinal);
    }
}
