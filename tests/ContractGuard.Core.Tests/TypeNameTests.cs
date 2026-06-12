using ContractGuard.Model;
using ContractGuard.TypeNames;

namespace ContractGuard.Core.Tests;

public class TypeNameTests
{
    [Fact]
    public void Parses_nested_generics()
    {
        var node = TypeNameParser.Parse("Dictionary<string, List<int>>");

        var named = Assert.IsType<TypeNameNode.Named>(node);
        Assert.Equal("Dictionary", named.Name);
        Assert.Equal(2, named.Args.Count);
        var list = Assert.IsType<TypeNameNode.Named>(named.Args[1]);
        Assert.Equal("List", list.Name);
    }

    [Fact]
    public void Parses_suffixes_in_order()
    {
        var node = TypeNameParser.Parse("int?[]");

        var array = Assert.IsType<TypeNameNode.Array>(node);
        Assert.IsType<TypeNameNode.Nullable>(array.Element);
    }

    [Fact]
    public void Parses_multidimensional_arrays()
    {
        var node = TypeNameParser.Parse("int[,]");

        var array = Assert.IsType<TypeNameNode.Array>(node);
        Assert.Equal(2, array.Rank);
    }

    [Fact]
    public void Parses_named_tuples()
    {
        var node = TypeNameParser.Parse("(int x, int y)");

        var tuple = Assert.IsType<TypeNameNode.Tuple>(node);
        Assert.Equal(["x", "y"], tuple.Elements.Select(e => e.Name));
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.Throws<FormatException>(() => TypeNameParser.Parse("List<int"));
        Assert.Throws<FormatException>(() => TypeNameParser.Parse("int) x"));
    }

    private static TypeNameMatcher Matcher(
        Significance nullable = Significance.Ignored,
        params string[] usings) => new(usings, nullable, Significance.Significant);

    [Fact]
    public void Resolves_shorthand_through_usings()
    {
        var matcher = Matcher(usings: ["System.Threading.Tasks", "MyCompany.Orders.Model"]);

        Assert.True(matcher.Matches(
            "Task<Result>",
            "System.Threading.Tasks.Task<MyCompany.Orders.Model.Result>",
            governedNamespace: "MyCompany.Orders"));
    }

    [Fact]
    public void Resolves_shorthand_through_the_governed_namespace_walking_outward()
    {
        var matcher = Matcher();

        Assert.True(matcher.Matches("IOrderService", "MyCompany.Orders.IOrderService", "MyCompany.Orders.Internal"));
        Assert.False(matcher.Matches("IOrderService", "Other.IOrderService", "MyCompany.Orders"));
    }

    [Fact]
    public void Matches_builtin_aliases()
    {
        var matcher = Matcher();

        Assert.True(matcher.Matches("int", "int", "N"));
        Assert.True(matcher.Matches("int[]", "int[]", "N"));
        Assert.False(matcher.Matches("int", "long", "N"));
    }

    [Fact]
    public void Nullable_annotations_follow_the_significance_switch()
    {
        Assert.True(Matcher(Significance.Ignored).Matches("string?", "string", "N"));
        Assert.False(Matcher(Significance.Significant).Matches("string?", "string", "N"));
        Assert.True(Matcher(Significance.Significant).Matches("int?", "int?", "N"));
    }
}
