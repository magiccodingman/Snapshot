using AngleSharp.Html.Parser;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Metadata;
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
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == SnapshotDiagnosticCodes.HtmlMinificationPreserved);
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
    public async Task Dom_html_minification_preserves_whitespace_sensitive_content_without_fallback()
    {
        const string javascript = "\n  const spacing = 'a  b';\n  window.value = spacing;\n";
        const string css = "\n  .card { white-space: pre; }\n";
        const string preformatted = "  first\n    second  ";
        var html = $$"""
            <!doctype html>
            <html>
            <head>
              <!-- remove this comment -->
              <title>Protected content</title>
              <link rel="canonical" href="https://example.test/docs">
              <style>{{css}}</style>
              <script>{{javascript}}</script>
            </head>
            <body>
              <main>  ordinary      visible text  </main>
              <pre>{{preformatted}}</pre>
              <textarea>{{preformatted}}</textarea>
              <code>{{preformatted}}</code>
            </body>
            </html>
            """;
        var options = new SnapshotProcessingOptions
        {
            MinifyInlineCss = false,
            MinifyInlineJson = false
        };

        var result = await new StandardSnapshotProcessor(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/docs"), html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == SnapshotDiagnosticCodes.HtmlMinificationPreserved);
        Assert.DoesNotContain("remove this comment", result.Html, StringComparison.Ordinal);

        var document = new HtmlParser().ParseDocument(result.Html);
        Assert.Equal(javascript, document.QuerySelector("script")!.TextContent);
        Assert.Equal(css, document.QuerySelector("style")!.TextContent);
        Assert.Equal(preformatted, document.QuerySelector("pre")!.TextContent);
        Assert.Equal(preformatted, document.QuerySelector("textarea")!.TextContent);
        Assert.Equal(preformatted, document.QuerySelector("code")!.TextContent);
        Assert.Equal(" ordinary visible text ", document.QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Css_parser_fallback_is_informational_and_preserves_the_block()
    {
        const string css = ".broken { color: red;";
        var html = $"<!doctype html><html><head><title>Docs</title><link rel=\"canonical\" href=\"https://example.test/docs\"><style>{css}</style></head><body>Docs</body></html>";
        var options = new SnapshotProcessingOptions
        {
            MinifyHtml = false,
            RemoveHtmlComments = false,
            MinifyInlineJson = false
        };

        var result = await new StandardSnapshotProcessor(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/docs"), html));

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
        Assert.Equal(SnapshotDiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("preserved unchanged", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("does not fail the build", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Equal(css, new HtmlParser().ParseDocument(result.Html).QuerySelector("style")!.TextContent);
    }

    [Fact]
    public async Task Repeated_identical_css_fallback_is_reported_once_per_processor()
    {
        const string css = ".broken { color: red;";
        var options = new SnapshotProcessingOptions
        {
            MinifyHtml = false,
            RemoveHtmlComments = false,
            MinifyInlineJson = false
        };
        var processor = new StandardSnapshotProcessor(options);

        var first = await processor.ProcessAsync(new SnapshotProcessingContext(
            SnapshotRoute.Parse("/one"),
            $"<!doctype html><html><head><title>One</title><link rel=\"canonical\" href=\"https://example.test/one\"><style>{css}</style></head><body>One</body></html>"));
        var second = await processor.ProcessAsync(new SnapshotProcessingContext(
            SnapshotRoute.Parse("/two"),
            $"<!doctype html><html><head><title>Two</title><link rel=\"canonical\" href=\"https://example.test/two\"><style>{css}</style></head><body>Two</body></html>"));

        Assert.Single(first.Diagnostics, item => item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
        Assert.DoesNotContain(second.Diagnostics, item => item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
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
    public async Task Validation_only_processing_preserves_content_and_adds_protocol_metadata()
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
        var document = new HtmlParser().ParseDocument(result.Html);
        Assert.Contains("keep", result.Html, StringComparison.Ordinal);
        Assert.Equal("{ \"value\": 1 }", document.QuerySelector("script")!.TextContent);
        Assert.Equal(
            SnapshotRepresentationMetadata.StaticPrerendered,
            document.QuerySelector("meta[name=rendering-mode]")?.GetAttribute("content"));
        Assert.Equal(
            SnapshotRepresentationMetadata.SnapshotProtocolMetaContent,
            document.QuerySelector("meta[name=snapshot-protocol]")?.GetAttribute("content"));
        Assert.True(result.ProcessedUtf8Length > result.OriginalUtf8Length);
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
