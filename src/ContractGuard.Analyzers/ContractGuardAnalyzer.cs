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
/// "assembly" matches the compilation governs it.
///
/// Member-level analysis runs per named type symbol - IDEs only execute symbol actions
/// during live typing, so this is what makes squiggles appear without a build. Assembly-
/// level verdicts (missing governed types, assembly mismatch) run at compilation end and
/// surface on real builds. This analyzer is never the gate - developers can disable
/// analyzers - the metadata check in CI is the part nobody can opt out of.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ContractGuardAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors.All;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(Start);
    }

    private static void Start(CompilationStartAnalysisContext context)
    {
        (AssemblyContract? contract, AdditionalText? file, string? parseError) =
            LoadContract(context.Options.AdditionalFiles, context.Compilation.AssemblyName, context.CancellationToken);

        if (parseError is not null && file is not null)
        {
            // A contract file that fails to parse must not silently disable governance.
            context.RegisterCompilationEndAction(endContext => endContext.ReportDiagnostic(
                Diagnostic.Create(
                    Descriptors.For(Descriptors.InvalidContractId),
                    Location.Create(file.Path, default, default),
                    parseError)));
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

        // Enum-name defaults ("Color.Green") resolve against enums defined in the assembly;
        // a per-type comparison still needs them in its surface.
        var enums = new Lazy<List<TypeContract>>(() => CollectEnums(context.Compilation.Assembly, options));

        context.RegisterSymbolAction(
            symbolContext => AnalyzeType(symbolContext, contract, options, enums),
            SymbolKind.NamedType);

        context.RegisterCompilationEndAction(
            endContext => AnalyzeAssemblyLevel(endContext, contract, options, file));
    }

    /// <summary>Everything member- and type-shape-level, incrementally per type.</summary>
    private static void AnalyzeType(
        SymbolAnalysisContext context, AssemblyContract contract, ReaderOptions options,
        Lazy<List<TypeContract>> enums)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;
        TypeContract? observed = SymbolSurfaceReader.ReadType(symbol, options);
        if (observed is null)
            return;

        int arity = observed.TypeParams?.Count ?? 0;
        TypeContract? governed = contract.Types.FirstOrDefault(entry =>
        {
            (string name, int entryArity) = ContractComparer.ParseGovernedTypeName(entry);
            return string.Equals(name, observed.Type, StringComparison.Ordinal) && entryArity == arity;
        });

        if (governed is null && contract.Settings.NewTypes != AllowDeny.Deny)
            return;

        var surfaceTypes = new List<TypeContract> { observed };
        surfaceTypes.AddRange(enums.Value.Where(e => !string.Equals(e.Type, observed.Type, StringComparison.Ordinal)));

        ComparisonResult result = ContractComparer.Compare(
            contract with { Types = governed is null ? [] : [governed] },
            new AssemblySurface { Name = contract.Assembly, Types = surfaceTypes });

        foreach (Core.Comparison.Diagnostic d in result.Diagnostics)
        {
            // Only this type's verdicts: the enum helpers in the mini-surface are not this
            // symbol's business, and assembly-level ids belong to the compilation end pass.
            if (!string.Equals(d.TypeName, observed.Type, StringComparison.Ordinal))
                continue;

            Location location = MapLocation(context.Compilation, d.SourceLocation)
                ?? symbol.Locations.FirstOrDefault(l => l.IsInSource)
                ?? Location.None;

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.For(d.Id), location, FormatMessage(d)));
        }
    }

    /// <summary>Whole-compilation verdicts: governed types that no longer exist, and a
    /// contract naming a different assembly. Runs on builds and full-solution analysis.</summary>
    private static void AnalyzeAssemblyLevel(
        CompilationAnalysisContext context, AssemblyContract contract, ReaderOptions options, AdditionalText? file)
    {
        AssemblySurface surface = SymbolSurfaceReader.Read(context.Compilation.Assembly, options);
        ComparisonResult result = ContractComparer.Compare(contract, surface);

        foreach (Core.Comparison.Diagnostic d in result.Diagnostics)
        {
            if (d.Id is not (DiagnosticIds.AssemblyNameMismatch or DiagnosticIds.TypeMissing))
                continue;

            Location location = MapLocation(context.Compilation, d.SourceLocation)
                ?? (file is not null ? Location.Create(file.Path, default, default) : Location.None);

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.For(d.Id), location, FormatMessage(d)));
        }
    }

    private static string FormatMessage(Core.Comparison.Diagnostic d)
    {
        // The id is carried by the descriptor and the source location by the Location;
        // the message carries the rest.
        string memberContext = (d.TypeName, d.Member) switch
        {
            (null, _) => string.Empty,
            ({ } t, null) => $" [{t}]",
            ({ } t, { } m) => $" [{t}.{m}]",
        };
        string reason = d.Reason is null ? string.Empty : $" ({d.Reason})";
        return $"{d.Message}{memberContext}{reason}";
    }

    private static List<TypeContract> CollectEnums(IAssemblySymbol assembly, ReaderOptions options)
    {
        var enums = new List<TypeContract>();
        Walk(assembly.GlobalNamespace);
        return enums;

        void Walk(INamespaceSymbol ns)
        {
            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
                Walk(child);
            foreach (INamedTypeSymbol type in ns.GetTypeMembers())
                WalkType(type);
        }

        void WalkType(INamedTypeSymbol type)
        {
            if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum
                && SymbolSurfaceReader.ReadType(type, options) is { } contract)
            {
                enums.Add(contract);
            }

            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                WalkType(nested);
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
