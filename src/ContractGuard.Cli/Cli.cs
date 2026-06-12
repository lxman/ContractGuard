using System.Text.Json;
using ContractGuard.Authoring;
using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;

namespace ContractGuard.Cli;

/// <summary>
/// Verb dispatch for the contractguard tool. Exit codes: 0 = pass, 1 = contract violations,
/// 2 = usage or load errors. Deliberately minimal argument handling for now.
/// </summary>
public static class Cli
{
    private const string SchemaUrl =
        "https://raw.githubusercontent.com/lxman/ContractGuard/main/schema/contractguard.schema.json";

    public static int Run(string[] args)
    {
        try
        {
            return args switch
            {
                ["verify", .. var rest] => Verify(Options.Parse(rest)),
                ["extract", .. var rest] => Extract(Options.Parse(rest)),
                ["show", .. var rest] => Show(Options.Parse(rest)),
                ["add", .. var rest] => Add(Options.Parse(rest)),
                ["import", .. var rest] => Import(Options.Parse(rest)),
                ["normalize", .. var rest] => Normalize(Options.Parse(rest)),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or JsonException or BadImageFormatException or FormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Verify(Options options)
    {
        string contractPath = options.Require("contract");
        string assemblyPath = options.Require("assembly");

        AssemblyContract contract = ContractJson.Load(contractPath);
        var readerOptions = new ReaderOptions
        {
            // 'ignored' is implemented by not decoding annotations; see ReaderOptions.
            DecodeNullableAnnotations = contract.Settings.NullableAnnotations == Significance.Significant,
        };
        AssemblySurface surface = AssemblyReader.Read(assemblyPath, readerOptions);
        ComparisonResult result = ContractComparer.Compare(contract, surface);

        if (options.Get("format") == "msbuild")
        {
            foreach (Diagnostic d in result.Diagnostics)
                Console.WriteLine(d.ToMsBuildString(contractPath));

            if (result.Passed)
                Console.WriteLine($"ContractGuard: PASS ({contract.Types.Count} governed types)");
        }
        else if (options.Get("format") == "json")
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
            foreach (Diagnostic diagnostic in result.Diagnostics)
                Console.WriteLine(diagnostic.ToString());

            Console.WriteLine(result.Passed
                ? $"ContractGuard: PASS ({contract.Types.Count} governed types)"
                : $"ContractGuard: FAIL ({result.Diagnostics.Count} violation(s))");
        }

        return result.Passed ? 0 : 1;
    }

    private static int Extract(Options options)
    {
        string assemblyPath = options.Require("assembly");
        AssemblySurface surface = AssemblyReader.Read(assemblyPath);

        IReadOnlyList<Accessibility> scope = ParseScope(options.Get("scope"));
        var settings = new ContractSettings { Scope = scope };
        AssemblySurface scoped = ScopeFilter.Apply(surface, scope);
        var contract = new AssemblyContract
        {
            Schema = SchemaUrl,
            Assembly = scoped.Name,
            Settings = settings,
            Types = scoped.Types,
        };

        string json = ContractJson.Serialize(contract);
        if (options.Get("output") is { } output)
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
        AssemblyContract contract = ContractJson.Load(options.Require("contract"));
        Console.WriteLine($"// {contract.Assembly} - {contract.Types.Count} governed type(s)");
        foreach (TypeContract type in contract.Types)
        {
            Console.WriteLine();
            Console.WriteLine(DeclarationRenderer.Render(type));
            foreach (MemberContract member in type.Members ?? [])
            {
                string prefix = member.Mode == EntryMode.Forbidden ? "FORBIDDEN: " : string.Empty;
                Console.WriteLine($"    {prefix}{DeclarationRenderer.Render(member, ShortName(type.Type))}");
            }
        }

        return 0;
    }

    private static int Add(Options options)
    {
        string contractPath = options.Require("contract");
        string typeName = options.Require("type");
        if (options.Positionals.Count == 0)
            throw new InvalidDataException("Give at least one C# member declaration to add.");

        AssemblyContract contract = ContractJson.Load(contractPath);
        (int index, TypeContract entry) = FindType(contract, typeName);

        EntryMode mode = options.Has("forbidden") ? EntryMode.Forbidden : EntryMode.Required;
        string? reason = options.Get("reason");

        List<MemberContract> members = (entry.Members ?? []).ToList();
        var added = 0;
        foreach (string declaration in options.Positionals)
        {
            MemberContract member = DeclarationParser.ParseMember(declaration) with { Mode = mode, Reason = reason };
            if (members.Any(m => Identity(m) == Identity(member)))
            {
                Console.WriteLine($"already present: {DeclarationRenderer.Render(member, ShortName(entry.Type))}");
                continue;
            }

            members.Add(member);
            added++;
            string prefix = mode == EntryMode.Forbidden ? "forbidden: " : "added: ";
            Console.WriteLine($"{prefix}{DeclarationRenderer.Render(member, ShortName(entry.Type))}");
        }

        if (added <= 0) return 0;
        List<TypeContract> types = contract.Types.ToList();
        TypeContract updated = entry with { Members = members };
        if (index >= 0)
            types[index] = updated;
        else
            types.Add(updated);

        ContractJson.Save(contract with { Types = types }, contractPath);

        return 0;
    }

    private static int Import(Options options)
    {
        string contractPath = options.Require("contract");
        if (options.Positionals.Count == 0)
            throw new InvalidDataException("Give at least one .cs scaffold file to import.");

        AssemblyContract contract;
        if (File.Exists(contractPath))
        {
            contract = ContractJson.Load(contractPath);
        }
        else
        {
            string assembly = options.Get("assembly") ?? throw new InvalidDataException(
                $"'{contractPath}' does not exist; pass --assembly <name> to create it.");
            contract = new AssemblyContract { Schema = SchemaUrl, Assembly = assembly, Types = [] };
        }

        foreach (string file in options.Positionals)
        {
            ImportResult result = ScaffoldImporter.Import(File.ReadAllText(file));

            List<string> usings = (contract.Usings ?? []).ToList();
            foreach (string u in result.Usings.Where(u => !usings.Contains(u)))
                usings.Add(u);

            List<TypeContract> types = contract.Types.ToList();
            var importedMembers = 0;
            foreach (TypeContract imported in result.Types)
            {
                int existingIndex = types.FindIndex(t => t.Type == imported.Type);
                if (existingIndex < 0)
                {
                    types.Add(imported);
                    importedMembers += imported.Members?.Count ?? 0;
                    continue;
                }

                TypeContract existing = types[existingIndex];
                List<MemberContract> members = (existing.Members ?? []).ToList();
                foreach (MemberContract member in imported.Members ?? [])
                {
                    if (members.Any(m => Identity(m) == Identity(member))) continue;
                    members.Add(member);
                    importedMembers++;
                }

                types[existingIndex] = existing with { Members = members.Count == 0 ? null : members };
            }

            contract = contract with { Types = types, Usings = usings.Count == 0 ? null : usings };
            Console.WriteLine($"imported {result.Types.Count} type(s), {importedMembers} new member(s) from {file}");
        }

        ContractJson.Save(contract, contractPath);
        return 0;
    }

    private static int Normalize(Options options)
    {
        string contractPath = options.Require("contract");
        string current = File.ReadAllText(contractPath);
        string canonical = ContractJson.Serialize(ContractJson.Parse(current)) + Environment.NewLine;

        if (current == canonical)
        {
            Console.WriteLine("already canonical");
            return 0;
        }

        if (options.Has("check"))
        {
            Console.WriteLine("not canonical (run 'contractguard normalize' without --check to rewrite)");
            return 1;
        }

        File.WriteAllText(contractPath, canonical);
        Console.WriteLine("normalized (note: // comments do not survive normalization)");
        return 0;
    }

    /// <summary>Finds a governed type by full name or unambiguous short name. Unknown full
    /// names create a new entry (index -1).</summary>
    private static (int Index, TypeContract Entry) FindType(AssemblyContract contract, string typeName)
    {
        List<(int Index, TypeContract Entry)> matches = contract.Types
            .Select((t, i) => (Index: i, Entry: t))
            .Where(x => x.Entry.Type == typeName || ShortName(x.Entry.Type) == typeName)
            .ToList();

        switch (matches.Count)
        {
            case > 1:
                throw new InvalidDataException(
                    $"'{typeName}' is ambiguous: {string.Join(", ", matches.Select(m => m.Entry.Type))}.");
            case 1:
                return matches[0];
        }

        if (!typeName.Contains('.'))
        {
            throw new InvalidDataException(
                $"No governed type matches '{typeName}'. Use the full name (Namespace.Type) to add a new entry.");
        }

        return (-1, new TypeContract { Type = typeName });
    }

    private static string Identity(MemberContract member)
    {
        IEnumerable<string> paramTypes = member switch
        {
            MethodContract m => m.Params?.Select(p => p.Type) ?? [],
            ConstructorMemberContract c => c.Params.Select(p => p.Type),
            IndexerContract i => i.Params.Select(p => p.Type),
            OperatorContract o => o.Params.Select(p => p.Type),
            _ => [],
        };

        return $"{member.KindName}|{member.DisplayName}|{string.Join(",", paramTypes)}";
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            ContractGuard - verifies built assemblies against a prescribed API contract.

            usage:
              contractguard verify    --contract <file> --assembly <dll> [--format text|json|msbuild]
              contractguard extract   --assembly <dll> [--output <file>] [--scope public,protected,internal,private]
              contractguard show      --contract <file>
              contractguard add       --contract <file> --type <TypeName> [--forbidden] [--reason <text>] "<C# declaration>" ...
              contractguard import    --contract <file> [--assembly <name>] <scaffold.cs> ...
              contractguard normalize --contract <file> [--check]

            exit codes: 0 pass, 1 contract violations / not canonical, 2 usage/load errors
            """);
        return 2;
    }

    private static string ShortName(string fullTypeName)
    {
        string tail = fullTypeName[(fullTypeName.LastIndexOf('+') + 1)..];
        return tail[(tail.LastIndexOf('.') + 1)..];
    }

    /// <summary>Parses a comma-separated scope list; the emitted contract's settings carry
    /// the same scope so the deny sweeps stay consistent with what was extracted.</summary>
    private static IReadOnlyList<Accessibility> ParseScope(string? text)
    {
        if (text is null)
            return new ContractSettings().Scope;

        return text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(level => level switch
            {
                "public" => Accessibility.Public,
                "protected" => Accessibility.Protected,
                "internal" => Accessibility.Internal,
                "private" => Accessibility.Private,
                _ => throw new InvalidDataException(
                    $"'{level}' is not a scope level. Allowed: public, protected, internal, private."),
            })
            .ToList();
    }

    private sealed class Options
    {
        private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal) { "forbidden", "check" };

        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public List<string> Positionals { get; } = [];

        public static Options Parse(IReadOnlyList<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Count; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    options.Positionals.Add(args[i]);
                    continue;
                }

                string name = args[i][2..];
                if (BooleanFlags.Contains(name))
                {
                    options._values[name] = "true";
                    continue;
                }

                if (i + 1 >= args.Count)
                    throw new InvalidDataException($"Option --{name} needs a value.");

                options._values[name] = args[++i];
            }

            return options;
        }

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public bool Has(string name) => _values.ContainsKey(name);

        public string Require(string name) =>
            Get(name) ?? throw new InvalidDataException($"Missing required option --{name}.");
    }
}
