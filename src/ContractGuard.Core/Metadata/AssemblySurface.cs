using ContractGuard.Core.Model;

namespace ContractGuard.Core.Metadata;

/// <summary>
/// What the metadata reader observed in an assembly, expressed in the same shapes a contract
/// uses so that comparison and extraction are both direct.
/// </summary>
public sealed record AssemblySurface
{
    public required string Name { get; init; }

    public required IReadOnlyList<TypeContract> Types { get; init; }
}
