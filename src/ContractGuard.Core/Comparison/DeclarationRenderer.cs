using System.Text;
using ContractGuard.Model;
using ContractGuard.Serialization;

namespace ContractGuard.Comparison;

/// <summary>
/// Renders model elements back into C#-style declarations for diagnostics and the 'show'
/// command. One-way and lossy by design - the stored form is the elements.
/// </summary>
public static class DeclarationRenderer
{
    public static string Render(MemberContract member, string? owningTypeShortName = null)
    {
        var sb = new StringBuilder();
        switch (member)
        {
            case MethodContract m:
                AppendAccess(sb, m.Access);
                AppendModifiers(sb, m.Modifiers);
                AppendRef(sb, m.RefKind);
                sb.Append(m.Returns).Append(' ').Append(m.Name);
                AppendTypeParams(sb, m.TypeParams);
                AppendParams(sb, m.Params);
                AppendConstraints(sb, m.TypeParams);
                break;
            case ConstructorMemberContract c:
                AppendAccess(sb, c.Access);
                sb.Append(owningTypeShortName ?? "new");
                AppendParams(sb, c.Params);
                break;
            case PropertyContract p:
                AppendAccess(sb, p.Access);
                AppendModifiers(sb, p.Modifiers);
                AppendRef(sb, p.RefKind);
                sb.Append(p.Type).Append(' ').Append(p.Name);
                AppendAccessors(sb, p.Accessors, p.Access);
                break;
            case IndexerContract i:
                AppendAccess(sb, i.Access);
                AppendModifiers(sb, i.Modifiers);
                AppendRef(sb, i.RefKind);
                sb.Append(i.Type).Append(" this[");
                AppendParamList(sb, i.Params);
                sb.Append(']');
                AppendAccessors(sb, i.Accessors, i.Access);
                break;
            case EventContract e:
                AppendAccess(sb, e.Access);
                AppendModifiers(sb, e.Modifiers);
                sb.Append("event ").Append(e.Type).Append(' ').Append(e.Name);
                break;
            case FieldContract f:
                AppendAccess(sb, f.Access);
                AppendModifiers(sb, f.Modifiers);
                sb.Append(f.Type).Append(' ').Append(f.Name);
                if (f.Value is not null)
                    sb.Append(" = ").Append(f.Value);
                break;
            case OperatorContract o:
                sb.Append("public static ");
                if (o.Name is "implicit" or "explicit")
                    sb.Append(o.Name).Append(" operator ").Append(o.Returns);
                else
                    sb.Append(o.Returns).Append(" operator ").Append(o.Name);
                AppendParams(sb, o.Params);
                break;
        }

        return sb.ToString();
    }

    public static string Render(TypeContract type)
    {
        var sb = new StringBuilder();
        foreach (var m in type.Modifiers ?? [])
            sb.Append(Wire(EnumMaps.TypeModifier, m)).Append(' ');

        sb.Append(type.Kind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Record => "record",
            TypeKind.RecordStruct => "record struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "class",
        });
        sb.Append(' ').Append(type.Type);
        AppendTypeParams(sb, type.TypeParams);

        var bases = new List<string>();
        if (type.Extends is not null)
            bases.Add(type.Extends);
        bases.AddRange(type.Implements ?? []);
        if (bases.Count > 0)
            sb.Append(" : ").Append(string.Join(", ", bases));

        return sb.ToString();
    }

    private static void AppendAccess(StringBuilder sb, Accessibility? access) =>
        sb.Append(Wire(EnumMaps.Accessibility, access ?? Accessibility.Public)).Append(' ');

    private static void AppendModifiers(StringBuilder sb, IReadOnlyList<MemberModifier>? modifiers)
    {
        foreach (var m in modifiers ?? [])
            sb.Append(Wire(EnumMaps.MemberModifier, m)).Append(' ');
    }

    private static void AppendRef(StringBuilder sb, ReturnRefKind? refKind)
    {
        if (refKind is ReturnRefKind k)
            sb.Append(k == ReturnRefKind.RefReadonly ? "ref readonly " : "ref ");
    }

    private static void AppendTypeParams(StringBuilder sb, IReadOnlyList<TypeParamContract>? typeParams)
    {
        if (typeParams is null || typeParams.Count == 0)
            return;

        sb.Append('<');
        sb.Append(string.Join(", ", typeParams.Select(tp =>
            tp.Variance is Variance v ? $"{Wire(EnumMaps.Variance, v)} {tp.Name}" : tp.Name)));
        sb.Append('>');
    }

    private static void AppendConstraints(StringBuilder sb, IReadOnlyList<TypeParamContract>? typeParams)
    {
        foreach (var tp in typeParams ?? [])
        {
            if (tp.Constraints is { Count: > 0 } constraints)
                sb.Append(" where ").Append(tp.Name).Append(" : ").Append(string.Join(", ", constraints));
        }
    }

    private static void AppendParams(StringBuilder sb, IReadOnlyList<ParamContract>? parameters)
    {
        sb.Append('(');
        AppendParamList(sb, parameters);
        sb.Append(')');
    }

    private static void AppendParamList(StringBuilder sb, IReadOnlyList<ParamContract>? parameters)
    {
        if (parameters is null)
            return;

        sb.Append(string.Join(", ", parameters.Select(p =>
        {
            var parts = new List<string>(4);
            if (p.Modifier is ParamModifier m)
                parts.Add(Wire(EnumMaps.ParamModifier, m));
            parts.Add(p.Type);
            if (p.Name is not null)
                parts.Add(p.Name);
            var text = string.Join(" ", parts);
            return p.Default is null ? text : $"{text} = {p.Default}";
        })));
    }

    private static void AppendAccessors(StringBuilder sb, AccessorsContract? accessors, Accessibility? memberAccess)
    {
        if (accessors is null)
            return;

        sb.Append(" {");
        var owner = memberAccess ?? Accessibility.Public;
        Append(accessors.Get, "get");
        Append(accessors.Set, "set");
        Append(accessors.Init, "init");
        sb.Append(" }");
        return;

        void Append(Accessibility? access, string keyword)
        {
            if (access is null)
                return;
            sb.Append(' ');
            if (access != owner)
                sb.Append(Wire(EnumMaps.Accessibility, access.Value)).Append(' ');
            sb.Append(keyword).Append(';');
        }
    }

    private static string Wire<T>(Serialization.MappedEnumConverter<T> converter, T value)
        where T : struct, Enum
        => converter.NameOf(value);
}
