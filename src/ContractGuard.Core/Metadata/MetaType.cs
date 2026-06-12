using System.Collections.Immutable;

namespace ContractGuard.Core.Metadata;

/// <summary>
/// Structured form of a decoded signature type. Signatures decode into this tree, nullable
/// transform flags and tuple element names are applied over it, and it renders to the
/// canonical name strings the rest of the system speaks. Nullability: 0 oblivious,
/// 1 not annotated, 2 annotated - only 2 renders as '?'.
/// </summary>
internal abstract record MetaType
{
    public byte Nullability { get; init; }

    public sealed record Named(
        string FullName,
        bool IsValueType,
        ImmutableArray<MetaType> Args) : MetaType
    {
        /// <summary>Element names when this is a ValueTuple, parallel to Args.</summary>
        public ImmutableArray<string?> TupleNames { get; init; } = [];

        public bool IsNullableOfT => FullName == "System.Nullable" && Args.Length == 1;

        public bool IsValueTuple => FullName == "System.ValueTuple" && Args.Length >= 2;
    }

    public sealed record Array(MetaType Element, int Rank) : MetaType;

    public sealed record Pointer(MetaType Element) : MetaType;

    public sealed record ByRef(MetaType Element) : MetaType;

    public sealed record GenericParam(string Name) : MetaType;

    /// <summary>modreq/modopt wrapper; transparent except where a specific modifier matters
    /// (init accessors via IsExternalInit).</summary>
    public sealed record Modified(string ModifierFullName, bool IsRequired, MetaType Inner) : MetaType;

    public sealed record FunctionPointer() : MetaType;

    /// <summary>Unwraps modifier wrappers to the underlying type.</summary>
    public MetaType Unwrap() => this is Modified m ? m.Inner.Unwrap() : this;

    public bool HasRequiredModifier(string modifierFullName) =>
        this is Modified m && (m.IsRequired && m.ModifierFullName == modifierFullName
            || m.Inner.HasRequiredModifier(modifierFullName));
}
