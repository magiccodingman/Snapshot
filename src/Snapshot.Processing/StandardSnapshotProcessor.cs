using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NUglify;
using NUglify.Css;
using NUglify.Html;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Processing;
using Snapshot.Protocol.Routes;
using AngleHtmlParser = AngleSharp.Html.Parser.HtmlParser;

namespace Snapshot.Processing;

public sealed class StandardSnapshotProcessor : ISnapshotProcessor
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SnapshotProcessingOptions _options;
    private readonly AngleHtmlParser _parser = new();

    public StandardSnapshotProcessor(SnapshotProcessingOptions? options = null)
    {
        _options = options ?? new SnapshotProcessingOptions();
    }

    public ValueTask<SnapshotProcessingResult> ProcessAsync(
        SnapshotProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            return ValueTask.FromResult(SnapshotProcessingResult.Unchanged(context));
        }

        var diagnostics = new List<SnapshotDiagnostic>();
        var originalLength = Encoding.UTF8.GetByteCount(context.Html);
        var document = _parser.ParseDocument(context.Html);

        if (document.DocumentElement is null || document.Head is null || document.Body is null)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.ProcessingInvalid,
                SnapshotDiagnosticSeverity.Error,
                "The captured snapshot could not be parsed as a complete HTML document.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
            return ValueTask.FromResult(new SnapshotProcessingResult(context.Html, diagnostics, originalLength, originalLength));
        }

        ValidateCanonical(document, context, diagnostics);
        ProcessInlineJson(document, context, diagnostics, cancellationToken);
        ProcessInlineCss(document, context, diagnostics, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var preparedHtml = SerializeDocument(document);
        var processedHtml = ProcessHtml(preparedHtml, context, diagnostics);
        var processedLength = Encoding.UTF8.GetByteCount(processedHtml);

        return ValueTask.FromResult(new SnapshotProcessingResult(
            processedHtml,
            diagnostics,
            originalLength,
            processedLength));
    }

    private void ValidateCanonical(
        IDocument document,
        SnapshotProcessingContext context,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        if (_options.CanonicalPolicy == SnapshotCanonicalPolicy.Disabled)
        {
            return;
        }

        var severity = _options.CanonicalPolicy == SnapshotCanonicalPolicy.Error
            ? SnapshotDiagnosticSeverity.Error
            : SnapshotDiagnosticSeverity.Warning;
        var canonicals = document.Head!
            .QuerySelectorAll("link[rel~=canonical]")
            .ToArray();

        if (canonicals.Length == 0)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CanonicalMissing,
                severity,
                "The snapshot does not contain a canonical link.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value,
                "Add one absolute HTTP or HTTPS canonical URL inside <snapshot-ready>."));
            return;
        }

        if (canonicals.Length != 1)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CanonicalDuplicate,
                severity,
                $"The snapshot contains {canonicals.Length} canonical links; exactly one is required.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
            return;
        }

        var href = canonicals[0].GetAttribute("href")?.Trim();
        if (string.IsNullOrEmpty(href) ||
            !Uri.TryCreate(href, UriKind.Absolute, out var canonical) ||
            canonical.Scheme is not ("http" or "https"))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CanonicalInvalid,
                severity,
                "The canonical link must contain one absolute HTTP or HTTPS URL.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
            return;
        }

        if (canonical.IsLoopback)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CanonicalInvalid,
                severity,
                "The canonical URL points to a loopback host.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value,
                "Use the public deployment origin; Snapshot intentionally trusts that origin once supplied."));
        }

        if (!string.IsNullOrEmpty(canonical.Query) || !string.IsNullOrEmpty(canonical.Fragment))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CanonicalInvalid,
                severity,
                "The canonical URL contains a query string or fragment, but Snapshot routes are path-based.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
        }

        try
        {
            var canonicalRoute = SnapshotRoute.Parse(canonical.AbsolutePath).Path;
            if (!canonicalRoute.Equals(context.Route.Path, StringComparison.Ordinal))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.CanonicalRouteMismatch,
                    severity,
                    $"Canonical path '{canonicalRoute}' does not match captured route '{context.Route.Path}'.",
                    context.Route.Path,
                    context.Route.Source,
                    context.Route.OutputPath.Value,
                    "Correct the route metadata or explicitly relax --canonical-policy if this is intentional."));
            }
        }
        catch (SnapshotRouteException exception)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.CanonicalInvalid,
                severity,
                $"The canonical URL contains an unsupported route path: {exception.Message}",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
        }
    }

    private void ProcessInlineJson(
        IDocument document,
        SnapshotProcessingContext context,
        ICollection<SnapshotDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!_options.ValidateInlineJson && !_options.MinifyInlineJson)
        {
            return;
        }

        var index = 0;
        foreach (var script in document.QuerySelectorAll("script[type]").Where(IsJsonScript))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var source = script.TextContent;
            try
            {
                using var parsed = JsonDocument.Parse(source);
                if (_options.MinifyInlineJson)
                {
                    var compact = JsonSerializer.Serialize(parsed.RootElement, CompactJsonOptions);
                    using var verification = JsonDocument.Parse(compact);
                    script.TextContent = compact;
                }
            }
            catch (JsonException exception)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.InlineJsonInvalid,
                    _options.ValidateInlineJson ? SnapshotDiagnosticSeverity.Error : SnapshotDiagnosticSeverity.Warning,
                    $"Inline JSON block {index} is invalid and was preserved: {exception.Message}",
                    context.Route.Path,
                    context.Route.Source,
                    context.Route.OutputPath.Value));
            }
        }
    }

    private void ProcessInlineCss(
        IDocument document,
        SnapshotProcessingContext context,
        ICollection<SnapshotDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!_options.MinifyInlineCss)
        {
            return;
        }

        var index = 0;
        foreach (var style in document.QuerySelectorAll("style"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var source = style.TextContent;
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var result = Uglify.Css(source, $"{context.Route.Path}#style-{index}", CreateSafeCssSettings());
            if (result.HasErrors || string.IsNullOrEmpty(result.Code))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.InlineCssPreserved,
                    SnapshotDiagnosticSeverity.Warning,
                    $"Inline CSS block {index} could not be safely minified and was preserved unchanged.",
                    context.Route.Path,
                    context.Route.Source,
                    context.Route.OutputPath.Value));
                continue;
            }

            var verification = Uglify.Css(result.Code, $"{context.Route.Path}#style-{index}-verification", CreateSafeCssSettings());
            if (verification.HasErrors)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.InlineCssPreserved,
                    SnapshotDiagnosticSeverity.Warning,
                    $"Inline CSS block {index} failed post-minification syntax validation and was preserved unchanged.",
                    context.Route.Path,
                    context.Route.Source,
                    context.Route.OutputPath.Value));
                continue;
            }

            style.TextContent = result.Code;
        }
    }

    private string ProcessHtml(
        string preparedHtml,
        SnapshotProcessingContext context,
        ICollection<SnapshotDiagnostic> diagnostics)
    {
        if (!_options.MinifyHtml && !_options.RemoveHtmlComments)
        {
            return preparedHtml;
        }

        var baselineFingerprint = CreateFingerprint(_parser.ParseDocument(preparedHtml));
        var result = Uglify.Html(preparedHtml, CreateSafeHtmlSettings());
        if (result.HasErrors || string.IsNullOrEmpty(result.Code))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.HtmlMinificationPreserved,
                SnapshotDiagnosticSeverity.Warning,
                "HTML minification reported a parsing error; the unminified snapshot was preserved.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
            return preparedHtml;
        }

        var verificationFingerprint = CreateFingerprint(_parser.ParseDocument(result.Code));
        if (!baselineFingerprint.IsEquivalentTo(verificationFingerprint))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.HtmlMinificationPreserved,
                SnapshotDiagnosticSeverity.Warning,
                "HTML minification changed the document structure or executable content; the unminified snapshot was preserved.",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value));
            return preparedHtml;
        }

        return result.Code;
    }

    private HtmlSettings CreateSafeHtmlSettings()
    {
        var settings = new HtmlSettings
        {
            CollapseWhitespaces = _options.MinifyHtml,
            KeepOneSpaceWhenCollapsing = true,
            RemoveComments = _options.RemoveHtmlComments,
            RemoveOptionalTags = false,
            RemoveInvalidClosingTags = false,
            RemoveEmptyAttributes = false,
            RemoveAttributeQuotes = false,
            DecodeEntityCharacters = false,
            RemoveScriptStyleTypeAttribute = false,
            ShortBooleanAttribute = false,
            MinifyJs = false,
            MinifyJsAttributes = false,
            MinifyCss = false,
            MinifyCssAttributes = false,
            RemoveJavaScript = false,
            PrettyPrint = false
        };

        settings.TagsWithNonCollapsibleWhitespaces["script"] = true;
        settings.TagsWithNonCollapsibleWhitespaces["style"] = true;
        settings.KeepCommentsRegex.Add(new Regex(@"^\s*\[if\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        settings.KeepCommentsRegex.Add(new Regex(@"^\s*(?:googleoff|googleon|noindex|/noindex)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        settings.KeepCommentsRegex.Add(new Regex(@"^\s*(?:Snapshot Protocol|End Snapshot Protocol)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return settings;
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

    private static bool IsJsonScript(IElement element)
    {
        var type = element.GetAttribute("type")?.Trim();
        return type is not null &&
               (type.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("application/ld+json", StringComparison.OrdinalIgnoreCase) ||
                type.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static string SerializeDocument(IDocument document)
    {
        var root = document.DocumentElement ?? throw new InvalidDataException("The parsed snapshot did not contain a document element.");
        var doctype = document.Doctype?.Name ?? "html";
        return $"<!doctype {doctype}>\n{root.OuterHtml}";
    }

    private static SemanticFingerprint CreateFingerprint(IDocument document)
    {
        var elements = document.QuerySelectorAll("*")
            .Select(element =>
            {
                var attributes = element.Attributes
                    .OrderBy(static attribute => attribute.Name, StringComparer.Ordinal)
                    .Select(static attribute => $"{attribute.Name}={attribute.Value}");
                return $"{element.TagName}|{string.Join("\u001f", attributes)}";
            })
            .ToArray();
        var scripts = document.QuerySelectorAll("script").Select(static element => element.TextContent).ToArray();
        var styles = document.QuerySelectorAll("style").Select(static element => element.TextContent).ToArray();
        var sensitiveText = document.QuerySelectorAll("pre,textarea").Select(static element => element.TextContent).ToArray();
        var visibleText = NormalizeWhitespace(document.Body?.TextContent ?? string.Empty);
        return new SemanticFingerprint(elements, scripts, styles, sensitiveText, visibleText);
    }

    private static string NormalizeWhitespace(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private sealed record SemanticFingerprint(
        IReadOnlyList<string> Elements,
        IReadOnlyList<string> Scripts,
        IReadOnlyList<string> Styles,
        IReadOnlyList<string> SensitiveText,
        string VisibleText)
    {
        public bool IsEquivalentTo(SemanticFingerprint other) =>
            Elements.SequenceEqual(other.Elements, StringComparer.Ordinal) &&
            Scripts.SequenceEqual(other.Scripts, StringComparer.Ordinal) &&
            Styles.SequenceEqual(other.Styles, StringComparer.Ordinal) &&
            SensitiveText.SequenceEqual(other.SensitiveText, StringComparer.Ordinal) &&
            VisibleText.Equals(other.VisibleText, StringComparison.Ordinal);
    }
}
