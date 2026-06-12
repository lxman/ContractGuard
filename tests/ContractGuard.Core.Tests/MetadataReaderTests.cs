using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Tests;

public class MetadataReaderTests
{
    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace TestLib
        {
            public class OrderService
            {
                public OrderService(int retries) { }
                public Task<int> SubmitAsync(string name, CancellationToken ct = default) => Task.FromResult(0);
                public static int Add(int a, int b) => a + b;
                public int PendingCount { get; private set; }
                public event EventHandler Completed { add { } remove { } }
                public const int MaxRetries = 3;
                public T Find<T>(ref Span<T> buffer, int start = 0) where T : struct => buffer[start];
                public virtual void Hook() { }
            }

            public readonly struct Money
            {
                public Money(decimal amount) { Amount = amount; }
                public decimal Amount { get; }
                public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);
                public static implicit operator Money(decimal amount) => new(amount);
            }

            public interface IProjection<out TOut>
            {
                TOut Project(int order);
            }
        }
        """;

    private static readonly Lazy<AssemblySurface> Surface = new(() => TestCompiler.CompileAndRead(Source));

    private static TypeContract Type(string fullName) => Surface.Value.Types.Single(t => t.Type == fullName);

    [Fact]
    public void Reads_assembly_name_and_types()
    {
        Assert.Equal("TestLib", Surface.Value.Name);
        Assert.Equal(3, Surface.Value.Types.Count);
    }

    [Fact]
    public void Reads_constructor_with_parameters()
    {
        ConstructorMemberContract ctor = Type("TestLib.OrderService").Members!.OfType<ConstructorMemberContract>().Single();

        Assert.Equal(Accessibility.Public, ctor.Access);
        ParamContract p = Assert.Single(ctor.Params);
        Assert.Equal("int", p.Type);
        Assert.Equal("retries", p.Name);
    }

    [Fact]
    public void Reads_method_signatures_with_full_type_names()
    {
        MethodContract submit = Type("TestLib.OrderService").Members!
            .OfType<MethodContract>().Single(m => m.Name == "SubmitAsync");

        Assert.Equal("System.Threading.Tasks.Task<int>", submit.Returns);
        Assert.Equal(2, submit.Params!.Count);
        Assert.Equal("string", submit.Params[0].Type);
        Assert.Equal("System.Threading.CancellationToken", submit.Params[1].Type);
    }

    [Fact]
    public void Default_struct_optionals_compare_equal_to_the_sentinel()
    {
        // Metadata encodes '= default' on a struct parameter as a nullref constant - the same
        // encoding as '= null' on a reference type - so equality treats the two as one.
        MethodContract submit = Type("TestLib.OrderService").Members!
            .OfType<MethodContract>().Single(m => m.Name == "SubmitAsync");

        Assert.Equal(ConstantValue.DefaultSentinel, submit.Params![1].Default);
    }

    [Fact]
    public void Reads_static_and_virtual_modifiers()
    {
        List<MethodContract> members = Type("TestLib.OrderService").Members!.OfType<MethodContract>().ToList();

        Assert.Equal([MemberModifier.Static], members.Single(m => m.Name == "Add").Modifiers);
        Assert.Equal([MemberModifier.Virtual], members.Single(m => m.Name == "Hook").Modifiers);
    }

    [Fact]
    public void Reads_asymmetric_property_accessors()
    {
        PropertyContract property = Type("TestLib.OrderService").Members!
            .OfType<PropertyContract>().Single(p => p.Name == "PendingCount");

        Assert.Equal("int", property.Type);
        Assert.Equal(Accessibility.Public, property.Access);
        Assert.Equal(Accessibility.Public, property.Accessors!.Get);
        Assert.Equal(Accessibility.Private, property.Accessors.Set);
        Assert.Null(property.Accessors.Init);
    }

    [Fact]
    public void Reads_events_with_their_delegate_type()
    {
        EventContract evt = Type("TestLib.OrderService").Members!.OfType<EventContract>().Single();

        Assert.Equal("Completed", evt.Name);
        Assert.Equal("System.EventHandler", evt.Type);
    }

    [Fact]
    public void Reads_const_fields_with_their_value()
    {
        FieldContract field = Type("TestLib.OrderService").Members!.OfType<FieldContract>().Single();

        Assert.Equal("MaxRetries", field.Name);
        Assert.Equal([MemberModifier.Const], field.Modifiers);
        Assert.Equal(ConstantValue.Of(3), field.Value);
    }

    [Fact]
    public void Reads_generic_methods_with_constraints_and_ref_params()
    {
        MethodContract find = Type("TestLib.OrderService").Members!
            .OfType<MethodContract>().Single(m => m.Name == "Find");

        TypeParamContract tp = Assert.Single(find.TypeParams!);
        Assert.Equal("T", tp.Name);
        Assert.Contains("struct", tp.Constraints!);
        Assert.Equal(ParamModifier.Ref, find.Params![0].Modifier);
        Assert.Equal("System.Span<T>", find.Params[0].Type);
        Assert.Equal(ConstantValue.Of(0), find.Params[1].Default);
        Assert.Equal("T", find.Returns);
    }

    [Fact]
    public void Reads_readonly_structs_and_operators()
    {
        TypeContract money = Type("TestLib.Money");

        Assert.Equal(TypeKind.Struct, money.Kind);
        Assert.Contains(TypeModifier.Readonly, money.Modifiers!);

        List<OperatorContract> operators = money.Members!.OfType<OperatorContract>().ToList();
        Assert.Contains(operators, o => o is { Name: "+", Returns: "TestLib.Money" });
        Assert.Contains(operators, o => o is { Name: "implicit", Returns: "TestLib.Money" });
    }

    [Fact]
    public void Clamps_nested_type_accessibility_to_the_declaring_chain()
    {
        // Caught by dogfooding: NestedPublic inside an internal type is effectively
        // internal and must not surface in a public-scoped extract.
        var source = """
            namespace TestLib
            {
                internal class Outer
                {
                    public class Inner { }
                }
            }
            """;
        AssemblySurface surface = TestCompiler.CompileAndRead(source);
        TypeContract inner = surface.Types.Single(t => t.Type == "TestLib.Outer+Inner");

        Assert.Equal(Accessibility.Internal, inner.Access);
    }

    [Fact]
    public void Reads_interface_variance()
    {
        TypeContract projection = Type("TestLib.IProjection");

        Assert.Equal(TypeKind.Interface, projection.Kind);
        TypeParamContract tp = Assert.Single(projection.TypeParams!);
        Assert.Equal(Variance.Out, tp.Variance);

        var method = Assert.IsType<MethodContract>(Assert.Single(projection.Members!));
        Assert.Equal("TOut", method.Returns);
        Assert.Null(method.Modifiers);
    }
}
