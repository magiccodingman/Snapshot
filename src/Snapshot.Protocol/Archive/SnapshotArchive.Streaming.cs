using Snapshot.Protocol.Abstractions;

namespace Snapshot.Protocol.Archive;

public sealed partial class SnapshotArchive
{
    public ValueTask<Stream> OpenArtifactReadStreamAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_artifactPath is null)
        {
            throw new InvalidOperationException("The archive was opened from a stream and has no independently reopenable artifact path.");
        }

        Stream stream = new FileStream(_artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public async Task CopyArtifactToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var input = await OpenArtifactReadStreamAsync(cancellationToken).ConfigureAwait(false);
        await input.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task StreamFilesAsync(
        Func<SnapshotFileStreamContext, ValueTask> handler,
        SnapshotStreamFilesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new SnapshotStreamFilesOptions();
        if (options.DegreeOfParallelism <= 1 || _artifactPath is null)
        {
            await foreach (var file in EnumerateFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                await using var content = await OpenFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
                await handler(new SnapshotFileStreamContext
                {
                    File = file,
                    Content = content,
                    CancellationToken = cancellationToken
                }).ConfigureAwait(false);
            }

            return;
        }

        var files = new List<SnapshotArchiveFile>();
        await foreach (var file in EnumerateFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            files.Add(file);
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = options.DegreeOfParallelism },
            async (file, token) =>
            {
                await using var independent = await OpenAsync(_artifactPath, token).ConfigureAwait(false);
                await using var content = await independent.OpenFileAsync(file.Path, token).ConfigureAwait(false);
                await handler(new SnapshotFileStreamContext
                {
                    File = file,
                    Content = content,
                    CancellationToken = token
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    public async Task CopyToAsync(ISnapshotFileSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        await StreamFilesAsync(
            context => sink.WriteFileAsync(context.File, context.Content, context.CancellationToken),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
