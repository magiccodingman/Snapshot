using AngleSharp.Html.Parser;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Processing;
using Snapshot.Protocol.Routes;
using Xunit;

namespace Snapshot.Processing.Tests;

public sealed class BlazorInlineCssProcessorTests
{
    [Fact]
    public async Task Default_processing_removes_blazor_render_markers_before_css_minification()
    {
        const string css = """
            <!--!-->
            .layout-wrapper{display:flex;gap:18px}
            .left-nav{width:280px;max-width:280px}
            @media (max-width: 1100px){.left-nav{display:none}}
            """;
        var html = CreateHtml("/docs", css);
        var options = CreateCssOnlyOptions();

        var result = await SnapshotProcessorFactory.Create(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/docs"), html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == SnapshotDiagnosticCodes.InlineCssPreserved);

        var processedCss = new HtmlParser().ParseDocument(result.Html).QuerySelector("style")!.TextContent;
        Assert.DoesNotContain("<!--!-->", processedCss, StringComparison.Ordinal);
        Assert.Contains(".layout-wrapper", processedCss, StringComparison.Ordinal);
        Assert.Contains("@media", processedCss, StringComparison.Ordinal);
        Assert.True(processedCss.Length < css.Length);
    }

    [Fact]
    public async Task Generated_split_heading_selector_is_repaired_only_after_parse_failure()
    {
        const string css = """
            <!--!-->
            .hero { display: flex; }
            <!--!-->@media (max-width: 600px)
            {
                .overlay-text h 1 { font-size: 2rem; }
            }
            <!--!-->
            """;
        var html = CreateHtml("/", css);
        var options = CreateCssOnlyOptions();

        var result = await SnapshotProcessorFactory.Create(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/"), html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == SnapshotDiagnosticCodes.InlineCssPreserved);

        var processedCss = new HtmlParser().ParseDocument(result.Html).QuerySelector("style")!.TextContent;
        Assert.DoesNotContain("<!--!-->", processedCss, StringComparison.Ordinal);
        Assert.DoesNotContain("h 1", processedCss, StringComparison.Ordinal);
        Assert.Contains(".overlay-text h1", processedCss, StringComparison.Ordinal);
        Assert.Contains("@media", processedCss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generated_split_heading_with_one_missing_outer_brace_is_completed_after_eof_validation()
    {
        const string css = """
            <!--!-->
            .hero { display: flex; }
            <!--!-->@media (max-width: 600px)
            {
                .overlay-text h 1
                {
                    font-size: 2rem;
                }
                .overlay-text::before { content: "}"; }
            <!--!-->
            """;
        var html = CreateHtml("/", css);
        var options = CreateCssOnlyOptions();

        var result = await SnapshotProcessorFactory.Create(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/"), html));

        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == SnapshotDiagnosticCodes.InlineCssPreserved);

        var processedCss = new HtmlParser().ParseDocument(result.Html).QuerySelector("style")!.TextContent;
        Assert.DoesNotContain("<!--!-->", processedCss, StringComparison.Ordinal);
        Assert.DoesNotContain("h 1", processedCss, StringComparison.Ordinal);
        Assert.Contains(".overlay-text h1", processedCss, StringComparison.Ordinal);
        Assert.Contains("content:\"}\"", processedCss, StringComparison.Ordinal);
        Assert.Contains("@media", processedCss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_brace_without_a_known_heading_repair_is_not_completed()
    {
        const string css = """
            <!--!-->@media (max-width: 600px)
            {
                .card { color: red; }
            <!--!-->
            """;
        var html = CreateHtml("/", css);
        var options = CreateCssOnlyOptions();

        var result = await SnapshotProcessorFactory.Create(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/"), html));

        Assert.Single(result.Diagnostics, item =>
            item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
    }

    [Fact]
    public async Task More_than_one_missing_brace_is_not_completed()
    {
        const string css = """
            <!--!-->@media (max-width: 600px)
            {
                .overlay-text h 1
                {
                    font-size: 2rem;
            <!--!-->
            """;
        var html = CreateHtml("/", css);
        var options = CreateCssOnlyOptions();

        var result = await SnapshotProcessorFactory.Create(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/"), html));

        Assert.Single(result.Diagnostics, item =>
            item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
    }

    [Fact]
    public async Task Arbitrary_invalid_selectors_are_not_guess_repaired()
    {
        const string css = """
            <!--!-->
            .hero { display: flex; }
            .overlay-text article 1 { font-size: 2rem; }
            """;
        var html = CreateHtml("/", css);
        var options = CreateCssOnlyOptions();

        var result = await SnapshotProcessorFactory.Create(options)
            .ProcessAsync(new SnapshotProcessingContext(SnapshotRoute.Parse("/"), html));

        var diagnostic = Assert.Single(result.Diagnostics, item =>
            item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
        Assert.Equal(SnapshotDiagnosticSeverity.Info, diagnostic.Severity);

        var processedCss = new HtmlParser().ParseDocument(result.Html).QuerySelector("style")!.TextContent;
        Assert.DoesNotContain("<!--!-->", processedCss, StringComparison.Ordinal);
        Assert.Contains("article 1", processedCss, StringComparison.Ordinal);
    }

    private static SnapshotProcessingOptions CreateCssOnlyOptions() => new()
    {
        MinifyHtml = false,
        RemoveHtmlComments = false,
        MinifyInlineJson = false,
        MinifyInlineCss = true
    };

    private static string CreateHtml(string route, string css) =>
        $"<!doctype html><html><head><title>Test</title><link rel=\"canonical\" href=\"https://example.test{route}\"><style>{css}</style></head><body>Test</body></html>";
}
