using Snapshot.Protocol.Build;
using Snapshot.Protocol.Paths;

namespace Snapshot.Protocol.Archive;

public sealed partial class SnapshotArchive
{
    public async Task ExtractToDirectoryAsync(
        string destinationDirectory,
        SnapshotExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SnapshotExtractionOptions();
        var destination = Path.GetFullPath(destinationDirectory);
        var extractionRoot = options.Atomic ? destination + $".snapshot-{Guid.NewGuid():N}.partial" : destination;

        ValidateExtraction(options.TargetFilesystem);
        if (Directory.Exists(extractionRoot))
        {
            Directory.Delete(extractionRoot, recursive: true);
        }

        Directory.CreateDirectory(extractionRoot);
        try
        {
            await foreach (var file in EnumerateFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var target = GetSafeExtractionPath(extractionRoot, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && !options.OverwriteExistingFiles)
                {
                    throw new IOException($"Extraction target already exists: {target}");
                }

                await using var input = await OpenFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (options.Atomic)
            {
                if (Directory.Exists(destination))
                {
                    if (!options.OverwriteExistingFiles)
                    {
                        throw new IOException($"Destination directory already exists: {destination}");
                    }

                    Directory.Delete(destination, recursive: true);
                }

                Directory.Move(extractionRoot, destination);
            }
        }
        catch
        {
            if (options.Atomic && Directory.Exists(extractionRoot))
            {
                Directory.Delete(extractionRoot, recursive: true);
            }

            throw;
        }
    }

    private void ValidateExtraction(SnapshotTargetFilesystem targetFilesystem)
    {
        var paths = _archive.Entries
            .Where(static entry => !entry.FullName.EndsWith('/'))
            .Select(static entry => SnapshotArchivePath.Normalize(entry.FullName))
            .ToArray();

        var comparer = targetFilesystem == SnapshotTargetFilesystem.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var duplicates = paths.GroupBy(static path => path, comparer).Where(static group => group.Count() > 1).ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidDataException($"Archive contains paths that collide under {targetFilesystem} semantics: {string.Join(", ", duplicates.Select(static group => group.Key))}");
        }

        if (targetFilesystem == SnapshotTargetFilesystem.Windows)
        {
            foreach (var path in paths)
            {
                foreach (var segment in path.Split('/'))
                {
                    if (segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                        segment.EndsWith(' ') ||
                        segment.EndsWith('.') ||
                        IsWindowsReservedName(segment))
                    {
                        throw new InvalidDataException($"Archive path is not safe for Windows extraction: {path}");
                    }
                }
            }
        }
    }


    private static bool IsWindowsReservedName(string segment)
    {
        var name = Path.GetFileNameWithoutExtension(segment).TrimEnd(' ', '.');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (name.Length == 4 &&
                (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                name[3] is >= '1' and <= '9');
    }

    private static string GetSafeExtractionPath(string root, string archivePath)
    {
        var relative = archivePath.Replace('/', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry escapes the extraction root: {archivePath}");
        }

        return target;
    }
}
