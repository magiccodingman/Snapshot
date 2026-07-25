using Snapshot.Playwright.Browser;
using Snapshot.Protocol.Abstractions;

namespace Snapshot.Playwright.Setup;

public sealed class PlaywrightBrowserInstaller
{
    private readonly ISnapshotLogger _logger;

    public PlaywrightBrowserInstaller(ISnapshotLogger logger)
    {
        _logger = logger;
    }

    public async Task EnsureInstalledAsync(PlaywrightSnapshotOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.BrowserInstallMode == BrowserInstallMode.CustomExecutable)
        {
            if (string.IsNullOrWhiteSpace(options.BrowserExecutablePath) || !File.Exists(options.BrowserExecutablePath))
            {
                throw new InvalidOperationException("CustomExecutable mode requires an existing BrowserExecutablePath.");
            }

            await ValidateLaunchAsync(options, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            // The launch is the source of truth. A marker is only a fast, human-readable
            // record and must never make a partial or deleted browser look installed.
            await ValidateLaunchAsync(options, cancellationToken).ConfigureAwait(false);
            await WriteMarkerAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && options.BrowserInstallMode == BrowserInstallMode.InstallIfMissing)
        {
            TryDeleteMarker(GetMarkerPath());
            _logger.Log(new SnapshotLogEntry(
                SnapshotLogLevel.Warning,
                "Chromium was missing or failed launch validation; installing the matching Playwright browser.",
                exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException && options.BrowserInstallMode == BrowserInstallMode.RequireExisting)
        {
            TryDeleteMarker(GetMarkerPath());
            throw new InvalidOperationException(
                "A complete matching Chromium installation was not found. Run 'snapshot browser install' or use InstallIfMissing mode.",
                exception);
        }

        await InstallCoreAsync(options.InstallLinuxDependencies, cancellationToken).ConfigureAwait(false);
        await ValidateLaunchAsync(options, cancellationToken).ConfigureAwait(false);
        await WriteMarkerAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task InstallAsync(bool withDependencies, CancellationToken cancellationToken = default)
    {
        TryDeleteMarker(GetMarkerPath());
        await InstallCoreAsync(withDependencies, cancellationToken).ConfigureAwait(false);
        await ValidateLaunchAsync(new PlaywrightSnapshotOptions
        {
            BrowserInstallMode = BrowserInstallMode.RequireExisting
        }, cancellationToken).ConfigureAwait(false);
        await WriteMarkerAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ValidateExistingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ValidateLaunchAsync(new PlaywrightSnapshotOptions
            {
                BrowserInstallMode = BrowserInstallMode.RequireExisting
            }, cancellationToken).ConfigureAwait(false);
            await WriteMarkerAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteMarker(GetMarkerPath());
            _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Warning, "Chromium installation validation failed.", exception));
            return false;
        }
    }

    public bool HasInstallationMarker() => File.Exists(GetMarkerPath());

    public string GetInstallationMarkerPath() => GetMarkerPath();

    private async Task InstallCoreAsync(bool withDependencies, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = withDependencies && OperatingSystem.IsLinux()
            ? new[] { "install", "--with-deps", "chromium" }
            : new[] { "install", "chromium" };

        _logger.Log(new SnapshotLogEntry(SnapshotLogLevel.Information, $"Running Playwright browser provisioning: {string.Join(' ', arguments)}"));
        var exitCode = Microsoft.Playwright.Program.Main(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright browser installation failed with exit code {exitCode}.");
        }
    }

    private static async Task ValidateLaunchAsync(PlaywrightSnapshotOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = options.BrowserInstallMode == BrowserInstallMode.CustomExecutable ? options.BrowserExecutablePath : null
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await browser.CloseAsync().ConfigureAwait(false);
    }

    private static async Task WriteMarkerAsync(CancellationToken cancellationToken)
    {
        var markerPath = GetMarkerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(
            markerPath,
            $"validated={DateTimeOffset.UtcNow:O}{Environment.NewLine}playwright={GetPlaywrightVersion()}{Environment.NewLine}platform={GetPlatformKey()}{Environment.NewLine}",
            cancellationToken).ConfigureAwait(false);
    }

    private static string GetMarkerPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".snapshot");
        }

        return Path.Combine(basePath, "Snapshot", "Playwright", GetPlaywrightVersion(), GetPlatformKey(), "chromium.validated");
    }

    private static string GetPlaywrightVersion() =>
        typeof(Microsoft.Playwright.Playwright).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string GetPlatformKey()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        return $"{os}-{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }

    private static void TryDeleteMarker(string markerPath)
    {
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }
}
