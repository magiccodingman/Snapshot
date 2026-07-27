using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Diagnostics;
using Snapshot.Protocol.Hosting;
using Snapshot.Protocol.Hosting.Netlify;
using Snapshot.Protocol.Manifest;
using Snapshot.Protocol.Metadata;
using Snapshot.Protocol.Processing;
using Snapshot.Protocol.Protocol;

namespace Snapshot.Protocol.Output;

public sealed record SnapshotZipWriteResult(
    SnapshotManifest? Manifest,
    string TemporaryPath,
    IReadOnlyList<SnapshotRenderResult> RenderResults,
    IReadOnlyList<SnapshotDiagnostic> Diagnostics);

public sealed class SnapshotZipWriter
{
    private const int StreamBufferSize = 1024 * 128;

    private readonly ISnapshotLogger _logger;
    private readonly ISnapshotProcessor _processor;

    public SnapshotZipWriter(ISnapshotLogger logger, ISnapshotProcessor processor)
    {
        _logger = logger;
        _processor = processor;
    }

    public async Task<SnapshotZipWriteResult> WriteAsync(
        SnapshotBuildRequest request,
        SnapshotOutputPlan plan,
        IAsyncEnumerable<SnapshotRenderResult> renderResults,
        string outputPath,
        string? siteVersion,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = outputPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var manifestEntries = new List<SnapshotManifestEntry>();
        var renderSummaries = new List<SnapshotRenderResult>();
        var processingDiagnostics = new List<SnapshotDiagnostic>();
        var written = new HashSet<string>(StringComparer.Ordinal);
        var canonicalByOutputPath = plan.CanonicalRoutes.ToDictionary(static route => route.OutputPath.Value, StringComparer.Ordinal);
        var snapshotsByRoute = plan.GeneratedEntries
            .Where(static entry => entry.Kind == SnapshotGeneratedEntryKind.Snapshot && entry.Route is not null)
            .ToDictionary(static entry => entry.Route!, StringComparer.Ordinal);

        var sitemapAnnotations = await SnapshotSitemapAnnotator.CreateAsync(
            plan.SourceDirectory,
            plan.CanonicalRoutes,
            cancellationToken).ConfigureAwait(false);
        processingDiagnostics.AddRange(sitemapAnnotations.Diagnostics);

        await using var file = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);

        var outputFullPath = Path.GetFullPath(outputPath);
        var temporaryFullPath = Path.GetFullPath(temporaryPath);
        var netlify = request.Hosting.Provider == SnapshotHostingProvider.Netlify;

        var completed = 0;
        var total = plan.SourceEntries.Count + plan.GeneratedEntries.Count + (netlify ? 2 : 0) + 1;

