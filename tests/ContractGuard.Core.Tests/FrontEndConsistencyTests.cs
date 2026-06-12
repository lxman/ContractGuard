using ContractGuard.Analyzers;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractGuard.Core.Tests;

/// <summary>
/// The phase-2 guarantee from the original design: compile a snippet in-memory, extract the
/// model from the Compilation via the ISymbol front-end AND from the emitted PE via the
/// metadata front-end, and assert the two are identical. Run over the whole edge-case zoo,
/// front-end drift becomes structurally impossible to ship.
/// </summary>
public class FrontEndConsistencyTests
{
    private static void AssertConsistent(string source, bool nullableEnable = true, ReaderOptions? options = null)
    {
        ReaderOptions effective = options ?? ReaderOptions.Default;
        CSharpCompilation compilation = TestCompiler.Compile(source, "Zoo", nullableEnable);

        using MemoryStream stream = TestCompiler.Emit(compilation);
        AssemblySurface metadata = AssemblyReader.Read(stream, effective);
        AssemblySurface symbols = SymbolSurfaceReader.Read(compilation.Assembly, effective);

        Assert.Equal(Canonical(metadata), Canonical(symbols));
    }

    /// <summary>Serialization ignores SourceLocation by design, so locations (PDB lines vs
    /// syntax positions) never participate; order is normalized because the metadata table
    /// and symbol traversal enumerate differently.</summary>
    private static string Canonical(AssemblySurface surface)
    {
        List<TypeContract> types = surface.Types
            .Select(t => t.Members is null
                ? t
                : t with
                {
                    Members = t.Members
                        .OrderBy(m => m.KindName, StringComparer.Ordinal)
                        .ThenBy(m => Core.Comparison.DeclarationRenderer.Render(m), StringComparer.Ordinal)
                        .ToList(),
                })
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ToList();

        return ContractJson.Serialize(new AssemblyContract { Assembly = surface.Name, Types = types });
    }

    [Fact]
    public void Agrees_on_the_general_surface_zoo() => AssertConsistent(MetadataReaderTests.Source);

    [Fact]
    public void Agrees_on_nullable_annotations_and_tuples() => AssertConsistent(NullabilityTests.Source);

    [Fact]
    public void Agrees_on_records_ref_readonly_volatile_and_constraints() =>
        AssertConsistent(DecodeGapsTests.Source);

    [Fact]
    public void Agrees_on_explicit_interface_implementations() =>
        AssertConsistent(GovernanceGapsTests.ExplicitSource);

    [Fact]
    public void Agrees_on_oblivious_assemblies() => AssertConsistent("""
        namespace Old
        {
            public class Svc
            {
                public string Find(string key) => key;
                public event System.EventHandler Done { add { } remove { } }
            }
        }
        """, nullableEnable: false);

    [Fact]
    public void Agrees_with_nullable_decoding_off() => AssertConsistent(
        NullabilityTests.Source, options: new ReaderOptions { DecodeNullableAnnotations = false });

    [Fact]
    public void Agrees_on_inheritance_indexers_and_nesting() => AssertConsistent("""
        using System;
        using System.Collections.Generic;

        namespace Web
        {
            public abstract class Page : List<string?>, IDisposable, IObserver<int>
            {
                protected Page(int size) { }
                public abstract int this[string key, int fallback] { get; set; }
                public void Dispose() { }
                void IObserver<int>.OnCompleted() { }
                void IObserver<int>.OnError(Exception error) { }
                void IObserver<int>.OnNext(int value) { }
                public sealed override string ToString() => "";

                public static class Inner
                {
                    public static void Helper(params string[] lines) { }
                }
            }

            public delegate ref int Picker(ref readonly Span<int> source, int index = -1);

            public interface IRepo<in TKey, out TValue> where TKey : notnull
            {
                TValue Find(TKey key);
            }
        }
        """);

    [Fact]
    public void Agrees_on_extension_methods_and_operators() => AssertConsistent("""
        namespace Ops
        {
            public static class Extensions
            {
                public static int DoubleIt(this int value) => value * 2;
            }

            public readonly struct Money
            {
                public static Money operator +(Money left, Money right) => default;
                public static implicit operator Money(decimal amount) => default;
                public static explicit operator decimal(Money money) => 0m;
                public static bool operator true(Money money) => false;
                public static bool operator false(Money money) => true;
            }
        }
        """);
}
