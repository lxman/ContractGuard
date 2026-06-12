using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace ContractGuard.Metadata;

/// <summary>
/// Decodes metadata signatures into the same canonical type-name strings the contract side
/// uses: full dotted names, C# keywords for primitives, "T?" for Nullable, "(a, b)" for
/// ValueTuple, "ref " prefix for byrefs (stripped into RefKind/ParamModifier by the reader).
/// </summary>
internal sealed class TypeNameProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly TypeNameProvider Instance = new();

    /// <summary>Marker returned for a setter return type carrying modreq IsExternalInit.</summary>
    public const string InitMarker = "$init";

    private TypeNameProvider()
    {
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        MetadataNames.FullName(reader, handle);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        MetadataNames.FullName(reader, handle);

    public string GetTypeFromSpecification(
        MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        if (genericType == "System.Nullable" && typeArguments.Length == 1)
            return typeArguments[0] + "?";

        if (genericType == "System.ValueTuple" && typeArguments.Length >= 2)
        {
            // Flatten the Rest nesting of 8+-element tuples.
            if (typeArguments.Length == 8 && typeArguments[7].StartsWith('(') && typeArguments[7].EndsWith(')'))
            {
                var rest = typeArguments[7][1..^1];
                return $"({string.Join(", ", typeArguments.Take(7))}, {rest})";
            }

            return $"({string.Join(", ", typeArguments)})";
        }

        return $"{genericType}<{string.Join(", ", typeArguments)}>";
    }

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

    public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
        index < genericContext.TypeParameters.Count ? genericContext.TypeParameters[index] : $"!{index}";

    public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
        index < genericContext.MethodParameters.Count ? genericContext.MethodParameters[index] : $"!!{index}";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
    {
        // The only modreq surfaced today is IsExternalInit on setter return types (init
        // accessors). TODO: volatile fields, ref readonly returns (modreq InAttribute),
        // ref readonly parameters (modopt RequiresLocationAttribute).
        if (isRequired && modifier == "System.Runtime.CompilerServices.IsExternalInit")
            return InitMarker;

        return unmodifiedType;
    }

    public string GetPinnedType(string elementType) => elementType;
}
