using System.Collections.Immutable;
using ContractGuard.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ContractGuard.Core.Tests;

/// <summary>
/// End-to-end through the Roslyn analyzer: contract via AdditionalFiles, diagnostics with
/// the same CG ids as the gate, located on the offending source.
/// </summary>
public class AnalyzerTests
{
    private const string Contract = """
        {
            "assembly": "Shop",
            "types": [
                {
                    "type": "Shop.Calc",
                    "members": [
                        { "kind": "method", "name": "Add", "returns": "int",
                          "params": [["int", "a"], ["int", "b"]] }
                    ]
                }
            ]
        }
        """;

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(string source, string contractJson)
    {
        CSharpCompilation compilation = TestCompiler.Compile(source, "Shop");
        var options = new AnalyzerOptions([new TestAdditionalText("Shop.contract.json", contractJson)]);
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [new ContractGuardAnalyzer()], options);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task Stays_silent_when_the_contract_is_honored()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            "namespace Shop { public class Calc { public int Add(int a, int b) => a + b; } }", Contract);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Reports_drift_with_the_gate_vocabulary_at_the_offending_line()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            """
            namespace Shop
            {
                public class Calc
                {
                    public long Add(int a, int b) => a + b;
                }
            }
            """, Contract);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CG0204", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("prescribes 'int'", diagnostic.GetMessage());

        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        Assert.Equal("TestSnippet.cs", span.Path);
        Assert.Equal(4, span.StartLinePosition.Line); // 0-based: the 'public long Add' line
    }

    [Fact]
    public async Task Honors_interface_closures_extracted_from_metadata()
    {
        // Field regression: a contract extracted from metadata prescribes the full
        // interface closure (IQueryableStore -> IStore -> IDisposable); the analyzer must
        // not report CG0104 for the inherited entries.
        var contract = """
            {
                "assembly": "Shop",
                "usings": ["System"],
                "types": [
                    {
                        "type": "Shop.RoleStore",
                        "kind": "class",
                        "implements": ["Shop.IQueryableStore", "Shop.IStore", "IDisposable"]
                    }
                ]
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;
            namespace Shop
            {
                public interface IStore : IDisposable { }
                public interface IQueryableStore : IStore { }
                public class RoleStore : IQueryableStore
                {
                    public void Dispose() { }
                }
            }
            """, contract);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Stays_silent_without_a_matching_contract()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            "namespace Shop { public class Calc { } }",
            Contract.Replace("\"Shop\"", "\"OtherAssembly\""));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Reports_an_unparseable_contract_instead_of_silently_ungoverning()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            "namespace Shop { public class Calc { } }",
            """{ "assembly": "Shop", "types": [ { "tpye": "oops" } ] }""");

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CG0002", diagnostic.Id);
    }

    private sealed class TestAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(text);
    }
}
