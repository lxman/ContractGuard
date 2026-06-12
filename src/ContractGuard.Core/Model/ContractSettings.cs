namespace ContractGuard.Model;

/// <summary>
/// Contract-wide policy. Lives inside the contract file deliberately: CLI flags and MSBuild
/// properties control only operational concerns and can never weaken these semantics.
/// </summary>
public sealed record ContractSettings
{
    public AllowDeny NewTypes { get; init; } = AllowDeny.Allow;

    public AllowDeny NewMembers { get; init; } = AllowDeny.Allow;

    public Significance ParameterNames { get; init; } = Significance.Significant;

    public Significance DefaultValues { get; init; } = Significance.Significant;

    public Significance NullableAnnotations { get; init; } = Significance.Ignored;

    /// <summary>Defaults to ignored until the reader decodes TupleElementNamesAttribute -
    /// a default of significant would promise enforcement that does not happen yet.</summary>
    public Significance TupleElementNames { get; init; } = Significance.Ignored;

    /// <summary>Accessibility levels the gate scans. Combined levels count if either side is listed.</summary>
    public IReadOnlyList<Accessibility> Scope { get; init; } = [Accessibility.Public, Accessibility.Protected];

    /// <summary>Attribute types the comparer pays attention to. Unlisted attributes are invisible.</summary>
    public IReadOnlyList<string>? SignificantAttributes { get; init; }
}
