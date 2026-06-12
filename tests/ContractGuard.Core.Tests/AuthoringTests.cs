using ContractGuard.Authoring;
using ContractGuard.Core.Comparison;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Tests;

public class AuthoringTests
{
    [Fact]
    public void Parses_a_generic_method_with_constraints_ref_param_and_default()
    {
        MemberContract member = DeclarationParser.ParseMember(
            "public T Find<T>(ref Span<T> buffer, int start = 0) where T : struct");

        var method = Assert.IsType<MethodContract>(member);
        Assert.Equal("Find", method.Name);
        Assert.Equal("T", method.Returns);
        Assert.Equal(["struct"], method.TypeParams!.Single().Constraints);
        Assert.Equal(ParamModifier.Ref, method.Params![0].Modifier);
        Assert.Equal("Span<T>", method.Params[0].Type);
        Assert.Equal(ConstantValue.Of(0), method.Params[1].Default);
    }

    [Fact]
    public void Parses_asymmetric_property_accessors()
    {
        MemberContract member = DeclarationParser.ParseMember("public int PendingCount { get; private set; }");

        var property = Assert.IsType<PropertyContract>(member);
        Assert.Equal(Accessibility.Public, property.Accessors!.Get);
        Assert.Equal(Accessibility.Private, property.Accessors.Set);
        Assert.Null(property.Accessors.Init);
    }

    [Fact]
    public void Parses_constructors_operators_events_and_const_fields()
    {
        Assert.Empty(Assert.IsType<ConstructorMemberContract>(
            DeclarationParser.ParseMember("public OrderService()")).Params);

        var plus = Assert.IsType<OperatorContract>(DeclarationParser.ParseMember(
            "public static Money operator +(Money left, Money right)"));
        Assert.Equal("+", plus.Name);

        var conversion = Assert.IsType<OperatorContract>(DeclarationParser.ParseMember(
            "public static implicit operator Money(decimal amount)"));
        Assert.Equal("implicit", conversion.Name);
        Assert.Equal("Money", conversion.Returns);

        var evt = Assert.IsType<EventContract>(DeclarationParser.ParseMember(
            "public event EventHandler<OrderEventArgs> OrderSubmitted;"));
        Assert.Equal("EventHandler<OrderEventArgs>", evt.Type);

        var field = Assert.IsType<FieldContract>(DeclarationParser.ParseMember(
            "public const int MaxRetries = 3;"));
        Assert.Equal([MemberModifier.Const], field.Modifiers);
        Assert.Equal(ConstantValue.Of(3), field.Value);
    }

    [Fact]
    public void Maps_default_null_and_negative_constants()
    {
        var method = (MethodContract)DeclarationParser.ParseMember(
            "public void M(CancellationToken ct = default, string? reason = null, int offset = -1)");

        Assert.True(method.Params![0].Default!.IsDefaultSentinel);
        Assert.Null(method.Params[1].Default!.Value);
        Assert.Equal(ConstantValue.Of(-1L), method.Params[2].Default);
    }

    [Fact]
    public void Rejects_async_with_the_doctrine()
    {
        var ex = Assert.Throws<FormatException>(() =>
            DeclarationParser.ParseMember("public async Task<int> CountAsync()"));

        Assert.Contains("async", ex.Message);
        Assert.Contains("implementation detail", ex.Message);
    }

    [Fact]
    public void Stores_enum_member_constants_as_written()
    {
        var method = (MethodContract)DeclarationParser.ParseMember(
            "public void M(OrderStatus status = OrderStatus.Pending)");

        // The comparer resolves the name against enums defined in the scanned assembly.
        Assert.Equal(ConstantValue.Of("OrderStatus.Pending"), method.Params![0].Default);
    }

