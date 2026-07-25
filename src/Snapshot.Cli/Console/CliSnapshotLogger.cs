using Snapshot.Protocol.Abstractions;

namespace Snapshot.Cli.ConsoleOutput;

internal sealed class CliSnapshotLogger : ISnapshotLogger
{
    private readonly bool _verbose;

    public CliSnapshotLogger(bool verbose)
    {
        _verbose = verbose;
    }

    public void Log(SnapshotLogEntry entry)
    {
        if (entry.Level == SnapshotLogLevel.Trace && !_verbose)
        {
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = entry.Level switch
        {
            SnapshotLogLevel.Error => ConsoleColor.Red,
            SnapshotLogLevel.Warning => ConsoleColor.Yellow,
            SnapshotLogLevel.Trace => ConsoleColor.DarkGray,
            _ => ConsoleColor.Cyan
        };
        Console.WriteLine($"{entry.Level,-11} {entry.Message}");
        if (_verbose && entry.Exception is not null)
        {
            Console.WriteLine(entry.Exception);
        }
        Console.ForegroundColor = previous;
    }
}
