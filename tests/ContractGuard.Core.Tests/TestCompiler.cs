using ContractGuard.Core.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ContractGuard.Core.Tests;

/// <summary>
/// The project's core test harness: compile a snippet in-memory with Roslyn, then read the
/// emitted PE bytes with the metadata reader. When the Roslyn ISymbol front-end arrives
/// (phase 2), the same snippets assert that both front-ends produce identical models.
/// </summary>
internal static class TestCompiler
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToList());

    public static AssemblySurface CompileAndRead(
        string source,
        string assemblyName = "TestLib",
        ReaderOptions? readerOptions = null,
        bool nullableEnable = true)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "TestSnippet.cs")],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: nullableEnable ? NullableContextOptions.Enable : NullableContextOptions.Disable));

        using var stream = new MemoryStream();
        // Embedded portable PDB so SourceLocator resolves member locations in tests.
        EmitResult emit = compilation.Emit(stream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!emit.Success)
        {
            string errors = string.Join(Environment.NewLine,
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Test snippet failed to compile:{Environment.NewLine}{errors}");
        }

        stream.Position = 0;
        return AssemblyReader.Read(stream, readerOptions ?? ReaderOptions.Default);
    }
}
