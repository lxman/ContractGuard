using System.Text.Json;
using ContractGuard.Comparison;
using ContractGuard.Metadata;
using ContractGuard.Model;
using ContractGuard.Serialization;

namespace ContractGuard.Cli;

/// <summary>
/// Verb dispatch for the contractguard tool. Exit codes: 0 = pass, 1 = contract violations,
/// 2 = usage or load errors. Deliberately minimal argument handling for now.
/// </summary>
public static class Cli
{
    private const string SchemaUrl = "https://contractguard.dev/schema/v1.json";

    public static int Run(string[] args)
    {
        try
        {
            return args switch
            {
                ["verify", .. var rest] => Verify(Options.Parse(rest)),
                ["extract", .. var rest] => Extract(Options.Parse(rest)),
                ["show", .. var rest] => Show(Options.Parse(rest)),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or BadImageFormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Verify(Options options)
    {
        var contractPath = options.Require("contract");
        var assemblyPath = options.Require("assembly");

        var contract = ContractJson.Load(contractPath);
        var surface = AssemblyReader.Read(assemblyPath);
        var result = ContractComparer.Compare(contract, surface);

        if (options.Get("format") == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    passed = result.Passed,
                    diagnostics = result.Diagnostics.Select(d => new
                    {
                        id = d.Id,
                        severity = d.Severity.ToString().ToLowerInvariant(),
                        message = d.Message,
                        type = d.TypeName,
                        member = d.Member,
                        reason = d.Reason,
                    }),
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
                Console.WriteLine(diagnostic.ToString());

            Console.WriteLine(result.Passed
                ? $"ContractGuard: PASS ({contract.Types.Count} governed types)"
                : $"ContractGuard: FAIL ({result.Diagnostics.Count} violation(s))");
        }

        return result.Passed ? 0 : 1;
    }

    private static int Extract(Options options)
    {
        var assemblyPath = options.Require("assembly");
        var surface = AssemblyReader.Read(assemblyPath);

        var settings = new ContractSettings();
        var scoped = ScopeFilter.Apply(surface, settings.Scope);
        var contract = new AssemblyContract
        {
            Schema = SchemaUrl,
            Assembly = scoped.Name,
            Settings = settings,
            Types = scoped.Types,
        };

        var json = ContractJson.Serialize(contract);
        if (options.Get("output") is string output)
        {
            File.WriteAllText(output, json + Environment.NewLine);
            Console.WriteLine($"Wrote {output} ({scoped.Types.Count} types).");
        }
        else
        {
            Console.WriteLine(json);
        }

        return 0;
    }

    private static int Show(Options options)
    {
        var contract = ContractJson.Load(options.Require("contract"));
        Console.WriteLine($"// {contract.Assembly} - {contract.Types.Count} governed type(s)");
        foreach (var type in contract.Types)
        {
            Console.WriteLine();
            Console.WriteLine(DeclarationRenderer.Render(type));
            foreach (var member in type.Members ?? [])
            {
                var prefix = member.Mode == EntryMode.Forbidden ? "FORBIDDEN: " : string.Empty;
                Console.WriteLine($"    {prefix}{DeclarationRenderer.Render(member, ShortName(type.Type))}");
            }
        }

        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            ContractGuard - verifies built assemblies against a prescribed API contract.

            usage:
              contractguard verify  --contract <file> --assembly <dll> [--format text|json]
              contractguard extract --assembly <dll> [--output <file>]
              contractguard show    --contract <file>

            exit codes: 0 pass, 1 contract violations, 2 usage/load errors
            """);
        return 2;
    }

    private static string ShortName(string fullTypeName)
    {
        var tail = fullTypeName[(fullTypeName.LastIndexOf('+') + 1)..];
        return tail[(tail.LastIndexOf('.') + 1)..];
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public static Options Parse(IReadOnlyList<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Count; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
                    throw new InvalidDataException($"Unexpected argument '{args[i]}'.");

                options._values[args[i][2..]] = args[++i];
            }

            return options;
        }

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public string Require(string name) =>
            Get(name) ?? throw new InvalidDataException($"Missing required option --{name}.");
    }
}
