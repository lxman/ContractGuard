using System.Text.Json.Serialization;

namespace ContractGuard.Core.Model;

/// <summary>
/// Base of the seven member kinds. The JSON representation is a discriminated union on the
/// "kind" property, handled by MemberContractConverter.
/// </summary>
public abstract record MemberContract
{
    /// <summary>Defaults to public when omitted.</summary>
    public Accessibility? Access { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EntryMode Mode { get; init; } = EntryMode.Required;

    /// <summary>Surfaced verbatim in gate diagnostics when this entry is violated.</summary>
    public string? Reason { get; init; }

    /// <summary>Attribute type names prescribed on this member.</summary>
    public IReadOnlyList<string>? Attributes { get; init; }

    [JsonIgnore]
    public abstract string KindName { get; }

    [JsonIgnore]
    public abstract string DisplayName { get; }
}

public sealed record MethodContract : MemberContract
{
    public required string Name { get; init; }

    public IReadOnlyList<MemberModifier>? Modifiers { get; init; }

    public required string Returns { get; init; }

    public ReturnRefKind? RefKind { get; init; }

    public IReadOnlyList<TypeParamContract>? TypeParams { get; init; }

    public IReadOnlyList<ParamContract>? Params { get; init; }

    public Significance? ParameterNames { get; init; }

    [JsonIgnore]
    public override string KindName => "method";

    [JsonIgnore]
    public override string DisplayName => Name;
}

public sealed record ConstructorMemberContract : MemberContract
{
    /// <summary>Required even when empty: the parameter list is a constructor's identity.</summary>
    public required IReadOnlyList<ParamContract> Params { get; init; }

    public Significance? ParameterNames { get; init; }

    [JsonIgnore]
    public override string KindName => "constructor";

    [JsonIgnore]
    public override string DisplayName => "constructor";
}

public sealed record PropertyContract : MemberContract
{
    public required string Name { get; init; }

    public IReadOnlyList<MemberModifier>? Modifiers { get; init; }

    public required string Type { get; init; }

    public ReturnRefKind? RefKind { get; init; }

    public AccessorsContract? Accessors { get; init; }

    [JsonIgnore]
    public override string KindName => "property";

    [JsonIgnore]
    public override string DisplayName => Name;
}

public sealed record IndexerContract : MemberContract
{
    public IReadOnlyList<MemberModifier>? Modifiers { get; init; }

    public required string Type { get; init; }

    public ReturnRefKind? RefKind { get; init; }

    public required IReadOnlyList<ParamContract> Params { get; init; }

    public AccessorsContract? Accessors { get; init; }

    public Significance? ParameterNames { get; init; }

    [JsonIgnore]
    public override string KindName => "indexer";

    [JsonIgnore]
    public override string DisplayName => "this[]";
}

public sealed record EventContract : MemberContract
{
    public required string Name { get; init; }

    public IReadOnlyList<MemberModifier>? Modifiers { get; init; }

    /// <summary>The event's delegate type.</summary>
    public required string Type { get; init; }

    [JsonIgnore]
    public override string KindName => "event";

    [JsonIgnore]
    public override string DisplayName => Name;
}

public sealed record FieldContract : MemberContract
{
    public required string Name { get; init; }

    public IReadOnlyList<MemberModifier>? Modifiers { get; init; }

    public required string Type { get; init; }

    /// <summary>For const fields: the constant value, part of the contract. Also enum member values.</summary>
    public ConstantValue? Value { get; init; }

    [JsonIgnore]
    public override string KindName => "field";

    [JsonIgnore]
    public override string DisplayName => Name;
}

public sealed record OperatorContract : MemberContract
{
    /// <summary>Operator symbol ("+", "==", ...) or "implicit"/"explicit" for conversions.
    /// Operators are always public static; Access and modifiers do not apply.</summary>
    public required string Name { get; init; }

    public required string Returns { get; init; }

    public required IReadOnlyList<ParamContract> Params { get; init; }

    public Significance? ParameterNames { get; init; }

    [JsonIgnore]
    public override string KindName => "operator";

    [JsonIgnore]
    public override string DisplayName => $"operator {Name}";
}
