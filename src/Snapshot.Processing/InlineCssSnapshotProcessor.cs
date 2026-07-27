using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using NUglify;
using NUglify.Css;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Processing;

namespace Snapshot.Processing;

internal sealed class InlineCssSnapshotProcessor : ISnapshotProcessor
{
    private const string BlazorEmptyRenderMarker = "<!--!-->";
    private readonly ISnapshotProcessor _inner;
    private readonly ConcurrentDictionary<string, byte> _reportedFallbacks = new(StringComparer.Ordinal);
    private readonly HtmlParser _parser = new();

    public InlineCssSnapshotProcessor(ISnapshotProcessor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<SnapshotProcessingResult> ProcessAsync(
        SnapshotProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var document = _parser.ParseDocument(context.Html);
        if (document.DocumentElement is null)
        {
            return await _inner.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var diagnostics = new List<SnapshotDiagnostic>();
        var changed = false;
        var index = 0;

        foreach (var style in document.QuerySelectorAll("style"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var originalSource = style.TextContent;
            if (string.IsNullOrWhiteSpace(originalSource))
            {
                continue;
            }

            var (source, removedMarkers) = RemoveBlazorRenderMarkers(originalSource);
            var result = Uglify.Css(source, $"{context.Route.Path}#style-{index}", CreateSafeCssSettings());
            if (result.HasErrors || string.IsNullOrEmpty(result.Code))
            {
                if (removedMarkers > 0)
                {
                    style.TextContent = source;
                    changed = true;
                }

                if (ShouldReportFallback(source, "source"))
                {
                    diagnostics.Add(CreatePreservedDiagnostic(
                        context,
                        index,
                        "NUglify rejected the CSS",
                        result.Errors.Select(static error => error.ToString()),
                        removedMarkers));
                }

                continue;
            }

            var verification = Uglify.Css(
                result.Code,
                $"{context.Route.Path}#style-{index}-verification",
                CreateSafeCssSettings());
            if (verification.HasErrors)
            {
                if (removedMarkers > 0)
                {
                    style.TextContent = source;
                    changed = true;
                }

                if (ShouldReportFallback(source, "verification"))
                {
                    diagnostics.Add(CreatePreservedDiagnostic(
                        context,
                        index,
                        "the minified CSS failed NUglify's verification pass",
                        verification.Errors.Select(static error => error.ToString()),
                        removedMarkers));
                }

                continue;
            }

            if (!result.Code.Equals(originalSource, StringComparison.Ordinal))
            {
                style.TextContent = result.Code;
                changed = true;
            }
        }

        var normalizedHtml = changed ? SerializeDocument(document) : context.Html;
        var processed = await _inner.ProcessAsync(
            context with { Html = normalizedHtml },
            cancellationToken).ConfigureAwait(false);

        if (diagnostics.Count == 0)
        {
            return processed with
            {
                OriginalUtf8Length = Encoding.UTF8.GetByteCount(context.Html)
            };
        }

        return processed with
        {
            Diagnostics = diagnostics.Concat(processed.Diagnostics).ToArray(),
            OriginalUtf8Length = Encoding.UTF8.GetByteCount(context.Html)
        };
    }

    private bool ShouldReportFallback(string source, string stage)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return _reportedFallbacks.TryAdd($"{stage}:{hash}", 0);
    }

    private static (string Source, int RemovedMarkers) RemoveBlazorRenderMarkers(string source)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(BlazorEmptyRenderMarker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += BlazorEmptyRenderMarker.Length;
        }

        return count == 0
            ? (source, 0)
            : (source.Replace(BlazorEmptyRenderMarker, string.Empty, StringComparison.Ordinal), count);
    }

    private static SnapshotDiagnostic CreatePreservedDiagnostic(
        SnapshotProcessingContext context,
        int index,
        string reason,
        IEnumerable<string?> errors,
        int removedMarkers)
    {
        var details = errors
            .Where(static error => !string.IsNullOrWhiteSpace(error))
            .Select(static error => error!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var parserDetail = details.Length == 0
            ? "No parser detail was returned."
            : string.Join(" | ", details);
        var markerDetail = removedMarkers == 0
            ? "The original CSS was preserved."
            : $"Snapshot removed {removedMarkers} empty Blazor render marker(s) before validation; the remaining CSS was preserved.";

        return new SnapshotDiagnostic(
            SnapshotDiagnosticCodes.InlineCssPreserved,
            SnapshotDiagnosticSeverity.Info,
            $"Inline CSS block {index} was not minified because {reason}. {parserDetail} {markerDetail}",
            context.Route.Path,
            context.Route.Source,
            context.Route.OutputPath.Value,
            "This is a safe minification fallback and does not fail the build. Correct genuinely invalid CSS in the application source.");
    }

    private static CssSettings CreateSafeCssSettings() => new()
    {
        ColorNames = CssColor.NoSwap,
        CommentMode = CssComment.Important,
        MinifyExpressions = false,
        RemoveEmptyBlocks = false,
        FixIE8Fonts = false,
        DecodeEscapes = false,
        AbbreviateHexColor = false,
        TermSemicolons = true
    };

    private static string SerializeDocument(IDocument document)
    {
        var root = document.DocumentElement ?? throw new InvalidDataException("The parsed snapshot did not contain a document element.");
        var doctype = document.Doctype?.Name ?? "html";
        return $"<!doctype {doctype}>\n{root.OuterHtml}";
    }
}
