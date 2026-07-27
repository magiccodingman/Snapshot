using Snapshot.Protocol.Abstractions;
using Snapshot.Protocol.Processing;

namespace Snapshot.Protocol.Build;

public sealed class SnapshotEngineBuilder
{
    internal ISnapshotRenderer? Renderer { get; private set; }

    internal ISnapshotProcessor Processor { get; private set; } = PassthroughSnapshotProcessor.Instance;

    internal ISnapshotLogger Logger { get; private set; } = NullSnapshotLogger.Instance;

    public SnapshotEngineBuilder UseRenderer(ISnapshotRenderer renderer)
    {
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        return this;
    }

    public SnapshotEngineBuilder UseProcessor(ISnapshotProcessor processor)
    {
        Processor = processor ?? throw new ArgumentNullException(nameof(processor));
        return this;
    }

    public SnapshotEngineBuilder UseLogger(ISnapshotLogger logger)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    public SnapshotEngine Build()
    {
        if (Renderer is null)
        {
            throw new InvalidOperationException("A snapshot renderer must be configured before building the engine.");
        }

        return new SnapshotEngine(Renderer, Processor, Logger);
    }
}
