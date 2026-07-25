using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;
using Snapshot.Protocol.Hosting;
using Snapshot.Protocol.Hosting.Netlify;
using Snapshot.Protocol.Manifest;
using Snapshot.Protocol.Protocol;

namespace Snapshot.Protocol.Output;

public sealed class SnapshotZipWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ISnapshotLogger _logger;

    public SnapshotZipWriter(ISnapshotLogger logger)
    {
        _logger = logger;
    }

    public async Task<(SnapshotManifest Manifest, string TemporaryPath)> WriteAsync(
        SnapshotBuildRequest request,
        SnapshotOutputPlan plan,
        IReadOnlyList<SnapshotRenderResult> renderResults,
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
        var written = new HashSet<string>(StringComparer.Ordinal);
        var renderByRoute = renderResults.ToDictionary(static result => result.Route.Path, StringComparer.Ordinal);
        var canonicalByOutputPath = plan.CanonicalRoutes.ToDictionary(static route => route.OutputPath.Value, StringComparer.Ordinal);

        await using var file = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);

        var sourceFiles = Directory.EnumerateFiles(plan.SourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        var outputFullPath = Path.GetFullPath(outputPath);
        var temporaryFullPath = Path.GetFullPath(temporaryPath);
        var netlify = request.Hosting.Provider == SnapshotHostingProvider.Netlify;

        var completed = 0;
        var total = sourceFiles.Length + plan.GeneratedEntries.Count + (netlify ? 2 : 0) + 1;

        foreach (var sourceFile in sourceFiles)
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

            await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var (length, hash) = await WriteStreamEntryAsync(archive, archivePath, input, cancellationToken).ConfigureAwait(false);
            var canonicalRoute = canonicalByOutputPath.TryGetValue(archivePath, out var manualRoute)
                ? manualRoute.Path
                : null;
            manifestEntries.Add(new SnapshotManifestEntry(
                archivePath,
                SnapshotManifestEntryKind.Source,
                length,
                hash,
                Route: canonicalRoute));
            progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Copied {archivePath}", ++completed, total));
        }

        foreach (var planned in plan.GeneratedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.SourceEntries.Contains(planned.ArchivePath) || !written.Add(planned.ArchivePath))
            {
                continue;
            }

            string content;
            if (planned.Kind == SnapshotGeneratedEntryKind.Snapshot)
            {
                if (planned.Route is null || !renderByRoute.TryGetValue(planned.Route, out var rendered) || !rendered.Succeeded || rendered.Html is null)
                {
                    throw new InvalidOperationException($"No successful render was available for {planned.Route}.");
                }

                content = rendered.Html;
            }
            else
            {
                content = planned.Content ?? throw new InvalidOperationException($"Generated entry {planned.ArchivePath} had no content.");
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            var (length, hash) = await WriteBytesEntryAsync(archive, planned.ArchivePath, bytes, cancellationToken).ConfigureAwait(false);
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

                var bytes = Encoding.UTF8.GetBytes(artifact.Content);
                var (length, hash) = await WriteBytesEntryAsync(archive, artifact.Path, bytes, cancellationToken).ConfigureAwait(false);
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

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await WriteBytesEntryAsync(archive, SnapshotProtocolConstants.ManifestFileName, manifestBytes, cancellationToken).ConfigureAwait(false);
        progress?.Report(new SnapshotProgress(SnapshotProgressStage.WritingArchive, $"Wrote {SnapshotProtocolConstants.ManifestFileName}", ++completed, total));

        archive.Dispose();
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Information, $"Created temporary artifact {temporaryPath}."));
        return (manifest, temporaryPath);
    }

    private static SnapshotManifestEntryKind MapKind(SnapshotGeneratedEntryKind kind) => kind switch
    {
        SnapshotGeneratedEntryKind.Snapshot => SnapshotManifestEntryKind.Snapshot,
        SnapshotGeneratedEntryKind.CaseAlias => SnapshotManifestEntryKind.CaseAlias,
        SnapshotGeneratedEntryKind.PrefixGateway => SnapshotManifestEntryKind.PrefixGateway,
        SnapshotGeneratedEntryKind.HostingArtifact => SnapshotManifestEntryKind.HostingArtifact,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static async Task<(long Length, string Hash)> WriteBytesEntryAsync(
        ZipArchive archive,
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        await using var output = entry.Open();
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        return (bytes.Length, Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
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
        var buffer = new byte[1024 * 128];
        long length = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
}
