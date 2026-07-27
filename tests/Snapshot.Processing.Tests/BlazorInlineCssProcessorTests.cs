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
    public async Task Invalid_css_is_not_guess_fixed_after_blazor_marker_cleanup()
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

        var diagnostic = Assert.Single(result.Diagnostics, item =>
            item.Code == SnapshotDiagnosticCodes.InlineCssPreserved);
        Assert.Equal(SnapshotDiagnosticSeverity.Info, diagnostic.Severity);
        Assert.DoesNotContain("Unexpected token, found '!'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("remaining CSS was preserved", diagnostic.Message, StringComparison.Ordinal);

        var processedCss = new HtmlParser().ParseDocument(result.Html).QuerySelector("style")!.TextContent;
        Assert.DoesNotContain("<!--!-->", processedCss, StringComparison.Ordinal);
        Assert.Contains(".overlay-text h 1", processedCss, StringComparison.Ordinal);
        Assert.DoesNotContain(".overlay-text h1", processedCss, StringComparison.Ordinal);
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
