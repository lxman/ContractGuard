using System.Text.Json;
using ContractGuard.Model;
using ContractGuard.Serialization;

namespace ContractGuard.Core.Tests;

public class ContractLoaderTests
{
    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ContractGuard.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }

    [Fact]
    public void Loads_the_sample_contract()
    {
        var contract = ContractJson.Load(RepoPath("samples", "MyCompany.Orders.contract.json"));

        Assert.Equal("MyCompany.Orders", contract.Assembly);
        Assert.Equal(4, contract.Types.Count);
        Assert.Equal(Significance.Ignored, contract.Settings.NullableAnnotations);

        var orderService = contract.Types.Single(t => t.Type == "MyCompany.Orders.OrderService");
        Assert.Equal(AllowDeny.Deny, orderService.NewMembers);
        Assert.Equal(8, orderService.Members!.Count);

        var forbidden = orderService.Members.OfType<ConstructorMemberContract>()
            .Single(c => c.Mode == EntryMode.Forbidden);
        Assert.Empty(forbidden.Params);
        Assert.Contains("DI", forbidden.Reason);

        var find = orderService.Members.OfType<MethodContract>().Single(m => m.Name == "Find");
        Assert.Equal(ParamModifier.Ref, find.Params![0].Modifier);
        Assert.Equal(ConstantValue.Of(0), find.Params[1].Default);

        var money = contract.Types.Single(t => t.Type == "MyCompany.Orders.Money");
        Assert.Equal([TypeModifier.Readonly], money.Modifiers);
        Assert.Equal(2, money.Members!.OfType<OperatorContract>().Count());
    }

    [Fact]
    public void Applies_defaults_for_omitted_settings()
    {
        var contract = ContractJson.Parse("""{"assembly": "X", "types": []}""");

        Assert.Equal(AllowDeny.Allow, contract.Settings.NewTypes);
        Assert.Equal(Significance.Significant, contract.Settings.ParameterNames);
        Assert.Equal(Significance.Significant, contract.Settings.DefaultValues);
        Assert.Equal(Significance.Ignored, contract.Settings.NullableAnnotations);
        Assert.Equal(Significance.Ignored, contract.Settings.TupleElementNames);
        Assert.Equal([Accessibility.Public, Accessibility.Protected], contract.Settings.Scope);
    }

    [Fact]
    public void Rejects_declaration_strings_as_members()
    {
        var json = """
            {"assembly": "X", "types": [{"type": "N.T", "members": ["public int Add(int a, int b)"]}]}
            """;

        var ex = Assert.Throws<JsonException>(() => ContractJson.Parse(json));
        Assert.Contains("decomposed elements", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_properties()
    {
        var json = """
            {"assembly": "X", "types": [{"type": "N.T", "members": [
                {"kind": "method", "name": "M", "returns": "void", "acess": "public"}]}]}
            """;

        Assert.ThrowsAny<JsonException>(() => ContractJson.Parse(json));
    }

    [Fact]
    public void Parses_the_special_default_sentinel()
    {
        var json = """
            {"assembly": "X", "types": [{"type": "N.T", "members": [
                {"kind": "method", "name": "M", "returns": "void",
                 "params": [{"type": "System.Threading.CancellationToken", "name": "ct",
                             "default": {"$special": "default"}}]}]}]}
            """;

        var contract = ContractJson.Parse(json);
        var method = (MethodContract)contract.Types[0].Members![0];
        Assert.True(method.Params![0].Default!.IsDefaultSentinel);
    }

    [Fact]
    public void Serialization_round_trips_stably()
    {
        var loaded = ContractJson.Load(RepoPath("samples", "MyCompany.Orders.contract.json"));
        var once = ContractJson.Serialize(loaded);
        var twice = ContractJson.Serialize(ContractJson.Parse(once));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Tolerates_comments_and_trailing_commas()
    {
        var contract = ContractJson.Parse("""
            {
                // governance file, hand-edited
                "assembly": "X",
                "types": [],
            }
            """);

        Assert.Equal("X", contract.Assembly);
    }
}
