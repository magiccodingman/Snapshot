namespace Snapshot.Protocol.Abstractions;

public enum SnapshotLogLevel { Trace, Information, Warning, Error }
public sealed record SnapshotLogEntry(SnapshotLogLevel Level, string Message, Exception? Exception = null);
public interface ISnapshotLogger { void Log(SnapshotLogEntry entry); }

public sealed class NullSnapshotLogger : ISnapshotLogger
{
    public static NullSnapshotLogger Instance { get; } = new();
    private NullSnapshotLogger() { }
    public void Log(SnapshotLogEntry entry) { }
}

public sealed class ConsoleSnapshotLogger : ISnapshotLogger
{
    public void Log(SnapshotLogEntry entry)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = entry.Level switch
        {
            SnapshotLogLevel.Warning => ConsoleColor.Yellow,
            SnapshotLogLevel.Error => ConsoleColor.Red,
            SnapshotLogLevel.Trace => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        };
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {entry.Level}: {entry.Message}");
        if (entry.Exception is not null) Console.WriteLine(entry.Exception);
        Console.ForegroundColor = previous;
    }
}
