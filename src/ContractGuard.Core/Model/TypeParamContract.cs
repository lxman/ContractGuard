namespace ContractGuard.Model;

public sealed record TypeParamContract
{
    public required string Name { get; init; }

    /// <summary>Interfaces and delegates only.</summary>
    public Variance? Variance { get; init; }

    /// <summary>'class', 'struct', 'notnull', 'unmanaged', 'new()', or base/interface type names.</summary>
    public IReadOnlyList<string>? Constraints { get; init; }
}