    [Theory]
    [InlineData("public Task<Result> Submit(Order order)")]
    [InlineData("public static int Add(int a, int b)")]
    [InlineData("public T Find<T>(ref Span<T> buffer, int start = 0) where T : struct")]
    [InlineData("public int PendingCount { get; private set; }")]
    [InlineData("public const int MaxRetries = 3")]
    [InlineData("public static Money operator +(Money left, Money right)")]
    public void Parse_then_render_round_trips(string declaration)
    {
        MemberContract member = DeclarationParser.ParseMember(declaration);

        Assert.Equal(declaration, DeclarationRenderer.Render(member));
    }

    private const string Scaffold = """
        using System;
        using System.Threading.Tasks;

        namespace Shop.Orders
        {
            public interface IOrderService
            {
                Task<int> SubmitAsync(string order);
                int Pending { get; }
            }

            public abstract class OrderBase : IOrderService
            {
                public abstract Task<int> SubmitAsync(string order);
                public abstract int Pending { get; }
                protected OrderBase(int retries) { }
                public const int MaxRetries = 3;

                public enum Status
                {
                    Pending,
                    Shipped = 5,
                    Cancelled,
                }
            }

            public delegate void OrderHandler(string order, int count);
        }
        """;

    [Fact]
    public void Imports_a_scaffold_with_nested_types_enums_and_delegates()
    {
        ImportResult result = ScaffoldImporter.Import(Scaffold);

        Assert.Equal(["System", "System.Threading.Tasks"], result.Usings);
        Assert.Equal(4, result.Types.Count);

        TypeContract service = result.Types.Single(t => t.Type == "Shop.Orders.IOrderService");
        Assert.Equal(TypeKind.Interface, service.Kind);
        Assert.Equal(2, service.Members!.Count);

        TypeContract orderBase = result.Types.Single(t => t.Type == "Shop.Orders.OrderBase");
        Assert.Equal([TypeModifier.Abstract], orderBase.Modifiers);
        Assert.Null(orderBase.Extends);
        Assert.Equal(["IOrderService"], orderBase.Implements);

        TypeContract status = result.Types.Single(t => t.Type == "Shop.Orders.OrderBase+Status");
        Assert.Equal(TypeKind.Enum, status.Kind);
        List<ConstantValue?> values = status.Members!.Cast<FieldContract>().Select(f => f.Value).ToList();
        Assert.Equal([ConstantValue.Of(0), ConstantValue.Of(5), ConstantValue.Of(6)], values);

        TypeContract handler = result.Types.Single(t => t.Type == "Shop.Orders.OrderHandler");
        Assert.Equal(TypeKind.Delegate, handler.Kind);
        Assert.Equal("void", handler.Returns);
        Assert.Equal(2, handler.Params!.Count);
    }

    [Fact]
    public void Imported_scaffold_verifies_against_a_conforming_implementation()
    {
        // The authoring loop end to end: architect writes a scaffold, import decomposes it,
        // the gate verifies an implementation against it.
        ImportResult result = ScaffoldImporter.Import("""
                                                      namespace Shop
                                                      {
                                                          public interface ICalc
                                                          {
                                                              int Add(int a, int b);
                                                              string Describe(int value);
                                                          }
                                                      }
                                                      """);

        var contract = new AssemblyContract
        {
            Assembly = "Shop",
            Usings = result.Usings,
            Types = result.Types,
        };

        AssemblySurface conforming = TestCompiler.CompileAndRead("""
                                                                 namespace Shop
                                                                 {
                                                                     public interface ICalc
                                                                     {
                                                                         int Add(int a, int b);
                                                                         string Describe(int value);
                                                                     }
                                                                 }
                                                                 """, "Shop");
        Assert.True(ContractComparer.Compare(contract, conforming).Passed);

        AssemblySurface drifted = TestCompiler.CompileAndRead("""
                                                              namespace Shop
                                                              {
                                                                  public interface ICalc
                                                                  {
                                                                      long Add(int a, int b);
                                                                      string Describe(int value);
                                                                  }
                                                              }
                                                              """, "Shop");
        ComparisonResult verdict = ContractComparer.Compare(contract, drifted);
        Assert.Contains(DiagnosticIds.ReturnTypeMismatch, verdict.Diagnostics.Select(d => d.Id));
    }
}
