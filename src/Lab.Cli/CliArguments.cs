namespace DataPerformanceLab.Cli;

public sealed class CliArguments
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Flags the command declares — which tokens take no value. Not what was passed.</summary>
    private readonly HashSet<string> _declaredFlags;

    /// <summary>Flags actually present on the command line.</summary>
    private readonly HashSet<string> _presentFlags = new(StringComparer.OrdinalIgnoreCase);

    public CliArguments(IEnumerable<string> args, IReadOnlyCollection<string> flagNames)
    {
        _declaredFlags = new HashSet<string>(flagNames, StringComparer.OrdinalIgnoreCase);

        var tokens = args as string[] ?? [.. args];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                Positional.Add(token);
                continue;
            }

            var name = token[2..];
            if (name.Length == 0)
            {
                throw new ArgumentException($"Empty option name: '{token}'");
            }

            if (_declaredFlags.Contains(name))
            {
                _presentFlags.Add(name);
                continue;
            }

            if (i + 1 >= tokens.Length || tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '--{name}' requires a value.");
            }

            _options[name] = tokens[++i];
        }
    }

    public List<string> Positional { get; } = [];

    public bool HasFlag(string name) => _presentFlags.Contains(name);

    public string? GetOption(string name) => _options.TryGetValue(name, out var value) ? value : null;
}
