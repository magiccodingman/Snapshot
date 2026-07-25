using Snapshot.Playwright.Browser;

namespace Snapshot.Playwright;

public sealed class PlaywrightSnapshotOptions
{
    public BrowserInstallMode BrowserInstallMode { get; set; } = BrowserInstallMode.InstallIfMissing;

    public string? BrowserExecutablePath { get; set; }

    public bool Headless { get; set; } = true;

    public int? Concurrency { get; set; }

    public bool InstallLinuxDependencies { get; set; }
}
