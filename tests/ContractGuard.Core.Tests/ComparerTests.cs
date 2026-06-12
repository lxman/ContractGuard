using ContractGuard.Comparison;
using ContractGuard.Serialization;

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
        var result = Compare(BaselineContract, BaselineSource);

        Assert.True(result.Passed, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Fails_when_the_assembly_name_does_not_match()
    {
        var result = Compare(BaselineContract, BaselineSource, assemblyName: "OtherAssembly");

        Assert.Equal([DiagnosticIds.AssemblyNameMismatch], Ids(result));
    }

    [Fact]
    public void Detects_a_changed_return_type()
    {
        var source = BaselineSource.Replace("public int Add", "public long Add");

        var result = Compare(BaselineContract, source);

        Assert.Equal([DiagnosticIds.ReturnTypeMismatch], Ids(result));
    }

    [Fact]
    public void Detects_a_changed_parameter_type_and_names_the_culprit()
    {
        var source = BaselineSource.Replace(
            "public int Add(int a, int b) => a + b;",
            "public int Add(int a, long b) => a + (int)b;");

        var result = Compare(BaselineContract, source);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.MemberSignatureChanged, diagnostic.Id);
        Assert.Contains("found: public int Add(int a, long b)", diagnostic.Message);
    }

    [Fact]
    public void Detects_a_missing_member()
    {
        var source = BaselineSource.Replace("public int Add(int a, int b) => a + b;", "");

        var result = Compare(BaselineContract, source);

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

        var result = Compare(contract, BaselineSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.ForbiddenMemberPresent, diagnostic.Id);
        Assert.Equal("construct via DI", diagnostic.Reason);
    }

    [Fact]
    public void Denying_new_members_flags_unprescribed_surface()
    {
        var contract = BaselineContract.Replace("\"kind\": \"class\",", "\"kind\": \"class\", \"newMembers\": \"deny\",");

        var result = Compare(contract, BaselineSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.UnexpectedMember, diagnostic.Id);
        Assert.Contains("Name", diagnostic.Message);
    }

    [Fact]
    public void Denying_new_members_ignores_out_of_scope_members()
    {
        var contract = BaselineContract.Replace("\"kind\": \"class\",", "\"kind\": \"class\", \"newMembers\": \"deny\",");
        var source = BaselineSource
            .Replace("public string Name => \"calc\";", "internal string Name => \"calc\";");

        var result = Compare(contract, source);

        Assert.True(result.Passed, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void Parameter_renames_follow_the_significance_switch()
    {
        var source = BaselineSource.Replace(
            "public int Add(int a, int b) => a + b;",
            "public int Add(int x, int b) => x + b;");

        var strict = Compare(BaselineContract, source);
        Assert.Equal([DiagnosticIds.ParameterNamesChanged], Ids(strict));

        var relaxed = BaselineContract.Replace(
            "\"assembly\": \"Shop\",",
            "\"assembly\": \"Shop\", \"settings\": { \"parameterNames\": \"ignored\" },");
        Assert.True(Compare(relaxed, source).Passed);
    }

    [Fact]
    public void Denying_new_types_flags_unprescribed_types()
    {
        var contract = BaselineContract.Replace(
            "\"assembly\": \"Shop\",",
            "\"assembly\": \"Shop\", \"settings\": { \"newTypes\": \"deny\" },");
        var source = BaselineSource.Replace(
            "public class Calc",
            "public class Extra { } public class Calc");

        var result = Compare(contract, source);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.UnexpectedType, diagnostic.Id);
        Assert.Contains("Shop.Extra", diagnostic.Message);
    }

    [Fact]
    public void Detects_demoted_type_accessibility()
    {
        var source = BaselineSource.Replace("public class Calc", "internal class Calc");

        var result = Compare(BaselineContract, source);

        Assert.Contains(DiagnosticIds.AccessMismatch, Ids(result));
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

        var result = Compare(contract, source);

        Assert.Equal([DiagnosticIds.ConstValueChanged], Ids(result));
    }
}
