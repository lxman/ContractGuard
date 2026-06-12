namespace ContractGuard.Core.Metadata;

public sealed record ReaderOptions
{
    public static readonly ReaderOptions Default = new();

    /// <summary>
    /// Decode NullableAttribute/NullableContextAttribute and render reference-type
    /// nullability ('string?'). Verification must set this to match the contract's
    /// nullableAnnotations setting: at the rendered-string level an annotated 'string?' and
    /// the real type 'int?' (Nullable&lt;int&gt;) are indistinguishable, so 'ignored' is
    /// implemented by not decoding annotations rather than by stripping at comparison time.
    /// Extraction wants the default (true): emit the truth.
    /// </summary>
    public bool DecodeNullableAnnotations { get; init; } = true;

    /// <summary>
    /// Record the full type names of custom attributes on members. Off by default: the
    /// comparer only consults attributes when the contract lists significantAttributes, and
    /// extract should not emit attribute noise into hand-maintained contracts.
    /// </summary>
    public bool CollectAttributes { get; init; }

    /// <summary>
    /// Resolve source file/line for members from the portable PDB (sibling file or embedded)
    /// so diagnostics can point at code rather than only at the contract file. Cheap, on by
    /// default; locations never serialize into contracts.
    /// </summary>
    public bool IncludeSourceLocations { get; init; } = true;
}
