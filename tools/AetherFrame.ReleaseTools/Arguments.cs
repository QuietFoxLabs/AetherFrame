using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// The command line after the verb: <c>--name value</c> options, <c>--flag</c> switches and positional
/// arguments. Every option is declared up front, so a misspelled one is an error rather than ignored.
/// </summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);
    private readonly List<string> positionals = new();

    public IReadOnlyList<string> Positionals => positionals;

    public static Arguments Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var result = new Arguments();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (flagOptions.Contains(name))
            {
                if (!result.flags.Add(name))
                {
                    throw new UsageException($"--{name} is given more than once.");
                }
            }
            else if (valueOptions.Contains(name))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"--{name} needs a value.");
                }

                if (result.values.ContainsKey(name))
                {
                    throw new UsageException($"--{name} is given more than once.");
                }

                result.values[name] = args[++i];
            }
            else
            {
                throw new UsageException($"unknown option --{name}.");
            }
        }

        return result;
    }

    public string? Optional(string name) => values.TryGetValue(name, out var value) ? value : null;

    public string Required(string name) => Optional(name) ?? throw new UsageException($"--{name} is required.");

    public bool Has(string name) => flags.Contains(name);

    public void NoPositionals()
    {
        if (positionals.Count > 0)
        {
            throw new UsageException($"unexpected argument(s): {string.Join(" ", positionals.Select(p => $"'{p}'"))}.");
        }
    }
}
