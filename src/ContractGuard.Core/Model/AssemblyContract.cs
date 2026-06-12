using System.Text.Json.Serialization;

namespace ContractGuard.Model;

/// <summary>
/// The root of a contract: prescribes the API surface of one assembly. Mirrors
/// schema/contractguard.schema.json one to one. Also used as the in-memory shape of an
/// observed assembly surface (policy fields at their defaults) so that 'extract' is a
/// straight serialization of what the metadata reader saw.
/// </summary>
public sealed record AssemblyContract
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>Simple assembly name (no path, no .dll extension).</summary>
    public required string Assembly { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public ContractSettings Settings { get; init; } = new();

    /// <summary>Namespaces used to resolve shorthand type names.</summary>
    public IReadOnlyList<string>? Usings { get; init; }

    public required IReadOnlyList<TypeContract> Types { get; init; }
}