        foreach (var sourceFile in Directory.EnumerateFiles(plan.SourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFullPath = Path.GetFullPath(sourceFile);
            if (sourceFullPath.Equals(outputFullPath, StringComparison.Ordinal) || sourceFullPath.Equals(temporaryFullPath, StringComparison.Ordinal))
            {
                continue;
            }

            var archivePath = Paths.SnapshotArchivePath.Normalize(Path.GetRelativePath(plan.SourceDirectory, sourceFile));
            if (string.Equals(archivePath, SnapshotProtocolConstants.ManifestFileName, StringComparison.Ordinal) ||
                (netlify && archivePath is "_headers" or "_redirects"))
            {
                continue;
            }

            if (!written.Add(archivePath))
            {
                continue;
            }

            (long Length, string Hash) sourceResult;
            if (sitemapAnnotations.Entries.TryGetValue(archivePath, out var annotatedSitemap))
            {
                await using var input = new MemoryStream(annotatedSitemap, writable: false);
                sourceResult = await WriteStreamEntryAsync(archive, archivePath, input, cancellationToken).ConfigureAwait(false);
            }
            else if (archivePath.Equals("index.html", StringComparison.Ordinal))
            {
                var sourceHtml = await File.ReadAllTextAsync(sourceFile, cancellationToken).ConfigureAwait(false);
                var annotation = SnapshotHtmlMetadataAnnotator.Annotate(sourceHtml);
                if (annotation.FailureReason is not null)
                {
                    processingDiagnostics.Add(new SnapshotDiagnostic(
                        SnapshotDiagnosticCodes.HtmlMetadataPreserved,
                        SnapshotDiagnosticSeverity.Warning,
                        $"Root index.html rendering metadata was not added because {annotation.FailureReason}",
                        "/",
                        sourceFile,
                        archivePath,
                        "The source loader was copied unchanged into the artifact."));
                }

                if (annotation.Changed)
                {
                    sourceResult = await WriteTextEntryAsync(archive, archivePath, annotation.Html, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    sourceResult = await WriteStreamEntryAsync(archive, archivePath, input, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                sourceResult = await WriteStreamEntryAsync(archive, archivePath, input, cancellationToken).ConfigureAwait(false);
            }

            var canonicalRoute = canonicalByOutputPath.TryGetValue(archivePath, out var manualRoute)
                ? manualRoute.Path
                : null;
            manifestEntries.Add(new SnapshotManifestEntry(
                archivePath,
                SnapshotManifestEntryKind.Source,
                sourceResult.Length,
                sourceResult.Hash,
                Route: canonicalRoute));
            progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Copied {archivePath}", ++completed, total));
        }

        var renderedRoutes = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var rendered in renderResults.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!renderedRoutes.Add(rendered.Route.Path))
            {
                throw new InvalidOperationException($"The renderer returned route {rendered.Route.Path} more than once.");
            }

            if (!rendered.Succeeded)
            {
                renderSummaries.Add(rendered with { Html = null });
                continue;
            }

            if (rendered.Html is null)
            {
                throw new InvalidOperationException($"The renderer marked {rendered.Route.Path} successful without returning HTML.");
            }

            if (!snapshotsByRoute.TryGetValue(rendered.Route.Path, out var planned))
            {
                throw new InvalidOperationException($"The renderer returned an unplanned route: {rendered.Route.Path}.");
            }

            if (!written.Add(planned.ArchivePath))
            {
                throw new InvalidOperationException($"Duplicate generated snapshot output: {planned.ArchivePath}.");
            }

            var processed = await _processor.ProcessAsync(
                new SnapshotProcessingContext(rendered.Route, rendered.Html),
                cancellationToken).ConfigureAwait(false);
            processingDiagnostics.AddRange(processed.Diagnostics);

            var processingError = processed.Diagnostics.FirstOrDefault(static diagnostic =>
                diagnostic.Severity == SnapshotDiagnosticSeverity.Error);
            if (processingError is not null)
            {
                renderSummaries.Add(rendered with
                {
                    Succeeded = false,
                    Html = null,
                    ErrorCode = processingError.Code,
                    ErrorMessage = processingError.Message
                });
                continue;
            }

            renderSummaries.Add(rendered with { Html = null });
            var (length, hash) = await WriteTextEntryAsync(archive, planned.ArchivePath, processed.Html, cancellationToken).ConfigureAwait(false);
            manifestEntries.Add(new SnapshotManifestEntry(
                planned.ArchivePath,
                SnapshotManifestEntryKind.Snapshot,
                length,
                hash,
                planned.Route));
            progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Wrote {planned.ArchivePath}", ++completed, total, planned.Route));
        }

        foreach (var missingRoute in plan.RoutesToRender.Where(route => !renderedRoutes.Contains(route.Path)))
        {
            renderSummaries.Add(new SnapshotRenderResult(
                missingRoute,
                false,
                null,
                0,
                TimeSpan.Zero,
                SnapshotDiagnosticCodes.BrowserFailure,
                "No browser worker returned a result."));
        }

        if (renderSummaries.Any(static result => !result.Succeeded))
        {
            archive.Dispose();
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new SnapshotZipWriteResult(null, temporaryPath, renderSummaries, processingDiagnostics);
        }

        foreach (var planned in plan.GeneratedEntries.Where(static entry => entry.Kind != SnapshotGeneratedEntryKind.Snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.SourceEntries.Contains(planned.ArchivePath) || !written.Add(planned.ArchivePath))
            {
                continue;
            }

            var content = planned.Content ?? throw new InvalidOperationException($"Generated entry {planned.ArchivePath} had no content.");
            var (length, hash) = await WriteTextEntryAsync(archive, planned.ArchivePath, content, cancellationToken).ConfigureAwait(false);
            manifestEntries.Add(new SnapshotManifestEntry(
                planned.ArchivePath,
                MapKind(planned.Kind),
                length,
                hash,
                planned.Route,
                planned.CanonicalRoute));
            progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Generated {planned.ArchivePath}", ++completed, total, planned.Route));
        }

        if (netlify)
        {
            var artifacts = NetlifyArtifactGenerator.Generate(plan.SourceDirectory, plan.CanonicalRoutes, plan.GeneratedEntries);
            foreach (var artifact in new[] { (Path: "_headers", Content: artifacts.Headers), (Path: "_redirects", Content: artifacts.Redirects) })
            {
                if (!written.Add(artifact.Path))
                {
                    throw new InvalidOperationException($"Duplicate provider artifact: {artifact.Path}");
                }

                var (length, hash) = await WriteTextEntryAsync(archive, artifact.Path, artifact.Content, cancellationToken).ConfigureAwait(false);
                manifestEntries.Add(new SnapshotManifestEntry(artifact.Path, SnapshotManifestEntryKind.HostingArtifact, length, hash));
                progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Generated {artifact.Path}", ++completed, total));
            }
        }

        var manifest = new SnapshotManifest
        {
            SiteVersion = siteVersion,
            TargetFilesystem = request.TargetFilesystem == SnapshotTargetFilesystem.Windows ? "windows" : "case-sensitive",
            SourceFileCount = manifestEntries.Count(static entry => entry.Kind == SnapshotManifestEntryKind.Source),
            CanonicalRouteCount = plan.CanonicalRoutes.Count,
            AliasCount = manifestEntries.Count(static entry => entry.Kind == SnapshotManifestEntryKind.CaseAlias),
            GatewayCount = manifestEntries.Count(static entry => entry.Kind == SnapshotManifestEntryKind.PrefixGateway),
            Entries = manifestEntries.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray()
        };

        await WriteManifestEntryAsync(archive, manifest, cancellationToken).ConfigureAwait(false);
        progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Wrote {SnapshotProtocolConstants.ManifestFileName}", ++completed, total));

        archive.Dispose();
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Information, $"Created temporary artifact {temporaryPath}."));
        return new SnapshotZipWriteResult(manifest, temporaryPath, renderSummaries, processingDiagnostics);
    }

    private static SnapshotManifestEntryKind MapKind(SnapshotGeneratedEntryKind kind) => kind switch
    {
        SnapshotGeneratedEntryKind.Snapshot => SnapshotManifestEntryKind.Snapshot,
        SnapshotGeneratedEntryKind.CaseAlias => SnapshotManifestEntryKind.CaseAlias,
        SnapshotGeneratedEntryKind.PrefixGateway => SnapshotManifestEntryKind.PrefixGateway,
        SnapshotGeneratedEntryKind.HostingArtifact => SnapshotManifestEntryKind.HostingArtifact,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

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

    private static async Task<(long Length, string Hash)> WriteStreamEntryAsync(
        ZipArchive archive,
        string path,
        Stream input,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        await using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        long length = 0;

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length += read;
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
