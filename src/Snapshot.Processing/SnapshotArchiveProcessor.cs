using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snapshot.Protocol.Archive;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Manifest;
using Snapshot.Protocol.Processing;
using Snapshot.Protocol.Protocol;
using Snapshot.Protocol.Routes;

namespace Snapshot.Processing;

public sealed record SnapshotArchiveProcessingResult(
    bool Succeeded,
    string? OutputPath,
    IReadOnlyList<SnapshotDiagnostic> Diagnostics,
    int ProcessedSnapshots,
    long OriginalSnapshotBytes,
    long ProcessedSnapshotBytes)
{
    public long SavedBytes => Math.Max(0, OriginalSnapshotBytes - ProcessedSnapshotBytes);
}

public sealed class SnapshotArchiveProcessor
{
    private const int StreamBufferSize = 1024 * 128;
    private readonly ISnapshotProcessor _processor;

    public SnapshotArchiveProcessor(SnapshotProcessingOptions? options = null)
    {
        _processor = SnapshotProcessorFactory.Create(options);
    }

    public async Task<SnapshotArchiveProcessingResult> ProcessAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (sourceFullPath.Equals(outputFullPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source and output archive paths must be different. Use a temporary destination and replace the source only after validation.", nameof(outputPath));
        }

        var temporaryPath = outputFullPath + ".processing.partial";
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath) ?? Directory.GetCurrentDirectory());
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var diagnostics = new List<SnapshotDiagnostic>();
        var processedSnapshots = 0;
        long originalSnapshotBytes = 0;
        long processedSnapshotBytes = 0;

        try
        {
            await using (var source = await SnapshotArchive.OpenAsync(sourceFullPath, cancellationToken).ConfigureAwait(false))
            {
                diagnostics.AddRange(await source.ValidateIntegrityAsync(cancellationToken).ConfigureAwait(false));
                if (HasErrors(diagnostics))
                {
                    return Failed(diagnostics, processedSnapshots, originalSnapshotBytes, processedSnapshotBytes);
                }

                var manifest = await source.TryReadManifestAsync(cancellationToken).ConfigureAwait(false);
                if (manifest is null)
                {
                    diagnostics.Add(new SnapshotDiagnostic(
                        SnapshotDiagnosticCodes.ProcessingInvalid,
                        SnapshotDiagnosticSeverity.Error,
                        "The source archive does not contain snapshot-manifest.json."));
                    return Failed(diagnostics, processedSnapshots, originalSnapshotBytes, processedSnapshotBytes);
                }

                var manifested = manifest.Entries.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
                var rewrittenEntries = new List<SnapshotManifestEntry>(manifest.Entries.Count);

                await using var file = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    StreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var destination = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);

                await foreach (var archiveFile in source.EnumerateFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (archiveFile.Path.Equals(SnapshotProtocolConstants.ManifestFileName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!manifested.TryGetValue(archiveFile.Path, out var manifestEntry))
                    {
                        diagnostics.Add(new SnapshotDiagnostic(
                            SnapshotDiagnosticCodes.ProcessingInvalid,
                            SnapshotDiagnosticSeverity.Error,
                            $"Archive entry '{archiveFile.Path}' is not represented in the manifest.",
                            OutputPath: archiveFile.Path));
                        continue;
                    }

                    if (manifestEntry.Kind == SnapshotManifestEntryKind.Snapshot && manifestEntry.Route is not null)
                    {
                        await using var input = await source.OpenFileAsync(archiveFile.Path, cancellationToken).ConfigureAwait(false);
                        using var reader = new StreamReader(
                            input,
                            Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true,
                            bufferSize: StreamBufferSize,
                            leaveOpen: false);
                        var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                        var route = SnapshotRoute.Parse(manifestEntry.Route);
                        var processed = await _processor.ProcessAsync(
                            new SnapshotProcessingContext(route, html),
                            cancellationToken).ConfigureAwait(false);
                        diagnostics.AddRange(processed.Diagnostics);

                        var (length, hash) = await WriteTextEntryAsync(destination, archiveFile.Path, processed.Html, cancellationToken).ConfigureAwait(false);
                        rewrittenEntries.Add(manifestEntry with { Length = length, Sha256 = hash });
                        processedSnapshots++;
                        originalSnapshotBytes += processed.OriginalUtf8Length;
                        processedSnapshotBytes += processed.ProcessedUtf8Length;
                        continue;
                    }

                    await using (var input = await source.OpenFileAsync(archiveFile.Path, cancellationToken).ConfigureAwait(false))
                    {
                        await CopyEntryAsync(destination, archiveFile.Path, input, cancellationToken).ConfigureAwait(false);
                    }
                    rewrittenEntries.Add(manifestEntry);
                }

                var rewrittenManifest = new SnapshotManifest
                {
                    FormatVersion = manifest.FormatVersion,
                    ProtocolVersion = manifest.ProtocolVersion,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    SiteVersion = manifest.SiteVersion,
                    TargetFilesystem = manifest.TargetFilesystem,
                    SourceFileCount = manifest.SourceFileCount,
                    CanonicalRouteCount = manifest.CanonicalRouteCount,
                    AliasCount = manifest.AliasCount,
                    GatewayCount = manifest.GatewayCount,
                    Entries = rewrittenEntries.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray()
                };

                await WriteManifestEntryAsync(destination, rewrittenManifest, cancellationToken).ConfigureAwait(false);
                destination.Dispose();
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (HasErrors(diagnostics))
            {
                File.Delete(temporaryPath);
                return Failed(diagnostics, processedSnapshots, originalSnapshotBytes, processedSnapshotBytes);
            }

            await using (var verification = await SnapshotArchive.OpenAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
            {
                diagnostics.AddRange(await verification.ValidateIntegrityAsync(cancellationToken).ConfigureAwait(false));
            }

            if (HasErrors(diagnostics))
            {
                File.Delete(temporaryPath);
                return Failed(diagnostics, processedSnapshots, originalSnapshotBytes, processedSnapshotBytes);
            }

            File.Move(temporaryPath, outputFullPath, overwrite: true);
            return new SnapshotArchiveProcessingResult(
                true,
                outputFullPath,
                diagnostics,
                processedSnapshots,
                originalSnapshotBytes,
                processedSnapshotBytes);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
        catch (Exception exception)
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            diagnostics.Add(new SnapshotDiagnostic(
                SnapshotDiagnosticCodes.ProcessingInvalid,
                SnapshotDiagnosticSeverity.Error,
                exception.Message));
            return Failed(diagnostics, processedSnapshots, originalSnapshotBytes, processedSnapshotBytes);
        }
    }

    private static SnapshotArchiveProcessingResult Failed(
        IReadOnlyList<SnapshotDiagnostic> diagnostics,
        int processedSnapshots,
        long originalSnapshotBytes,
        long processedSnapshotBytes) =>
        new(false, null, diagnostics, processedSnapshots, originalSnapshotBytes, processedSnapshotBytes);

    private static bool HasErrors(IEnumerable<SnapshotDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == SnapshotDiagnosticSeverity.Error);

    private static async Task CopyEntryAsync(
        ZipArchive archive,
        string path,
        Stream input,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        await using var output = entry.Open();
        await input.CopyToAsync(output, StreamBufferSize, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long Length, string Hash)> WriteTextEntryAsync(
        ZipArchive archive,
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        await using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        long length = 0;

        try
        {
            var offset = 0;
            var maximumChars = Math.Max(2, buffer.Length / 4);
            while (offset < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var charCount = Math.Min(maximumChars, text.Length - offset);
                if (offset + charCount < text.Length &&
                    char.IsHighSurrogate(text[offset + charCount - 1]) &&
                    char.IsLowSurrogate(text[offset + charCount]))
                {
                    charCount = charCount == 1 ? 2 : charCount - 1;
                }

                var bytesWritten = Encoding.UTF8.GetBytes(text.AsSpan(offset, charCount), buffer.AsSpan());
                hash.AppendData(buffer, 0, bytesWritten);
                await output.WriteAsync(buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
                offset += charCount;
                length += bytesWritten;
            }

            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteManifestEntryAsync(
        ZipArchive archive,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(SnapshotProtocolConstants.ManifestFileName, CompressionLevel.SmallestSize);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(
            output,
            manifest,
            SnapshotManifestJson.Options,
            cancellationToken).ConfigureAwait(false);
    }
}
