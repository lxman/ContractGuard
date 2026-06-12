namespace ContractGuard.Core.Model;

/// <summary>
/// Accessor shape of a property/indexer, each present accessor mapped to its accessibility
/// (supports asymmetric cases like get-public set-private). Set and Init are mutually
/// exclusive. A null AccessorsContract on a contract entry means accessor shape is ungoverned.
/// </summary>
public sealed record AccessorsContract
{
    public Accessibility? Get { get; init; }

    public Accessibility? Set { get; init; }

    public Accessibility? Init { get; init; }
}
