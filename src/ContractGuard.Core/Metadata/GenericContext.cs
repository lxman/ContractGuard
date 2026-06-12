namespace ContractGuard.Core.Metadata;

/// <summary>Generic parameter names in scope while decoding a signature.</summary>
internal readonly struct GenericContext(IReadOnlyList<string> typeParameters, IReadOnlyList<string> methodParameters)
{
    public static readonly GenericContext Empty = new([], []);

    public IReadOnlyList<string> TypeParameters { get; } = typeParameters;

    public IReadOnlyList<string> MethodParameters { get; } = methodParameters;
}
