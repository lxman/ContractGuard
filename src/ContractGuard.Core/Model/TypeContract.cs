namespace ContractGuard.Core.Model;

/// <summary>One governed type. 'Returns'/'Params' apply to delegate types only.</summary>
public sealed record TypeContract
{
    /// <summary>Full type name including namespace; nested types use '+'. Generic types may
    /// carry their arity either as type-parameter syntax ("Repo&lt;T&gt;") or via TypeParams.</summary>
    public required string Type { get; init; }

    /// <summary>Defaults to public when omitted. A public type silently becoming internal is
    /// a contract violation, so accessibility is part of a governed type's identity.</summary>
    public Accessibility? Access { get; init; }

    public TypeKind? Kind { get; init; }

    public IReadOnlyList<TypeModifier>? Modifiers { get; init; }

    /// <summary>Enum types only: the underlying integral type.</summary>
    public string? UnderlyingType { get; init; }

    /// <summary>Required base type.</summary>
    public string? Extends { get; init; }

    /// <summary>Interfaces the type must implement (not exhaustive - extras are allowed).</summary>
    public IReadOnlyList<string>? Implements { get; init; }

    public IReadOnlyList<TypeParamContract>? TypeParams { get; init; }

    /// <summary>Overrides settings.newMembers for this type.</summary>
    public AllowDeny? NewMembers { get; init; }

    /// <summary>Overrides settings.parameterNames for this type's members.</summary>
    public Significance? ParameterNames { get; init; }

    public IReadOnlyList<MemberContract>? Members { get; init; }

    /// <summary>Delegate types only: the delegate's return type.</summary>
    public string? Returns { get; init; }

    /// <summary>Delegate types only: the delegate's parameter list.</summary>
    public IReadOnlyList<ParamContract>? Params { get; init; }
}
