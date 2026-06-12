using System.Text;

namespace ContractGuard.Metadata;

/// <summary>
/// Renders a <see cref="MetaType"/> tree to the canonical name string the contract side
/// speaks: primitive keywords, Nullable&lt;T&gt; as "T?", ValueTuple as "(a, b)" with element
/// names when known, '?' on reference types and type parameters annotated nullable (flag 2),
/// and a "ref " prefix for byrefs (stripped into RefKind by the reader).
/// </summary>
internal static class MetaTypeRenderer
{
    public static string Render(MetaType type)
    {
        var sb = new StringBuilder();
        Append(sb, type);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, MetaType type)
    {
        switch (type)
        {
            case MetaType.ByRef byRef:
                sb.Append("ref ");
                Append(sb, byRef.Element);
                break;

            case MetaType.Modified modified:
                Append(sb, modified.Inner);
                break;

            case MetaType.Named { IsNullableOfT: true } nullable:
                Append(sb, nullable.Args[0]);
                sb.Append('?');
                break;

            case MetaType.Named { IsValueTuple: true } tuple:
            {
                sb.Append('(');
                for (var i = 0; i < tuple.Args.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    Append(sb, tuple.Args[i]);
                    var name = i < tuple.TupleNames.Length ? tuple.TupleNames[i] : null;
                    if (name is not null)
                        sb.Append(' ').Append(name);
                }

                sb.Append(')');
                AppendAnnotation(sb, tuple);
                break;
            }

            case MetaType.Named named:
                sb.Append(named.FullName);
                if (named.Args.Length > 0)
                {
                    sb.Append('<');
                    for (var i = 0; i < named.Args.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        Append(sb, named.Args[i]);
                    }

                    sb.Append('>');
                }

                if (!named.IsValueType)
                    AppendAnnotation(sb, named);
                break;

            case MetaType.Array array:
                Append(sb, array.Element);
                sb.Append('[').Append(',', array.Rank - 1).Append(']');
                AppendAnnotation(sb, array);
                break;

            case MetaType.Pointer pointer:
                Append(sb, pointer.Element);
                sb.Append('*');
                break;

            case MetaType.GenericParam genericParam:
                sb.Append(genericParam.Name);
                AppendAnnotation(sb, genericParam);
                break;

            case MetaType.FunctionPointer:
                sb.Append("delegate*");
                break;
        }
    }

    private static void AppendAnnotation(StringBuilder sb, MetaType type)
    {
        if (type.Nullability == 2)
            sb.Append('?');
    }
}
