using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;

// The verify-only gate the MSBuild package invokes. Exit codes match the CLI:
// 0 pass, 1 contract violations, 2 usage/load errors.
string? contractPath = null;
string? assemblyPath = null;
var format = "text";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "verify":
            break;
        case "--contract" when i + 1 < args.Length:
            contractPath = args[++i];
            break;
        case "--assembly" when i + 1 < args.Length:
            assemblyPath = args[++i];
            break;
        case "--format" when i + 1 < args.Length:
            format = args[++i];
            break;
        default:
            Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
            return 2;
    }
}

if (contractPath is null || assemblyPath is null)
{
    Console.Error.WriteLine("usage: contractguard-gate verify --contract <file> --assembly <dll> [--format text|msbuild]");
    return 2;
}

try
{
    AssemblyContract contract = ContractJson.Load(contractPath);
    var readerOptions = new ReaderOptions
    {
        DecodeNullableAnnotations = contract.Settings.NullableAnnotations == Significance.Significant,
    };
    AssemblySurface surface = AssemblyReader.Read(assemblyPath, readerOptions);
    ComparisonResult result = ContractComparer.Compare(contract, surface);

    foreach (Diagnostic diagnostic in result.Diagnostics)
        Console.WriteLine(format == "msbuild" ? diagnostic.ToMsBuildString(contractPath) : diagnostic.ToString());

    if (result.Passed)
        Console.WriteLine($"ContractGuard: PASS ({contract.Types.Count} governed types)");
    else if (format != "msbuild")
        Console.WriteLine($"ContractGuard: FAIL ({result.Diagnostics.Count} violation(s))");

    return result.Passed ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or BadImageFormatException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
