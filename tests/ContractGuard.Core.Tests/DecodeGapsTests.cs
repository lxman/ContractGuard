using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;

namespace ContractGuard.Core.Tests;

/// <summary>
/// Pins the second round of metadata decoding against real Roslyn emission: record
/// detection, ref readonly in both positions, volatile fields, constraint nullability,
/// and enum-name default resolution.
/// </summary>
public class DecodeGapsTests
{
    internal const string Source = """
        #pragma warning disable CS0414
        namespace Gaps
        {
            public record Order(int Id, string Name);

            public record struct Point(int X, int Y);

            public class Buffer
            {
                private readonly int[] _data = new int[8];
                public ref readonly int Peek() => ref _data[0];
                public void Load(ref readonly int source) { }
                public volatile int Counter = 0;
            }

            public static class Constrained
            {
                public static void NotNull<T>(T value) where T : notnull { }
                public static void NullableClass<T>(T value) where T : class? { }
                public static void Plain<T>(T value) { }
                public static void Blit<T>(T value) where T : unmanaged { }
            }

            public class Pricing
            {
                public const decimal Tax = 0.07m;
                public decimal Total(decimal price = 9.99m) => price + Tax;
            }

            public interface ICalculator<TSelf> where TSelf : ICalculator<TSelf>
            {
                static abstract TSelf Zero { get; }
                static abstract TSelf Parse(string text);
                static virtual int Precision => 2;
                int Evaluate();
            }

            public enum Color { Red, Green = 5, Blue }

            public class Painter
            {
                public void Paint(Color color = Color.Green) { }
            }
        }
        """;

    private static readonly Lazy<AssemblySurface> Surface = new(() => TestCompiler.CompileAndRead(Source, "Gaps"));

    private static TypeContract Type(string fullName) => Surface.Value.Types.Single(t => t.Type == fullName);

    [Fact]
    public void Detects_record_classes_and_suppresses_their_plumbing()
    {
        TypeContract order = Type("Gaps.Order");

        Assert.Equal(TypeKind.Record, order.Kind);
        var memberNames = order.Members!.Select(m => m.DisplayName).ToList();
        Assert.DoesNotContain("EqualityContract", memberNames);
        Assert.DoesNotContain("PrintMembers", memberNames);
        Assert.Contains("Id", memberNames);
        Assert.Contains("Deconstruct", memberNames);
    }

    [Fact]
    public void Record_structs_stay_structs()
    {
        Assert.Equal(TypeKind.Struct, Type("Gaps.Point").Kind);
    }

    [Fact]
    public void Decodes_ref_readonly_returns_and_parameters()
    {
        var buffer = Type("Gaps.Buffer");
        var peek = buffer.Members!.OfType<MethodContract>().Single(m => m.Name == "Peek");
        var load = buffer.Members!.OfType<MethodContract>().Single(m => m.Name == "Load");

        Assert.Equal(ReturnRefKind.RefReadonly, peek.RefKind);
        Assert.Equal("int", peek.Returns);
        Assert.Equal(ParamModifier.RefReadonly, load.Params![0].Modifier);
        Assert.Equal("int", load.Params[0].Type);
    }

    [Fact]
    public void Decodes_volatile_fields()
    {
        var counter = Type("Gaps.Buffer").Members!.OfType<FieldContract>().Single(f => f.Name == "Counter");

        Assert.Contains(MemberModifier.Volatile, counter.Modifiers!);
        Assert.Equal("int", counter.Type);
    }

    [Fact]
    public void Decodes_constraint_nullability()
    {
        var methods = Type("Gaps.Constrained").Members!.OfType<MethodContract>().ToList();

        Assert.Equal(["notnull"], methods.Single(m => m.Name == "NotNull").TypeParams!.Single().Constraints);
        Assert.Equal(["class?"], methods.Single(m => m.Name == "NullableClass").TypeParams!.Single().Constraints);
        Assert.Null(methods.Single(m => m.Name == "Plain").TypeParams!.Single().Constraints);
    }

