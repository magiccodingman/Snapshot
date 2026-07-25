namespace Snapshot.Cli.Commands;

internal sealed class CommandLine
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, List<string?>> _options = new(StringComparer.OrdinalIgnoreCase);

    public CommandLine(IEnumerable<string> args)
    {
        var values = args.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                _positionals.Add(value);
                continue;
            }

            var equals = value.IndexOf('=', StringComparison.Ordinal);
            string name;
            string? optionValue;
            if (equals >= 0)
            {
                name = value[2..equals];
                optionValue = value[(equals + 1)..];
            }
            else
            {
                name = value[2..];
                optionValue = index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? values[++index]
                    : null;
            }

            if (!_options.TryGetValue(name, out var entries))
            {
                entries = [];
                _options[name] = entries;
            }
            entries.Add(optionValue);
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var values) ? values.LastOrDefault() : null;

    public IReadOnlyList<string> GetMany(string name) => _options.TryGetValue(name, out var values)
        ? values.Where(static value => value is not null).Cast<string>().ToArray()
        : [];

    public int? GetInt(string name) => int.TryParse(Get(name), out var value) ? value : null;

    public double? GetDouble(string name) => double.TryParse(Get(name), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}
