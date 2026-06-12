using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace ContractGuard.Core.Metadata;

/// <summary>
/// Decodes metadata signatures into <see cref="MetaType"/> trees. Value-ness comes from the
/// signature element type (the rawTypeKind byte), which the nullable transform walk needs.
/// </summary>
internal sealed class MetaTypeProvider : ISignatureTypeProvider<MetaType, GenericContext>
{
    public static readonly MetaTypeProvider Instance = new();

    private MetaTypeProvider()
    {
    }

    public MetaType GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        (string name, bool isValue) = typeCode switch
        {
            PrimitiveTypeCode.Boolean => ("bool", true),
            PrimitiveTypeCode.Byte => ("byte", true),
            PrimitiveTypeCode.SByte => ("sbyte", true),
            PrimitiveTypeCode.Char => ("char", true),
            PrimitiveTypeCode.Double => ("double", true),
            PrimitiveTypeCode.Single => ("float", true),
            PrimitiveTypeCode.Int16 => ("short", true),
            PrimitiveTypeCode.UInt16 => ("ushort", true),
            PrimitiveTypeCode.Int32 => ("int", true),
            PrimitiveTypeCode.UInt32 => ("uint", true),
            PrimitiveTypeCode.Int64 => ("long", true),
            PrimitiveTypeCode.UInt64 => ("ulong", true),
            PrimitiveTypeCode.IntPtr => ("nint", true),
            PrimitiveTypeCode.UIntPtr => ("nuint", true),
            PrimitiveTypeCode.Object => ("object", false),
            PrimitiveTypeCode.String => ("string", false),
            PrimitiveTypeCode.Void => ("void", true),
            PrimitiveTypeCode.TypedReference => ("System.TypedReference", true),
            _ => (typeCode.ToString(), true),
        };

        return new MetaType.Named(name, isValue, []);
    }

    public MetaType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        new MetaType.Named(MetadataNames.FullName(reader, handle), IsValueKind(reader, handle, rawTypeKind), []);

    public MetaType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        new MetaType.Named(MetadataNames.FullName(reader, handle), rawTypeKind == 0x11, []);

    public MetaType GetTypeFromSpecification(
        MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public MetaType GetGenericInstantiation(MetaType genericType, ImmutableArray<MetaType> typeArguments) =>
        genericType is MetaType.Named named
            ? named with { Args = typeArguments }
            : genericType;

    public MetaType GetSZArrayType(MetaType elementType) => new MetaType.Array(elementType, 1);

    public MetaType GetArrayType(MetaType elementType, ArrayShape shape) =>
        new MetaType.Array(elementType, Math.Max(shape.Rank, 1));

    public MetaType GetByReferenceType(MetaType elementType) => new MetaType.ByRef(elementType);

    public MetaType GetPointerType(MetaType elementType) => new MetaType.Pointer(elementType);

    public MetaType GetFunctionPointerType(MethodSignature<MetaType> signature) => new MetaType.FunctionPointer();

    public MetaType GetGenericTypeParameter(GenericContext genericContext, int index) =>
        new MetaType.GenericParam(
            index < genericContext.TypeParameters.Count ? genericContext.TypeParameters[index] : $"!{index}");

    public MetaType GetGenericMethodParameter(GenericContext genericContext, int index) =>
        new MetaType.GenericParam(
            index < genericContext.MethodParameters.Count ? genericContext.MethodParameters[index] : $"!!{index}");

    public MetaType GetModifiedType(MetaType modifier, MetaType unmodifiedType, bool isRequired) =>
        new MetaType.Modified(
            modifier.Unwrap() is MetaType.Named n ? n.FullName : "?",
            isRequired,
            unmodifiedType);

    public MetaType GetPinnedType(MetaType elementType) => elementType;

    /// <summary>ELEMENT_TYPE_VALUETYPE is 0x11; when the signature doesn't say (0), classify
    /// from the definition's base type.</summary>
    private static bool IsValueKind(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        if (rawTypeKind is 0x11 or 0x12)
            return rawTypeKind == 0x11;

        TypeDefinition td = reader.GetTypeDefinition(handle);
        if (td.BaseType.IsNil)
            return false;

        string? baseName = td.BaseType.Kind switch
        {
            HandleKind.TypeDefinition => MetadataNames.FullName(reader, (TypeDefinitionHandle)td.BaseType),
            HandleKind.TypeReference => MetadataNames.FullName(reader, (TypeReferenceHandle)td.BaseType),
            _ => null,
        };

        return baseName is "System.ValueType" or "System.Enum";
    }
}
