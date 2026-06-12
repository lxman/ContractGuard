using System.Text;
using Microsoft.CodeAnalysis;

namespace ContractGuard.Analyzers;

/// <summary>
/// Renders an ITypeSymbol to the same canonical name string the metadata front-end emits:
/// primitive keywords for the ELEMENT_TYPE set (decimal deliberately stays "System.Decimal"
/// - metadata sees it as an ordinary struct), Nullable&lt;T&gt; as "T?", tuples as "(int x,
/// string y)" with explicit names only, full dotted names with '+' for nesting and all
/// generic arguments after the name, '?' on annotated reference types only when nullable
/// decoding is on. Parity with MetaTypeRenderer is pinned by the consistency harness.
/// </summary>
internal static class SymbolTypeRenderer
{
    public static string Render(ITypeSymbol type, bool decodeNullable)
    {
        var sb = new StringBuilder();
        Append(sb, type, decodeNullable);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, ITypeSymbol type, bool decodeNullable)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                Append(sb, array.ElementType, decodeNullable);
                sb.Append('[').Append(',', array.Rank - 1).Append(']');
                AppendAnnotation(sb, array, decodeNullable);
                return;

            case IPointerTypeSymbol pointer:
                Append(sb, pointer.PointedAtType, decodeNullable);
                sb.Append('*');
                return;

            case IFunctionPointerTypeSymbol:
                sb.Append("delegate*");
                return;

            case ITypeParameterSymbol typeParameter:
                sb.Append(typeParameter.Name);
                AppendAnnotation(sb, typeParameter, decodeNullable);
                return;

            case IDynamicTypeSymbol dynamicType:
                sb.Append("object");
                AppendAnnotation(sb, dynamicType, decodeNullable);
                return;

            case INamedTypeSymbol named:
                AppendNamed(sb, named, decodeNullable);
                return;

            default:
                sb.Append(type.Name);
                return;
        }
    }

    private static void AppendNamed(StringBuilder sb, INamedTypeSymbol named, bool decodeNullable)
    {
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            Append(sb, named.TypeArguments[0], decodeNullable);
            sb.Append('?');
            return;
        }

        if (named.IsTupleType && named.TupleElements.Length >= 2)
        {
            sb.Append('(');
            for (var i = 0; i < named.TupleElements.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                IFieldSymbol element = named.TupleElements[i];
                Append(sb, element.Type, decodeNullable);
                if (element.IsExplicitlyNamedTupleElement)
                    sb.Append(' ').Append(element.Name);
            }

            sb.Append(')');
            return;
        }

        if (Keyword(named.SpecialType) is { } keyword)
        {
            sb.Append(keyword);
            if (!named.IsValueType)
                AppendAnnotation(sb, named, decodeNullable);
            return;
        }

        AppendFullName(sb, named);

        // Metadata renders all generic arguments after the (possibly nested) name.
        var arguments = new List<ITypeSymbol>();
        CollectTypeArguments(named, arguments);
        if (arguments.Count > 0)
        {
            sb.Append('<');
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                Append(sb, arguments[i], decodeNullable);
            }

            sb.Append('>');
        }

        if (!named.IsValueType)
            AppendAnnotation(sb, named, decodeNullable);
    }

    /// <summary>Full dotted name with '+' for nesting, no generic arity.</summary>
    public static void AppendFullName(StringBuilder sb, INamedTypeSymbol named)
    {
        if (named.ContainingType is { } containing)
        {
            AppendFullName(sb, containing);
            sb.Append('+').Append(named.Name);
            return;
        }

        if (named.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            AppendNamespace(sb, ns);
            sb.Append('.');
        }

        sb.Append(named.Name);
    }

    private static void AppendNamespace(StringBuilder sb, INamespaceSymbol ns)
    {
        if (ns.ContainingNamespace is { IsGlobalNamespace: false } parent)
        {
            AppendNamespace(sb, parent);
            sb.Append('.');
        }

        sb.Append(ns.Name);
    }

    private static void CollectTypeArguments(INamedTypeSymbol named, List<ITypeSymbol> arguments)
    {
        if (named.ContainingType is { } containing)
            CollectTypeArguments(containing, arguments);
        arguments.AddRange(named.TypeArguments);
    }

    private static void AppendAnnotation(StringBuilder sb, ITypeSymbol type, bool decodeNullable)
    {
        if (decodeNullable && type.NullableAnnotation == NullableAnnotation.Annotated)
            sb.Append('?');
    }

    private static string? Keyword(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "bool",
        SpecialType.System_Byte => "byte",
        SpecialType.System_SByte => "sbyte",
        SpecialType.System_Char => "char",
        SpecialType.System_Double => "double",
        SpecialType.System_Single => "float",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_IntPtr => "nint",
        SpecialType.System_UIntPtr => "nuint",
        SpecialType.System_Object => "object",
        SpecialType.System_String => "string",
        SpecialType.System_Void => "void",
        _ => null,
    };
}
