using System.Text;
using System.Text.Json;
using Snapshot.Protocol.Manifest;

namespace Snapshot.Protocol.Archive;

public sealed partial class SnapshotArchive
{
    public ValueTask<Stream> OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = TryGetZipEntry(path) ?? throw new FileNotFoundException("The requested file does not exist in the snapshot archive.", path);
        return ValueTask.FromResult(entry.Open());
    }

    public async Task<string> ReadTextAsync(
        string path,
        int maxCharacters = 5_000_000,
        CancellationToken cancellationToken = default)
    {
        var file = TryGetFile(path) ?? throw new FileNotFoundException("The requested file does not exist in the snapshot archive.", path);
        if (file.UncompressedLength > maxCharacters * 4L)
        {
            throw new InvalidDataException($"The requested entry exceeds the configured text-read safety limit of {maxCharacters:N0} characters.");
        }

        await using var stream = await OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > maxCharacters)
        {
            throw new InvalidDataException($"The requested entry exceeds the configured text-read safety limit of {maxCharacters:N0} characters.");
        }

        return text;
    }

    public async Task<byte[]> ReadBytesAsync(
        string path,
        int maxBytes = 10_000_000,
        CancellationToken cancellationToken = default)
    {
        var file = TryGetFile(path) ?? throw new FileNotFoundException("The requested file does not exist in the snapshot archive.", path);
        if (file.UncompressedLength > maxBytes)
        {
            throw new InvalidDataException($"The requested entry exceeds the configured byte-read safety limit of {maxBytes:N0} bytes.");
        }

        await using var input = await OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream((int)file.UncompressedLength);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public async Task<SnapshotManifest?> TryReadManifestAsync(CancellationToken cancellationToken = default)
    {
        if (!FileExists("snapshot-manifest.json"))
        {
            return null;
        }

        await using var stream = await OpenFileAsync("snapshot-manifest.json", cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(
            stream,
            SnapshotManifestJson.Options,
            cancellationToken).ConfigureAwait(false);
    }
}