    [Fact]
    public void Decodes_unmanaged_constraints()
    {
        var blit = Type("Gaps.Constrained").Members!.OfType<MethodContract>().Single(m => m.Name == "Blit");

        Assert.Equal(["unmanaged"], blit.TypeParams!.Single().Constraints);
    }

    [Fact]
    public void Decodes_decimal_constants_in_fields_and_defaults()
    {
        TypeContract pricing = Type("Gaps.Pricing");

        var tax = pricing.Members!.OfType<FieldContract>().Single(f => f.Name == "Tax");
        Assert.Equal([MemberModifier.Const], tax.Modifiers);
        Assert.Equal(ConstantValue.Of(0.07m), tax.Value);

        var total = pricing.Members!.OfType<MethodContract>().Single(m => m.Name == "Total");
        Assert.Equal(ConstantValue.Of(9.99m), total.Params![0].Default);
    }

    [Fact]
    public void Distinguishes_static_abstract_from_static_virtual_interface_members()
    {
        TypeContract calculator = Type("Gaps.ICalculator");
        var members = calculator.Members!;

        Assert.Equal([MemberModifier.Static, MemberModifier.Abstract],
            members.OfType<PropertyContract>().Single(p => p.Name == "Zero").Modifiers);
        Assert.Equal([MemberModifier.Static, MemberModifier.Abstract],
            members.OfType<MethodContract>().Single(m => m.Name == "Parse").Modifiers);
        Assert.Equal([MemberModifier.Static, MemberModifier.Virtual],
            members.OfType<PropertyContract>().Single(p => p.Name == "Precision").Modifiers);
        Assert.Null(members.OfType<MethodContract>().Single(m => m.Name == "Evaluate").Modifiers);
    }

    [Fact]
    public void Json_fractional_defaults_compare_equal_to_decoded_decimals()
    {
        var contract = ContractJson.Parse("""
            {
                "assembly": "Gaps",
                "types": [
                    {
                        "type": "Gaps.Pricing",
                        "members": [
                            { "kind": "field", "name": "Tax", "type": "decimal",
                              "modifiers": ["const"], "value": 0.07 },
                            { "kind": "method", "name": "Total", "returns": "decimal",
                              "params": [{ "type": "decimal", "name": "price", "default": 9.99 }] }
                        ]
                    }
                ]
            }
            """);

        ComparisonResult result = ContractComparer.Compare(contract, Surface.Value);
        Assert.True(result.Passed, string.Join("; ", result.Diagnostics));

        var drifted = ContractJson.Parse("""
            {
                "assembly": "Gaps",
                "types": [
                    {
                        "type": "Gaps.Pricing",
                        "members": [
                            { "kind": "field", "name": "Tax", "type": "decimal",
                              "modifiers": ["const"], "value": 0.08 }
                        ]
                    }
                ]
            }
            """);

        Assert.Contains(DiagnosticIds.ConstValueChanged,
            ContractComparer.Compare(drifted, Surface.Value).Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void Resolves_enum_name_defaults_against_assembly_enums()
    {
        var contract = ContractJson.Parse("""
            {
                "assembly": "Gaps",
                "types": [
                    {
                        "type": "Gaps.Painter",
                        "members": [
                            { "kind": "method", "name": "Paint", "returns": "void",
                              "params": [{ "type": "Color", "name": "color", "default": "Color.Green" }] }
                        ]
                    }
                ]
            }
            """);

        Assert.True(ContractComparer.Compare(contract, Surface.Value).Passed);

        var wrongMember = ContractJson.Parse("""
            {
                "assembly": "Gaps",
                "types": [
                    {
                        "type": "Gaps.Painter",
                        "members": [
                            { "kind": "method", "name": "Paint", "returns": "void",
                              "params": [{ "type": "Color", "name": "color", "default": "Color.Blue" }] }
                        ]
                    }
                ]
            }
            """);

        var result = ContractComparer.Compare(wrongMember, Surface.Value);
        Assert.Contains(DiagnosticIds.ParameterDefaultsChanged, result.Diagnostics.Select(d => d.Id));
    }
}
