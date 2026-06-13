using System.Reflection.PortableExecutable;
using CliApp = ContractGuard.Cli.Cli;
using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.Serialization;

namespace ContractGuard.Core.Tests;

/// <summary>
/// Regressions surfaced by adversarial integration testing (C:\temp\cg-integration): the
/// reader and serializer must degrade gracefully on real inputs that aren't ordinary managed
/// assemblies, an extracted contract must verify against the assembly it came from, and the
/// CLI must reject bad options instead of running anyway.
/// </summary>
public class RobustnessTests
{
    [Fact]
    public void Native_PE_without_managed_metadata_is_a_load_error_not_a_crash()
    {
        // Blank the CLI header of a real managed assembly: now it's a valid PE image with no
        // .NET metadata, exactly like kernel32.dll. The CLI maps BadImageFormatException to a
        // clean exit 2; the uncaught InvalidOperationException from GetMetadataReader was a crash.
        byte[] native = BlankCliHeader(File.ReadAllBytes(typeof(AssemblyReader).Assembly.Location));

        Assert.Throws<BadImageFormatException>(() => AssemblyReader.Read(new MemoryStream(native)));
    }

    [Fact]
    public void Non_finite_double_constants_serialize_and_round_trip()
    {
        // System.Private.CoreLib carries const double NaN/Infinity; WriteNumberValue rejects them.
        AssemblySurface surface = TestCompiler.CompileAndRead("""
            namespace N
            {
                public static class K
                {
                    public const double Nan = double.NaN;
                    public const double Pos = double.PositiveInfinity;
                    public const double Neg = double.NegativeInfinity;
                    public const float FloatNan = float.NaN;
                }
            }
            """, "N");

        var contract = new AssemblyContract { Assembly = surface.Name, Types = surface.Types };
        string json = ContractJson.Serialize(contract); // previously threw ArgumentException

        Assert.Contains("\"$special\": \"NaN\"", json);
        Assert.Contains("\"$special\": \"Infinity\"", json);
        Assert.Contains("\"$special\": \"-Infinity\"", json);

        // Parses back and the recovered constants still verify against the assembly.
        ComparisonResult result = ContractComparer.Compare(ContractJson.Parse(json), surface);
        Assert.True(result.Passed, Diagnostics(result));
    }

    [Fact]
    public void A_contract_round_trips_against_its_own_assembly_with_a_notnull_constraint()
    {
        // Extract decodes nullability and emits 'notnull'; a default verify run (nullable
        // annotations ignored) never observes it. The contract must still pass against the
        // very assembly it was extracted from.
        using MemoryStream pe = TestCompiler.Emit(TestCompiler.Compile(
            "namespace N { public class Box<T> where T : notnull { } }", "N"));
        byte[] bytes = pe.ToArray();

        AssemblySurface extracted = AssemblyReader.Read(
            new MemoryStream(bytes), new ReaderOptions { DecodeNullableAnnotations = true });
        var contract = new AssemblyContract { Assembly = extracted.Name, Types = extracted.Types };

        AssemblySurface verified = AssemblyReader.Read(
            new MemoryStream(bytes), new ReaderOptions { DecodeNullableAnnotations = false });

        ComparisonResult result = ContractComparer.Compare(contract, verified);
        Assert.True(result.Passed, Diagnostics(result));
    }

    [Fact]
    public void A_significant_notnull_constraint_is_still_enforced()
    {
        // The fix must not blanket-ignore 'notnull': when nullableAnnotations is significant,
        // an assembly that drops the constraint is a real violation.
        AssemblySurface withConstraint = TestCompiler.CompileAndRead(
            "namespace N { public class Box<T> where T : notnull { } }", "N",
            new ReaderOptions { DecodeNullableAnnotations = true });
        AssemblySurface without = TestCompiler.CompileAndRead(
            "namespace N { public class Box<T> { } }", "N",
            new ReaderOptions { DecodeNullableAnnotations = true });

        var contract = new AssemblyContract
        {
            Assembly = withConstraint.Name,
            Settings = new ContractSettings { NullableAnnotations = Significance.Significant },
            Types = withConstraint.Types,
        };

        ComparisonResult result = ContractComparer.Compare(contract, without);

        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Id == DiagnosticIds.TypeParamsMismatch);
    }

    [Fact]
    public void Verify_rejects_an_unknown_output_format()
    {
        string dir = Directory.CreateTempSubdirectory("cg-fmt").FullName;
        try
        {
            string asm = Path.Combine(dir, "T.dll");
            File.WriteAllBytes(asm,
                TestCompiler.Emit(TestCompiler.Compile("namespace N { public class C { } }", "T")).ToArray());
            string contract = Path.Combine(dir, "T.contract.json");
            Assert.Equal(0, CliApp.Run(["extract", "--assembly", asm, "--output", contract]));

            // valid contract + assembly, but a bogus format is a usage error, not a silent run.
            Assert.Equal(2, CliApp.Run(["verify", "--contract", contract, "--assembly", asm, "--format", "xml"]));
            Assert.Equal(0, CliApp.Run(["verify", "--contract", contract, "--assembly", asm, "--format", "json"]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Help_is_a_clean_exit_zero()
    {
        Assert.Equal(0, CliApp.Run(["--help"]));
        Assert.Equal(0, CliApp.Run(["extract", "--help"]));
    }

    /// <summary>Zeroes the CLI header data-directory entry so PEReader.HasMetadata is false -
    /// turning a managed assembly into a stand-in for a native binary.</summary>
    private static byte[] BlankCliHeader(byte[] image)
    {
        PEHeaders headers;
        using (var probe = new PEReader(new MemoryStream(image, writable: false)))
            headers = probe.PEHeaders;

        // Optional header starts after the 20-byte COFF header; its data directories follow the
        // standard+windows fields (96 bytes for PE32, 112 for PE32+). The CLI header is data
        // directory index 14 - zero its 8-byte RVA+size.
        int optionalHeader = headers.CoffHeaderStartOffset + 20;
        int dataDirectories = optionalHeader + (headers.PEHeader!.Magic == PEMagic.PE32Plus ? 112 : 96);
        var stripped = (byte[])image.Clone();
        Array.Clear(stripped, dataDirectories + 14 * 8, 8);
        return stripped;
    }

    private static string Diagnostics(ComparisonResult result) =>
        string.Join("; ", result.Diagnostics.Select(d => d.ToString()));
}
