using System.Collections.Immutable;
using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace ContractGuard.Analyzers;

/// <summary>
/// The assistance front-end: the same comparison engine the CI gate runs, fed by symbols
/// instead of metadata, surfacing the same CG diagnostics inside the editor and inside an
/// AI agent's build loop. The contract arrives as an AdditionalFile; the file whose
/// "assembly" matches the compilation governs it. This analyzer is never the gate -
/// developers can disable analyzers - the metadata check in CI is the part nobody can opt
/// out of.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ContractGuardAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors.All;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationAction(Run);
    }

    private static void Run(CompilationAnalysisContext context)
    {
        (AssemblyContract? contract, AdditionalText? file, string? parseError) =
            LoadContract(context.Options.AdditionalFiles, context.Compilation.AssemblyName, context.CancellationToken);

        if (parseError is not null && file is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.For(Descriptors.InvalidContractId),
                Location.Create(file.Path, default, default),
                parseError));
            return;
        }

        if (contract is null)
            return;

        var options = new ReaderOptions
        {
            DecodeNullableAnnotations = contract.Settings.NullableAnnotations == Significance.Significant,
            CollectAttributes = contract.Settings.SignificantAttributes is { Count: > 0 },
            IncludeSourceLocations = true,
        };

        AssemblySurface surface = SymbolSurfaceReader.Read(context.Compilation.Assembly, options);
        ComparisonResult result = ContractComparer.Compare(contract, surface);

        foreach (Core.Comparison.Diagnostic d in result.Diagnostics)
        {
            Location location = MapLocation(context.Compilation, d.SourceLocation)
                ?? (file is not null ? Location.Create(file.Path, default, default) : Location.None);

            string message = d.ToString();
            // Strip the leading "CGxxxx: " - the id is carried by the descriptor.
            message = message[(d.Id.Length + 2)..];

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.For(d.Id), location, message));
        }
    }

    private static (AssemblyContract? Contract, AdditionalText? File, string? ParseError) LoadContract(
        ImmutableArray<AdditionalText> additionalFiles, string? assemblyName, CancellationToken cancellationToken)
    {
        AdditionalText? candidate = null;
        string? candidateError = null;

        foreach (AdditionalText file in additionalFiles)
        {
            if (!file.Path.EndsWith(".contract.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string? text = file.GetText(cancellationToken)?.ToString();
            if (text is null)
                continue;

            candidate ??= file;
            try
            {
                AssemblyContract contract = ContractJson.Parse(text);
                if (string.Equals(contract.Assembly, assemblyName, StringComparison.Ordinal))
                    return (contract, file, null);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                candidateError = ex.Message;
                candidate = file;
            }
        }

        // A contract file that fails to parse must not silently disable governance.
        return candidateError is not null ? (null, candidate, candidateError) : (null, null, null);
    }

    /// <summary>Maps the comparer's "path(line)" back to a Roslyn location.</summary>
    private static Location? MapLocation(Compilation compilation, string? sourceLocation)
    {
        if (sourceLocation is null)
            return null;

        int open = sourceLocation.LastIndexOf('(');
        if (open <= 0 || !sourceLocation.EndsWith(")", StringComparison.Ordinal))
            return null;

        string path = sourceLocation.Substring(0, open);
        if (!int.TryParse(sourceLocation.Substring(open + 1, sourceLocation.Length - open - 2), out int line))
            return null;

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            if (!string.Equals(tree.FilePath, path, StringComparison.OrdinalIgnoreCase))
                continue;

            TextLineCollection lines = tree.GetText().Lines;
            if (line - 1 < 0 || line - 1 >= lines.Count)
                return null;

            return Location.Create(tree, lines[line - 1].Span);
        }

        return null;
    }
}
