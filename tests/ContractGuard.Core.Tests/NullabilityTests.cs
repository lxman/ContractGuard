using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;

namespace ContractGuard.Core.Tests;

/// <summary>
/// Asserts the nullable-annotation and tuple-name decoding against what Roslyn actually
/// emits - the compile-and-read harness is the source of truth for the transform-flag walk.
/// </summary>
public class NullabilityTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Nrt
        {
            public class Svc
            {
                public string? Find(string key) => null;
                public Dictionary<string, List<int?>>? Map(string? a, int? b) => null;
                public string?[] Tags = Array.Empty<string?>();
                public string[]? MaybeTags;
                public (int x, string? y) Pair() => default;
                public (int a, (bool b, string c) inner) Nested() => default;
                public T? Get<T>(T input) where T : class => null;
                public event EventHandler? Done { add { } remove { } }
                public string? Prop { get; set; }
            }
        }
        """;

    private static readonly Lazy<AssemblySurface> Annotated = new(() =>
        TestCompiler.CompileAndRead(Source, "Nrt"));

    private static TypeContract Svc => Annotated.Value.Types.Single(t => t.Type == "Nrt.Svc");

    private static MethodContract Method(string name) =>
        Svc.Members!.OfType<MethodContract>().Single(m => m.Name == name);

    [Fact]
    public void Annotates_returns_and_leaves_unannotated_params_plain()
    {
        MethodContract find = Method("Find");

        Assert.Equal("string?", find.Returns);
        Assert.Equal("string", find.Params![0].Type);
    }

    [Fact]
    public void Walks_flags_through_generic_structure()
    {
        MethodContract map = Method("Map");

        Assert.Equal(
            "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int?>>?",
            map.Returns);
        Assert.Equal("string?", map.Params![0].Type);
        Assert.Equal("int?", map.Params[1].Type);
    }

    [Fact]
    public void Distinguishes_array_of_nullable_from_nullable_array()
    {
        List<FieldContract> fields = Svc.Members!.OfType<FieldContract>().ToList();

        Assert.Equal("string?[]", fields.Single(f => f.Name == "Tags").Type);
        Assert.Equal("string[]?", fields.Single(f => f.Name == "MaybeTags").Type);
    }

    [Fact]
    public void Decodes_tuple_element_names()
    {
        Assert.Equal("(int x, string? y)", Method("Pair").Returns);
        Assert.Equal("(int a, (bool b, string c) inner)", Method("Nested").Returns);
    }

    [Fact]
    public void Annotates_generic_type_parameters()
    {
        MethodContract get = Method("Get");

        Assert.Equal("T?", get.Returns);
        Assert.Equal("T", get.Params![0].Type);
    }

    [Fact]
    public void Annotates_events_and_properties()
    {
        Assert.Equal("System.EventHandler?", Svc.Members!.OfType<EventContract>().Single().Type);
        Assert.Equal("string?", Svc.Members!.OfType<PropertyContract>().Single(p => p.Name == "Prop").Type);
    }

    [Fact]
    public void Oblivious_assemblies_render_plain()
    {
        var source = """
            namespace Old { public class Svc { public string Find(string key) => key; } }
            """;
        AssemblySurface surface = TestCompiler.CompileAndRead(source, "Old", nullableEnable: false);
        MethodContract find = surface.Types.Single().Members!.OfType<MethodContract>().Single();

        Assert.Equal("string", find.Returns);
        Assert.Equal("string", find.Params![0].Type);
    }

    [Fact]
    public void Decode_off_renders_annotated_assemblies_plain_but_keeps_tuple_names()
    {
        AssemblySurface surface = TestCompiler.CompileAndRead(
            Source, "Nrt", new ReaderOptions { DecodeNullableAnnotations = false });
        TypeContract svc = surface.Types.Single(t => t.Type == "Nrt.Svc");
        List<MethodContract> methods = svc.Members!.OfType<MethodContract>().ToList();

        Assert.Equal("string", methods.Single(m => m.Name == "Find").Returns);
        Assert.Equal("(int x, string y)", methods.Single(m => m.Name == "Pair").Returns);
    }

    private const string GateSource = """
        namespace Shop
        {
            public class Calc
            {
                public string? Describe(string input) => null;
            }
        }
        """;

    private static ComparisonResult Compare(string contractJson, string source, Significance nullableAnnotations)
    {
        AssemblyContract contract = ContractJson.Parse(contractJson);
        AssemblySurface surface = TestCompiler.CompileAndRead(source, "Shop", new ReaderOptions
        {
            DecodeNullableAnnotations = nullableAnnotations == Significance.Significant,
        });
        return ContractComparer.Compare(contract, surface);
    }

    [Fact]
    public void Significant_mode_enforces_annotations()
    {
        var contract = """
            {
                "assembly": "Shop",
                "settings": { "nullableAnnotations": "significant" },
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "method", "name": "Describe", "returns": "string?",
                              "params": [["string", "input"]] }
                        ]
                    }
                ]
            }
            """;

        Assert.True(Compare(contract, GateSource, Significance.Significant).Passed);

        string drifted = GateSource.Replace("public string? Describe", "public string Describe")
            .Replace("=> null;", "=> input;");
        ComparisonResult result = Compare(contract, drifted, Significance.Significant);
        Assert.Equal([DiagnosticIds.ReturnTypeMismatch], result.Diagnostics.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Ignored_mode_accepts_annotation_drift()
    {
        var contract = """
            {
                "assembly": "Shop",
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "method", "name": "Describe", "returns": "string?",
                              "params": [["string", "input"]] }
                        ]
                    }
                ]
            }
            """;

        string unannotated = GateSource.Replace("public string? Describe", "public string Describe")
            .Replace("=> null;", "=> input;");

        Assert.True(Compare(contract, GateSource, Significance.Ignored).Passed);
        Assert.True(Compare(contract, unannotated, Significance.Ignored).Passed);
    }

    [Fact]
    public void Tuple_element_renames_follow_the_significance_switch()
    {
        var contract = """
            {
                "assembly": "Shop",
                "settings": { "tupleElementNames": "significant" },
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "method", "name": "Range", "returns": "(int start, int end)",
                              "params": [] }
                        ]
                    }
                ]
            }
            """;
        var source = "namespace Shop { public class Calc { public (int start, int end) Range() => default; } }";
        string renamed = source.Replace("(int start, int end) Range", "(int from, int to) Range");

        Assert.True(Compare(contract, source, Significance.Ignored).Passed);
        Assert.False(Compare(contract, renamed, Significance.Ignored).Passed);

        string relaxed = contract.Replace("\"significant\"", "\"ignored\"");
        Assert.True(Compare(relaxed, renamed, Significance.Ignored).Passed);
    }
}
