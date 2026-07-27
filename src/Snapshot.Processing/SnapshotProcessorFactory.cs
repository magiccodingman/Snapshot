using Snapshot.Protocol.Processing;

namespace Snapshot.Processing;

public static class SnapshotProcessorFactory
{
    public static ISnapshotProcessor Create(SnapshotProcessingOptions? options = null)
    {
        options ??= new SnapshotProcessingOptions();
        if (!options.Enabled)
        {
            return PassthroughSnapshotProcessor.Instance;
        }

        var standardOptions = CloneOptions(options);
        standardOptions.MinifyInlineCss = false;

        ISnapshotProcessor processor = new StandardSnapshotProcessor(standardOptions);
        if (options.MinifyInlineCss)
        {
            processor = new InlineCssSnapshotProcessor(processor);
        }

        return HasTransformations(options)
            ? processor
            : new ValidationOnlySnapshotProcessor(processor);
    }

    private static SnapshotProcessingOptions CloneOptions(SnapshotProcessingOptions options) => new()
    {
        Enabled = options.Enabled,
        MinifyHtml = options.MinifyHtml,
        RemoveHtmlComments = options.RemoveHtmlComments,
        MinifyInlineJson = options.MinifyInlineJson,
        ValidateInlineJson = options.ValidateInlineJson,
        MinifyInlineCss = options.MinifyInlineCss,
        CanonicalPolicy = options.CanonicalPolicy
    };

    private static bool HasTransformations(SnapshotProcessingOptions options) =>
        options.MinifyHtml ||
        options.RemoveHtmlComments ||
        options.MinifyInlineJson ||
        options.MinifyInlineCss;

    private sealed class ValidationOnlySnapshotProcessor : ISnapshotProcessor
    {
        private readonly ISnapshotProcessor _validator;

        public ValidationOnlySnapshotProcessor(ISnapshotProcessor validator)
        {
            _validator = validator;
        }

        public async ValueTask<SnapshotProcessingResult> ProcessAsync(
            SnapshotProcessingContext context,
            CancellationToken cancellationToken = default)
        {
            var validated = await _validator.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
            return validated with
            {
                Html = context.Html,
                ProcessedUtf8Length = validated.OriginalUtf8Length
            };
        }
    }
}
