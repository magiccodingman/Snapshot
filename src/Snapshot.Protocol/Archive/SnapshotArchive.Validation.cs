using System.Security.Cryptography;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Protocol;
using Snapshot.Protocol.Paths;

namespace Snapshot.Protocol.Archive;

public sealed partial class SnapshotArchive
{
    public async Task<IReadOnlyList<SnapshotDiagnostic>> ValidateIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<SnapshotDiagnostic>();
        var archivePaths = _archive.Entries
            .Where(static entry => !entry.FullName.EndsWith('/'))
            .Select(static entry => SnapshotArchivePath.Normalize(entry.FullName))
            .ToArray();

        foreach (var duplicate in archivePaths.GroupBy(static path => path, StringComparer.Ordinal).Where(static group => group.Count() > 1))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.OutputCollision,
                SnapshotDiagnosticSeverity.Error,
                "The ZIP contains duplicate entries with the same exact path.",
                OutputPath: duplicate.Key));
        }

        if (!archivePaths.Contains("index.html", StringComparer.Ordinal))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "The archive root does not contain index.html."));
        }

        var manifest = await TryReadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                $"The archive does not contain {SnapshotProtocolConstants.ManifestFileName}."));
            return diagnostics;
        }

        foreach (var duplicate in manifest.Entries.GroupBy(static entry => entry.Path, StringComparer.Ordinal).Where(static group => group.Count() > 1))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.OutputCollision,
                SnapshotDiagnosticSeverity.Error,
                "The manifest contains duplicate paths.",
                OutputPath: duplicate.Key));
        }

        var manifestedPaths = manifest.Entries.Select(static entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in archivePaths.Where(path =>
                     !path.Equals(SnapshotProtocolConstants.ManifestFileName, StringComparison.Ordinal) &&
                     !manifestedPaths.Contains(path)))
        {
            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.SnapshotInvalid,
                SnapshotDiagnosticSeverity.Error,
                "The archive contains an entry that is not represented in the manifest.",
                OutputPath: extra));
        }

        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = TryGetFile(entry.Path);
            if (file is null)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    "Manifest entry is missing from the archive.",
                    OutputPath: entry.Path));
                continue;
            }

            if (file.UncompressedLength != entry.Length)
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    $"Manifest length {entry.Length} does not match archive length {file.UncompressedLength}.",
                    OutputPath: entry.Path));
            }

            await using var content = await OpenFileAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            var hash = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    SnapshotDiagnosticCodes.SnapshotInvalid,
                    SnapshotDiagnosticSeverity.Error,
                    "Manifest SHA-256 does not match the archive entry.",
                    OutputPath: entry.Path));
            }
        }

        return diagnostics;
    }
}
