using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Snapshot.Protocol.Abstractions;

namespace Snapshot.Playwright.Setup;

internal static class PlaywrightDriverProvisioner
{
    private static readonly SemaphoreSlim ProvisionLock = new(1, 1);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    public static async Task<string> EnsureAsync(ISnapshotLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH");
        if (TryUseDriverRoot(configured, out var configuredRoot))
        {
            return configuredRoot;
        }

        await ProvisionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH");
            if (TryUseDriverRoot(configured, out configuredRoot))
            {
                return configuredRoot;
            }

            foreach (var candidate in GetExistingCandidates())
            {
                if (TryUseDriverRoot(candidate, out var existingRoot))
                {
                    logger.Log(new SnapshotLogEntry(
                        SnapshotLogLevel.Trace,
                        $"Using Playwright driver from {existingRoot}."));
                    return existingRoot;
                }
            }

            var cacheRoot = GetDriverCacheRoot();
            if (TryUseDriverRoot(cacheRoot, out var cachedRoot))
            {
                return cachedRoot;
            }

            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }

            logger.Log(new SnapshotLogEntry(
                SnapshotLogLevel.Information,
                $"The Playwright {GetPackageVersion()} driver is not available locally; downloading the matching NuGet package."));

            await DownloadAndExtractAsync(cacheRoot, logger, cancellationToken).ConfigureAwait(false);
            if (!TryUseDriverRoot(cacheRoot, out cachedRoot))
            {
                throw new InvalidOperationException(
                    $"Playwright driver provisioning completed but the required {GetNodePlatformId()} driver was not found in {cacheRoot}.");
            }

            return cachedRoot;
        }
        finally
        {
            ProvisionLock.Release();
        }
    }

    private static IEnumerable<string> GetExistingCandidates()
    {
        yield return AppContext.BaseDirectory;

        var assemblyDirectory = Path.GetDirectoryName(typeof(PlaywrightDriverProvisioner).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        var packageVersion = GetPackageVersion().ToLowerInvariant();
        var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(nugetPackages))
        {
            yield return Path.Combine(nugetPackages, "microsoft.playwright", packageVersion);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".nuget", "packages", "microsoft.playwright", packageVersion);
        }
    }

    private static bool TryUseDriverRoot(string? root, out string normalizedRoot)
    {
        normalizedRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root);
        var cliPath = Path.Combine(fullRoot, ".playwright", "package", "cli.js");
        var nodePath = GetNodeExecutablePath(fullRoot);
        if (!File.Exists(cliPath) || !File.Exists(nodePath))
        {
            return false;
        }

        EnsureNodeExecutable(nodePath);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", fullRoot);
        normalizedRoot = fullRoot;
        return true;
    }

    private static async Task DownloadAndExtractAsync(
        string cacheRoot,
        ISnapshotLogger logger,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(cacheRoot)
            ?? throw new InvalidOperationException("Could not determine the Playwright driver cache parent directory.");
        Directory.CreateDirectory(parent);

        var temporaryRoot = cacheRoot + $".partial-{Guid.NewGuid():N}";
        var temporaryPackage = temporaryRoot + ".nupkg";
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var version = GetPackageVersion().ToLowerInvariant();
            var packageUri = new Uri(
                $"https://api.nuget.org/v3-flatcontainer/microsoft.playwright/{version}/microsoft.playwright.{version}.nupkg");

            using (var response = await HttpClient.GetAsync(
                       packageUri,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(
                    temporaryPackage,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            using (var packageStream = new FileStream(
                       temporaryPackage,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       1024 * 128,
                       FileOptions.SequentialScan))
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read))
            {
                var packagePrefix = ".playwright/package/";
                var nodePrefix = $".playwright/node/{GetNodePlatformId()}/";
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryPath = entry.FullName.Replace('\\', '/');
                    if (!entryPath.StartsWith(packagePrefix, StringComparison.Ordinal) &&
                        !entryPath.StartsWith(nodePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (entryPath.EndsWith('/'))
                    {
                        continue;
                    }

                    var targetPath = GetSafeExtractionPath(temporaryRoot, entryPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    await using var source = entry.Open();
                    await using var destination = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 128,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }

            var nodePath = GetNodeExecutablePath(temporaryRoot);
            if (!File.Exists(Path.Combine(temporaryRoot, ".playwright", "package", "cli.js")) ||
                !File.Exists(nodePath))
            {
                throw new InvalidDataException(
                    $"The Microsoft.Playwright package did not contain the expected {GetNodePlatformId()} driver files.");
            }

            EnsureNodeExecutable(nodePath);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "driver.validated"),
                $"version={GetPackageVersion()}{Environment.NewLine}platform={GetNodePlatformId()}{Environment.NewLine}downloaded={DateTimeOffset.UtcNow:O}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
            Directory.Move(temporaryRoot, cacheRoot);
            logger.Log(new SnapshotLogEntry(
                SnapshotLogLevel.Information,
                $"Installed the Playwright driver into {cacheRoot}."));
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPackage))
            {
                File.Delete(temporaryPackage);
            }
        }
    }

    private static string GetSafeExtractionPath(string root, string entryPath)
    {
        var target = Path.GetFullPath(Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(
                prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Playwright package entry escapes the driver cache: {entryPath}");
        }
        return target;
    }

    private static string GetDriverCacheRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            localData = Path.Combine(userProfile, ".snapshot");
        }

        return Path.Combine(
            localData,
            "Snapshot",
            "Playwright",
            "driver",
            GetPackageVersion(),
            GetNodePlatformId());
    }

    private static string GetPackageVersion()
    {
        var metadata = typeof(PlaywrightDriverProvisioner).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute => attribute.Key == "MicrosoftPlaywrightPackageVersion")
            ?.Value;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            return metadata;
        }

        return typeof(Microsoft.Playwright.Playwright).Assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException("Could not determine the Microsoft.Playwright package version.");
    }

    private static string GetNodePlatformId()
    {
        if (OperatingSystem.IsWindows())
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw new PlatformNotSupportedException("Playwright currently requires an x64 process on Windows.");
            }
            return "win32_x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "darwin-arm64"
                : "darwin-x64";
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
        }

        throw new PlatformNotSupportedException("Snapshot Playwright supports Windows, Linux, and macOS.");
    }

    private static string GetNodeExecutablePath(string root)
    {
        var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return Path.Combine(root, ".playwright", "node", GetNodePlatformId(), executable);
    }

    private static void EnsureNodeExecutable(string nodePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            nodePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }
}
