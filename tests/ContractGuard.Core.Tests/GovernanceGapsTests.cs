using ContractGuard.Authoring;
using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;

namespace ContractGuard.Core.Tests;

/// <summary>
/// The last governance gaps: significant attributes, explicit interface implementations,
/// constraint/inheritance nullability, and PDB source locations on diagnostics.
/// </summary>
public class GovernanceGapsTests
{
    private static ComparisonResult Compare(string contractJson, AssemblySurface surface) =>
        ContractComparer.Compare(ContractJson.Parse(contractJson), surface);

    [Fact]
    public void Significant_attributes_enforce_presence_in_both_directions()
    {
        var surface = TestCompiler.CompileAndRead("""
            using System;
            namespace Shop
            {
                public class Calc
                {
                    [Obsolete] public int Old(int a) => a;
                    public int Fresh(int a) => a;
                }
            }
            """, "Shop", new ReaderOptions { CollectAttributes = true });

        var contract = """
            {
                "assembly": "Shop",
                "usings": ["System"],
                "settings": { "significantAttributes": ["Obsolete"] },
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "method", "name": "Old", "returns": "int",
                              "params": [["int", "a"]], "attributes": ["Obsolete"] },
                            { "kind": "method", "name": "Fresh", "returns": "int",
                              "params": [["int", "a"]] }
                        ]
                    }
                ]
            }
            """;
        Assert.True(Compare(contract, surface).Passed, string.Join("; ", Compare(contract, surface).Diagnostics));

        // Prescribed but absent, and present but unprescribed, both fail.
        var swapped = contract
            .Replace(", \"attributes\": [\"Obsolete\"]", "")
            .Replace("\"params\": [[\"int\", \"a\"]] }\n", "\"params\": [[\"int\", \"a\"]], \"attributes\": [\"Obsolete\"] }\n");
        ComparisonResult result = Compare(swapped, surface);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == DiagnosticIds.AttributesMismatch));
    }

    [Fact]
    public void Insignificant_attributes_are_invisible()
    {
        var surface = TestCompiler.CompileAndRead("""
            using System;
            namespace Shop { public class Calc { [Obsolete] public int Old(int a) => a; } }
            """, "Shop", new ReaderOptions { CollectAttributes = true });

        var contract = """
            {
                "assembly": "Shop",
                "settings": { "significantAttributes": ["Serializable"] },
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "method", "name": "Old", "returns": "int", "params": [["int", "a"]] }
                        ]
                    }
                ]
            }
            """;

        Assert.True(Compare(contract, surface).Passed);
    }

    private const string ExplicitSource = """
        using System;
        namespace Shop
        {
            public class Resource : IDisposable
            {
                void IDisposable.Dispose() { }
                public void Dispose(bool disposing) { }
            }
        }
        """;

    [Fact]
    public void Reads_explicit_interface_implementations()
    {
        AssemblySurface surface = TestCompiler.CompileAndRead(ExplicitSource, "Shop");
        TypeContract resource = surface.Types.Single(t => t.Type == "Shop.Resource");
        var methods = resource.Members!.OfType<MethodContract>().ToList();

        MethodContract explicitDispose = methods.Single(m => m.ExplicitInterface is not null);
        Assert.Equal("Dispose", explicitDispose.Name);
        Assert.Equal("System.IDisposable", explicitDispose.ExplicitInterface);
        Assert.Null(explicitDispose.Modifiers);

        Assert.Single(methods, m => m is { Name: "Dispose", ExplicitInterface: null });
    }

    [Fact]
    public void Governs_explicit_implementations_distinctly_from_ordinary_members()
    {
        AssemblySurface surface = TestCompiler.CompileAndRead(ExplicitSource, "Shop");

        var contract = """
            {
                "assembly": "Shop",
                "usings": ["System"],
                "types": [
                    {
                        "type": "Shop.Resource",
                        "members": [
                            { "kind": "method", "name": "Dispose", "explicitInterface": "IDisposable",
                              "returns": "void", "params": [] }
                        ]
                    }
                ]
            }
            """;
        Assert.True(Compare(contract, surface).Passed,
            string.Join("; ", Compare(contract, surface).Diagnostics));

        // Switching to an implicit implementation does not satisfy a prescribed explicit
        // one - the closest-candidate pairing reports what changed.
        AssemblySurface without = TestCompiler.CompileAndRead(
            ExplicitSource.Replace("void IDisposable.Dispose() { }", "public void Dispose() { }"), "Shop");
        ComparisonResult result = Compare(contract, without);
        Assert.Contains(DiagnosticIds.MemberSignatureChanged, result.Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void Parses_explicit_interface_declarations()
    {
        var method = (MethodContract)DeclarationParser.ParseMember("void IDisposable.Dispose()");

        Assert.Equal("Dispose", method.Name);
        Assert.Equal("IDisposable", method.ExplicitInterface);
        Assert.Equal("void IDisposable.Dispose()", DeclarationRenderer.Render(method));
    }

    [Fact]
    public void Decodes_constraint_type_and_inheritance_nullability()
    {
        AssemblySurface surface = TestCompiler.CompileAndRead("""
            using System;
            using System.Collections.Generic;
            namespace Nrt
            {
                public class Holder : List<string?>
                {
                    public void Compare<T>(T value) where T : IComparable<string?> { }
                }

                public class Sink : IObserver<string?>
                {
                    public void OnCompleted() { }
                    public void OnError(Exception error) { }
                    public void OnNext(string? value) { }
                }
            }
            """, "Nrt");

        TypeContract holder = surface.Types.Single(t => t.Type == "Nrt.Holder");
        Assert.Equal("System.Collections.Generic.List<string?>", holder.Extends);

        MethodContract compare = holder.Members!.OfType<MethodContract>().Single(m => m.Name == "Compare");
        Assert.Equal(["System.IComparable<string?>"], compare.TypeParams!.Single().Constraints);

        TypeContract sink = surface.Types.Single(t => t.Type == "Nrt.Sink");
        Assert.Contains("System.IObserver<string?>", sink.Implements!);
    }

    [Fact]
    public void Diagnostics_carry_pdb_source_locations()
    {
        AssemblySurface surface = TestCompiler.CompileAndRead("""
            namespace Shop
            {
                public class Calc
                {
                    public long Add(int a, int b) => a + b;
                }
            }
            """, "Shop");

        var contract = """
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

        ComparisonResult result = Compare(contract, surface);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.StartsWith("TestSnippet.cs(", diagnostic.SourceLocation);
        Assert.StartsWith("TestSnippet.cs(", diagnostic.ToMsBuildString("fallback.contract.json"));
        Assert.Contains("@ TestSnippet.cs(", diagnostic.ToString());
    }

    [Fact]
    public void Diagnostics_fall_back_to_the_contract_origin_without_a_pdb()
    {
        var diagnostic = new Diagnostic("CG0200", DiagnosticSeverity.Error, "Missing method.");

        Assert.StartsWith("the.contract.json : error CG0200:", diagnostic.ToMsBuildString("the.contract.json"));
    }
}
