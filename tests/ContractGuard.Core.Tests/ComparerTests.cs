using ContractGuard.Core.Comparison;
using ContractGuard.Core.Serialization;

namespace ContractGuard.Core.Tests;

public class ComparerTests
{
    private const string BaselineSource = """
        namespace Shop
        {
            public class Calc
            {
                public Calc() { }
                public int Add(int a, int b) => a + b;
                public string Name => "calc";
            }
        }
        """;

    private const string BaselineContract = """
        {
            "assembly": "Shop",
            "types": [
                {
                    "type": "Shop.Calc",
                    "kind": "class",
                    "members": [
                        { "kind": "constructor", "access": "public", "params": [] },
                        { "kind": "method", "name": "Add", "returns": "int",
                          "params": [["int", "a"], ["int", "b"]] }
                    ]
                }
            ]
        }
        """;

    private static ComparisonResult Compare(string contractJson, string source, string assemblyName = "Shop") =>
        ContractComparer.Compare(ContractJson.Parse(contractJson), TestCompiler.CompileAndRead(source, assemblyName));

    private static string[] Ids(ComparisonResult result) => result.Diagnostics.Select(d => d.Id).ToArray();

    [Fact]
    public void Passes_when_the_assembly_honors_the_contract()
    {
        ComparisonResult result = Compare(BaselineContract, BaselineSource);

        Assert.True(result.Passed, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Fails_when_the_assembly_name_does_not_match()
    {
        ComparisonResult result = Compare(BaselineContract, BaselineSource, assemblyName: "OtherAssembly");

        Assert.Equal([DiagnosticIds.AssemblyNameMismatch], Ids(result));
    }

    [Fact]
    public void Detects_a_changed_return_type()
    {
        string source = BaselineSource.Replace("public int Add", "public long Add");

        ComparisonResult result = Compare(BaselineContract, source);

        Assert.Equal([DiagnosticIds.ReturnTypeMismatch], Ids(result));
    }

    [Fact]
    public void Detects_a_changed_parameter_type_and_names_the_culprit()
    {
        string source = BaselineSource.Replace(
            "public int Add(int a, int b) => a + b;",
            "public int Add(int a, long b) => a + (int)b;");

        ComparisonResult result = Compare(BaselineContract, source);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.MemberSignatureChanged, diagnostic.Id);
        Assert.Contains("found: public int Add(int a, long b)", diagnostic.Message);
    }

    [Fact]
    public void Detects_a_missing_member()
    {
        string source = BaselineSource.Replace("public int Add(int a, int b) => a + b;", "");

        ComparisonResult result = Compare(BaselineContract, source);

        Assert.Equal([DiagnosticIds.MemberMissing], Ids(result));
    }

    [Fact]
    public void Detects_a_forbidden_constructor_and_reports_the_reason()
    {
        var contract = """
            {
                "assembly": "Shop",
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "constructor", "access": "public", "params": [],
                              "mode": "forbidden", "reason": "construct via DI" }
                        ]
                    }
                ]
            }
            """;

        ComparisonResult result = Compare(contract, BaselineSource);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.ForbiddenMemberPresent, diagnostic.Id);
        Assert.Equal("construct via DI", diagnostic.Reason);
    }

    [Fact]
    public void Denying_new_members_flags_unprescribed_surface()
    {
        string contract = BaselineContract.Replace("\"kind\": \"class\",", "\"kind\": \"class\", \"newMembers\": \"deny\",");

        ComparisonResult result = Compare(contract, BaselineSource);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.UnexpectedMember, diagnostic.Id);
        Assert.Contains("Name", diagnostic.Message);
    }

    [Fact]
    public void Denying_new_members_ignores_out_of_scope_members()
    {
        string contract = BaselineContract.Replace("\"kind\": \"class\",", "\"kind\": \"class\", \"newMembers\": \"deny\",");
        string source = BaselineSource
            .Replace("public string Name => \"calc\";", "internal string Name => \"calc\";");

        ComparisonResult result = Compare(contract, source);

        Assert.True(result.Passed, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void Parameter_renames_follow_the_significance_switch()
    {
        string source = BaselineSource.Replace(
            "public int Add(int a, int b) => a + b;",
            "public int Add(int x, int b) => x + b;");

        ComparisonResult strict = Compare(BaselineContract, source);
        Assert.Equal([DiagnosticIds.ParameterNamesChanged], Ids(strict));

        string relaxed = BaselineContract.Replace(
            "\"assembly\": \"Shop\",",
            "\"assembly\": \"Shop\", \"settings\": { \"parameterNames\": \"ignored\" },");
        Assert.True(Compare(relaxed, source).Passed);
    }

    [Fact]
    public void Denying_new_types_flags_unprescribed_types()
    {
        string contract = BaselineContract.Replace(
            "\"assembly\": \"Shop\",",
            "\"assembly\": \"Shop\", \"settings\": { \"newTypes\": \"deny\" },");
        string source = BaselineSource.Replace(
            "public class Calc",
            "public class Extra { } public class Calc");

        ComparisonResult result = Compare(contract, source);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.UnexpectedType, diagnostic.Id);
        Assert.Contains("Shop.Extra", diagnostic.Message);
    }

    [Fact]
    public void Detects_demoted_type_accessibility()
    {
        string source = BaselineSource.Replace("public class Calc", "internal class Calc");

        ComparisonResult result = Compare(BaselineContract, source);

        Assert.Contains(DiagnosticIds.AccessMismatch, Ids(result));
    }

    [Fact]
    public void Prescribed_members_are_enforced_regardless_of_scope()
    {
        // Scope gates the deny sweeps (what counts as new surface); it never exempts a
        // member the architect explicitly prescribed, whatever its accessibility.
        var contract = """
            {
                "assembly": "Shop",
                "types": [
                    {
                        "type": "Shop.Calc",
                        "members": [
                            { "kind": "method", "name": "Hook", "access": "internal",
                              "returns": "int", "params": [] }
                        ]
                    }
                ]
            }
            """;
        string conforming = BaselineSource.Replace(
            "public string Name => \"calc\";",
            "internal int Hook() => 1;");
        Assert.True(Compare(contract, conforming).Passed);

        string drifted = BaselineSource.Replace(
            "public string Name => \"calc\";",
            "internal long Hook() => 1;");
        Assert.Equal([DiagnosticIds.ReturnTypeMismatch], Ids(Compare(contract, drifted)));
    }

    [Fact]
    public void Widened_scope_makes_internal_additions_visible_to_deny_sweeps()
    {
        string contract = BaselineContract
            .Replace("\"assembly\": \"Shop\",",
                "\"assembly\": \"Shop\", \"settings\": { \"scope\": [\"public\", \"protected\", \"internal\"] },")
            .Replace("\"kind\": \"class\",", "\"kind\": \"class\", \"newMembers\": \"deny\",");
        string source = BaselineSource.Replace(
            "public string Name => \"calc\";",
            "internal string Name => \"calc\";");

        ComparisonResult result = Compare(contract, source);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.UnexpectedMember, diagnostic.Id);
    }

    [Fact]
    public void Detects_a_changed_const_value()
    {
        var contract = """
            {
                "assembly": "Shop",
                "types": [
                    {
                        "type": "Shop.Limits",
                        "members": [
                            { "kind": "field", "name": "Max", "type": "int",
                              "modifiers": ["const"], "value": 10 }
                        ]
                    }
                ]
            }
            """;
        var source = "namespace Shop { public static class Limits { public const int Max = 12; } }";

        ComparisonResult result = Compare(contract, source);

        Assert.Equal([DiagnosticIds.ConstValueChanged], Ids(result));
    }
}
