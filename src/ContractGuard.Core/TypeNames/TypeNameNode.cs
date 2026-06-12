namespace ContractGuard.TypeNames;

/// <summary>
/// AST of a type reference name - the one place a string survives element decomposition.
/// The grammar is deliberately tiny: dotted names, generics, arrays, nullable suffix,
/// tuples, pointers. Nothing here parses C#.
/// </summary>
public abstract record TypeNameNode
{
    public sealed record Named(string Name, IReadOnlyList<TypeNameNode> Args) : TypeNameNode
    {
        public override string ToString() =>
            Args.Count == 0 ? Name : $"{Name}<{string.Join(", ", Args)}>";
    }

    public sealed record Array(TypeNameNode Element, int Rank) : TypeNameNode
    {
        public override string ToString() => $"{Element}[{new string(',', Rank - 1)}]";
    }

    public sealed record Nullable(TypeNameNode Element) : TypeNameNode
    {
        public override string ToString() => $"{Element}?";
    }

    public sealed record Tuple(IReadOnlyList<(string? Name, TypeNameNode Type)> Elements) : TypeNameNode
    {
        public override string ToString() =>
            $"({string.Join(", ", Elements.Select(e => e.Name is null ? e.Type.ToString() : $"{e.Type} {e.Name}"))})";
    }

    public sealed record Pointer(TypeNameNode Element) : TypeNameNode
    {
        public override string ToString() => $"{Element}*";
    }
}
