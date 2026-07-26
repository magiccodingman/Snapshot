using System.IO.Compression;
using Snapshot.Protocol.Paths;

namespace Snapshot.Protocol.Archive;

public sealed partial class SnapshotArchive : IAsyncDisposable
{
    private readonly string? _artifactPath;
    private readonly Stream _stream;
    private readonly ZipArchive _archive;
    private readonly bool _leaveOpen;
    private bool _disposed;

    private SnapshotArchive(string? artifactPath, Stream stream, bool leaveOpen)
    {
        _artifactPath = artifactPath;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    }

    public static ValueTask<SnapshotArchive> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.RandomAccess);
        return ValueTask.FromResult(new SnapshotArchive(fullPath, stream, leaveOpen: false));
    }

    public static ValueTask<SnapshotArchive> OpenAsync(Stream stream, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Snapshot ZIP streams must be readable and seekable.", nameof(stream));
        }

        return ValueTask.FromResult(new SnapshotArchive(null, stream, leaveOpen));
    }

    public bool FileExists(string path) => TryGetZipEntry(path) is not null;

    public bool DirectoryExists(string path)
    {
        var prefix = NormalizeDirectory(path);
        return _archive.Entries.Any(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal));
    }

    public SnapshotArchiveFile? TryGetFile(string path)
    {
        var entry = TryGetZipEntry(path);
        return entry is null ? null : ToFile(entry);
    }

    public IReadOnlyList<SnapshotArchiveFile> FindCaseInsensitiveMatches(string path)
    {
        var normalized = SnapshotArchivePath.Normalize(path);
        return _archive.Entries
            .Where(static entry => !entry.FullName.EndsWith('/'))
            .Where(entry => entry.FullName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(ToFile)
            .ToArray();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _archive.Dispose();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private ZipArchiveEntry? TryGetZipEntry(string path)
    {
        ThrowIfDisposed();
        var normalized = SnapshotArchivePath.Normalize(path);
        return _archive.GetEntry(normalized);
    }

    private static SnapshotArchiveFile ToFile(ZipArchiveEntry entry) =>
        new(entry.FullName, entry.Length, entry.CompressedLength, entry.LastWriteTime, MimeTypes.FromPath(entry.FullName));

    private static string NormalizeDirectory(string path) => SnapshotArchivePath.Normalize(path).TrimEnd('/') + '/';

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
