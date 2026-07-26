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

        var standard = new StandardSnapshotProcessor(options);
        return HasTransformations(options)
            ? standard
            : new ValidationOnlySnapshotProcessor(standard);
    }

    private static bool HasTransformations(SnapshotProcessingOptions options) =>
        options.MinifyHtml ||
        options.RemoveHtmlComments ||
        options.MinifyInlineJson ||
        options.MinifyInlineCss;

    private sealed class ValidationOnlySnapshotProcessor : ISnapshotProcessor
    {
        private readonly StandardSnapshotProcessor _validator;

        public ValidationOnlySnapshotProcessor(StandardSnapshotProcessor validator)
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
