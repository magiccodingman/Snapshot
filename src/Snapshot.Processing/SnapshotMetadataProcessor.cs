using System.Text;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Metadata;
using Snapshot.Protocol.Processing;

namespace Snapshot.Processing;

internal sealed class SnapshotMetadataProcessor : ISnapshotProcessor
{
    private readonly ISnapshotProcessor _inner;

    public SnapshotMetadataProcessor(ISnapshotProcessor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<SnapshotProcessingResult> ProcessAsync(
        SnapshotProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        var processed = await _inner.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
        if (processed.Diagnostics.Any(static diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error))
        {
            return processed;
        }

        var annotation = SnapshotHtmlMetadataAnnotator.Annotate(processed.Html);
        if (annotation.FailureReason is not null)
        {
            var diagnostic = new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.HtmlMetadataPreserved,
                SnapshotDiagnosticSeverity.Warning,
                $"Snapshot rendering metadata was not added because {annotation.FailureReason}",
                context.Route.Path,
                context.Route.Source,
                context.Route.OutputPath.Value,
                "The original generated HTML was preserved safely.");
            return processed with
            {
                Diagnostics = processed.Diagnostics.Concat([diagnostic]).ToArray()
            };
        }

        if (!annotation.Changed)
        {
            return processed;
        }

        return processed with
        {
            Html = annotation.Html,
            ProcessedUtf8Length = Encoding.UTF8.GetByteCount(annotation.Html)
        };
    }
}
