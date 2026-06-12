namespace ContractGuard.Core.TypeNames;

/// <summary>
/// Rewrites a type name to the canonical spelling both front-ends emit: built-in System
/// types become their C# keywords ("System.String" to "string"). Used where a name arrives
/// as compiler-written text (explicit interface implementation names in metadata) rather
/// than through a renderer that already speaks keywords.
/// </summary>
internal static class TypeNameCanonicalizer
{
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Char"] = "char",
        ["System.Double"] = "double",
        ["System.Single"] = "float",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.IntPtr"] = "nint",
        ["System.UIntPtr"] = "nuint",
        ["System.Object"] = "object",
        ["System.String"] = "string",
        ["System.Void"] = "void",
    };

    public static string Canonicalize(string typeName)
    {
        try
        {
            return Rewrite(TypeNameParser.Parse(typeName)).ToString();
        }
        catch (FormatException)
        {
            return typeName;
        }
    }

    private static TypeNameNode Rewrite(TypeNameNode node) => node switch
    {
        TypeNameNode.Named named => new TypeNameNode.Named(
            Keywords.TryGetValue(named.Name, out string? keyword) ? keyword : named.Name,
            named.Args.Select(Rewrite).ToList()),
        TypeNameNode.Array array => array with { Element = Rewrite(array.Element) },
        TypeNameNode.Nullable nullable => nullable with { Element = Rewrite(nullable.Element) },
        TypeNameNode.Pointer pointer => pointer with { Element = Rewrite(pointer.Element) },
        TypeNameNode.Tuple tuple => new TypeNameNode.Tuple(
            tuple.Elements.Select(e => (e.Name, Rewrite(e.Type))).ToList()),
        _ => node,
    };
}
