using AngleSharp.Html.Parser;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Manifest;
using Snapshot.Protocol.Output;
using Snapshot.Protocol.Protocol;

namespace Snapshot.Protocol.Validation;

public sealed class SnapshotArchiveValidator
{
    public async Task<IReadOnlyList<SnapshotDiagnostic>> ValidateAsync(
        string artifactPath,
        SnapshotOutputPlan plan,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<SnapshotDiagnostic>();
        await using var archive = await SnapshotArchive.OpenAsync(artifactPath, cancellationToken).ConfigureAwait(false);

        diagnostics.AddRange(await archive.ValidateIntegrityAsync(cancellationToken).ConfigureAwait(false));

        foreach (var route in plan.CanonicalRoutes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = archive.TryGetFile(route.OutputPath.Value);
            if (file is null)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    "Canonical route output is missing from the archive.",
                    route.Path,
                    route.Source,
                    route.OutputPath.Value));
                continue;
            }

            var manifestEntry = manifest.Entries.FirstOrDefault(entry => entry.Path.Equals(route.OutputPath.Value, StringComparison.Ordinal));
            if (manifestEntry?.Kind != SnapshotManifestEntryKind.Snapshot)
            {
                continue;
            }

            var html = await archive.ReadTextAsync(route.OutputPath.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (ContainsReadinessElement(html))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    $"Captured output still contains <{SnapshotProtocolConstants.ReadyElementName}>.",
                    route.Path,
                    OutputPath: route.OutputPath.Value));
            }

            if (!html.Contains($"name=\"{SnapshotProtocolConstants.RouteMetaName}\"", StringComparison.OrdinalIgnoreCase) &&
                !html.Contains($"name='{SnapshotProtocolConstants.RouteMetaName}'", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    "Captured output is missing Snapshot Protocol route metadata.",
                    route.Path,
                    OutputPath: route.OutputPath.Value));
            }

            if (!html.Contains("rel=\"canonical\"", StringComparison.OrdinalIgnoreCase) &&
                !html.Contains("rel='canonical'", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotMissingCanonical,
                    SnapshotDiagnosticSeverity.Warning,
                    "Captured output does not contain a canonical link.",
                    route.Path,
                    OutputPath: route.OutputPath.Value));
            }
        }

        return diagnostics;
    }

    internal static bool ContainsReadinessElement(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelector(SnapshotProtocolConstants.ReadyElementName) is not null;
    }
}
