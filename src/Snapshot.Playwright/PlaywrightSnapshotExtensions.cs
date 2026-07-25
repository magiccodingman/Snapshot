using Snapshot.Playwright.Rendering;
using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Build;

namespace Snapshot.Playwright;

public static class PlaywrightSnapshotExtensions
{
    public static SnapshotEngineBuilder UsePlaywright(this SnapshotEngineBuilder builder, Action<PlaywrightSnapshotOptions>? configure = null, ISnapshotLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new PlaywrightSnapshotOptions();
        configure?.Invoke(options);
        logger ??= new ConsoleSnapshotLogger();
        return builder.UseLogger(logger).UseRenderer(new PlaywrightSnapshotRenderer(options, logger));
    }
}
