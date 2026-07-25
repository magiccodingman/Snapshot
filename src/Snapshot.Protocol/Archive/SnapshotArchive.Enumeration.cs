namespace Snapshot.Protocol.Archive;

public sealed partial class SnapshotArchive
{
    public async IAsyncEnumerable<SnapshotArchiveFile> EnumerateFilesAsync(
        string? directory = null,
        bool recursive = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefix = directory is null ? string.Empty : NormalizeDirectory(directory);
        foreach (var entry in _archive.Entries.OrderBy(static entry => entry.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || !entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = entry.FullName[prefix.Length..];
            if (!recursive && remainder.Contains('/'))
            {
                continue;
            }

            yield return ToFile(entry);
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<SnapshotArchiveDirectory> EnumerateDirectoriesAsync(
        string? directory = null,
        bool recursive = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefix = directory is null ? string.Empty : NormalizeDirectory(directory);
        var directories = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in _archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var segments = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var count = entry.FullName.EndsWith('/') ? segments.Length : Math.Max(0, segments.Length - 1);
            for (var index = 1; index <= count; index++)
            {
                var candidate = string.Join('/', segments.Take(index));
                if (!candidate.StartsWith(prefix.TrimEnd('/'), StringComparison.Ordinal))
                {
                    continue;
                }

                var remainder = prefix.Length == 0 ? candidate : candidate[prefix.TrimEnd('/').Length..].Trim('/');
                if (!recursive && remainder.Contains('/'))
                {
                    continue;
                }

                directories.Add(candidate);
            }
        }

        foreach (var item in directories)
        {
            yield return new SnapshotArchiveDirectory(item);
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<SnapshotArchiveEntry> EnumerateEntriesAsync(
        string? directory = null,
        bool recursive = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in EnumerateDirectoriesAsync(directory, recursive, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }

        await foreach (var item in EnumerateFilesAsync(directory, recursive, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
