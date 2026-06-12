using ContractGuard.Core.Model;

namespace ContractGuard.Core.TypeNames;

/// <summary>
/// Decides whether a contract-side type name (shorthand, resolved via usings and the governed
/// type's namespace) refers to the same type as an observed-side name (fully qualified, as the
/// metadata reader renders it). Resolution is asymmetric by design: the observed side is the
/// universe of truth; the contract side carries author shorthand.
/// </summary>
public sealed class TypeNameMatcher(
    IReadOnlyList<string> usings,
    Significance nullableAnnotations,
    Significance tupleElementNames)
{
    private static readonly Dictionary<string, string> BuiltinAliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["char"] = "System.Char",
        ["decimal"] = "System.Decimal",
        ["double"] = "System.Double",
        ["float"] = "System.Single",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["nint"] = "System.IntPtr",
        ["nuint"] = "System.UIntPtr",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["object"] = "System.Object",
        ["string"] = "System.String",
        ["void"] = "System.Void",
    };

    public bool Matches(string contractName, string observedName, string governedNamespace)
    {
        TypeNameNode contract;
        TypeNameNode observed;
        try
        {
            contract = TypeNameParser.Parse(contractName);
            observed = TypeNameParser.Parse(observedName);
        }
        catch (FormatException)
        {
            return string.Equals(contractName, observedName, StringComparison.Ordinal);
        }

        return MatchNode(contract, observed, governedNamespace);
    }

    private bool MatchNode(TypeNameNode contract, TypeNameNode observed, string ns)
    {
        // Reference-type nullable annotations are not decoded from metadata yet, so the
        // observed side never carries '?' on reference types. When annotations are ignored,
        // strip the contract-side annotation. Value-type nullability (Nullable<T>) is a real
        // type difference and always shows on both sides. TODO: decode NullableAttribute.
        if (nullableAnnotations == Significance.Ignored
            && contract is TypeNameNode.Nullable cn
            && observed is not TypeNameNode.Nullable)
        {
            return MatchNode(cn.Element, observed, ns);
        }

        return (contract, observed) switch
        {
            (TypeNameNode.Nullable c, TypeNameNode.Nullable o) => MatchNode(c.Element, o.Element, ns),
            (TypeNameNode.Array c, TypeNameNode.Array o) =>
                c.Rank == o.Rank && MatchNode(c.Element, o.Element, ns),
            (TypeNameNode.Pointer c, TypeNameNode.Pointer o) => MatchNode(c.Element, o.Element, ns),
            (TypeNameNode.Tuple c, TypeNameNode.Tuple o) => MatchTuple(c, o, ns),
            (TypeNameNode.Named c, TypeNameNode.Named o) =>
                c.Args.Count == o.Args.Count
                && NameMatches(c.Name, o.Name, ns)
                && c.Args.Zip(o.Args).All(pair => MatchNode(pair.First, pair.Second, ns)),
            _ => false,
        };
    }

    private bool MatchTuple(TypeNameNode.Tuple contract, TypeNameNode.Tuple observed, string ns)
    {
        if (contract.Elements.Count != observed.Elements.Count)
            return false;

        foreach (((string? Name, TypeNameNode Type) c, (string? Name, TypeNameNode Type) o) in contract.Elements.Zip(observed.Elements))
        {
            if (!MatchNode(c.Type, o.Type, ns))
                return false;

            // Element names compare only when both sides carry them. The metadata reader does
            // not surface TupleElementNamesAttribute yet, so the observed side is currently
            // always nameless. TODO: decode tuple element names.
            if (tupleElementNames == Significance.Significant
                && c.Name is not null && o.Name is not null
                && !string.Equals(c.Name, o.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool NameMatches(string contractName, string observedName, string ns)
    {
        string observed = Canonicalize(observedName);
        if (contractName.Contains('.'))
            return string.Equals(Canonicalize(contractName), observed, StringComparison.Ordinal);

        if (BuiltinAliases.TryGetValue(contractName, out string? aliased))
            return string.Equals(aliased, observed, StringComparison.Ordinal);

        if (string.Equals(contractName, observed, StringComparison.Ordinal))
            return true;

        foreach (string u in usings)
        {
            if (string.Equals($"{u}.{contractName}", observed, StringComparison.Ordinal))
                return true;
        }

        // C#-like outward walk through the governed type's namespace and its ancestors.
        string candidate = ns;
        while (candidate.Length > 0)
        {
            if (string.Equals($"{candidate}.{contractName}", observed, StringComparison.Ordinal))
                return true;
            int dot = candidate.LastIndexOf('.');
            candidate = dot < 0 ? string.Empty : candidate[..dot];
        }

        return false;
    }

    private static string Canonicalize(string name) =>
        BuiltinAliases.GetValueOrDefault(name, name);
}
