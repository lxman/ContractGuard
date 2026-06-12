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
}
