namespace ContractGuard.Core.Model;

/// <summary>
/// One parameter. JSON forms: compact ["type", "name"] pair, or an object when a modifier
/// or default value is involved.
/// </summary>
public sealed record ParamContract
{
    public required string Type { get; init; }

    /// <summary>May be omitted when parameter names are not significant.</summary>
    public string? Name { get; init; }

    public ParamModifier? Modifier { get; init; }

    /// <summary>Presence marks the parameter optional.</summary>
    public ConstantValue? Default { get; init; }
}
