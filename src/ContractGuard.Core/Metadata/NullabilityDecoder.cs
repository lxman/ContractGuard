using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace ContractGuard.Metadata;

/// <summary>
/// Decodes NullableAttribute / NullableContextAttribute / TupleElementNamesAttribute blobs
/// and applies them over <see cref="MetaType"/> trees, following
/// roslyn/docs/features/nullable-metadata.md: flags are consumed in a depth-first pre-order
/// walk where reference types, arrays, and type parameters consume a slot, generic value
/// types consume a placeholder slot, Nullable&lt;T&gt; is transparent, and non-generic value
/// types are skipped. Anything malformed degrades to the unannotated type - the gate must
/// never fail on strange metadata.
/// </summary>
internal sealed class NullabilityDecoder(MetadataReader md)
{
    private const string NullableAttribute = "System.Runtime.CompilerServices.NullableAttribute";
    private const string NullableContextAttribute = "System.Runtime.CompilerServices.NullableContextAttribute";
    private const string TupleElementNamesAttribute = "System.Runtime.CompilerServices.TupleElementNamesAttribute";

    /// <summary>Transform flags from a NullableAttribute: a single byte that repeats, or an array.</summary>
    internal readonly struct Flags
    {
        private readonly byte[]? _array;
        private readonly byte _single;

        public Flags(byte single) => _single = single;

        public Flags(byte[] array) => _array = array;

        public byte At(int index) => _array is null
            ? _single
            : index < _array.Length
                ? _array[index]
                : throw new InvalidOperationException("nullable transform flags exhausted");
    }

    public Flags? FindNullableFlags(CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            var ca = md.GetCustomAttribute(h);
            if (MetadataNames.AttributeTypeName(md, ca) != NullableAttribute)
                continue;

            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 1)
                return null;

            if (TakesByteArray(ca))
            {
                var count = blob.ReadInt32();
                if (count < 0)
                    return null;
                var bytes = new byte[count];
                for (var i = 0; i < count; i++)
                    bytes[i] = blob.ReadByte();
                return new Flags(bytes);
            }

            return new Flags(blob.ReadByte());
        }

        return null;
    }

    public byte? FindNullableContext(CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            var ca = md.GetCustomAttribute(h);
            if (MetadataNames.AttributeTypeName(md, ca) != NullableContextAttribute)
                continue;

            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 1)
                return null;
            return blob.ReadByte();
        }

        return null;
    }

    /// <summary>Context for a member: the declaring type chain, innermost first.</summary>
    public byte TypeContext(TypeDefinitionHandle handle)
    {
        while (!handle.IsNil)
        {
            var td = md.GetTypeDefinition(handle);
            if (FindNullableContext(td.GetCustomAttributes()) is byte context)
                return context;
            handle = td.GetDeclaringType();
        }

        return 0;
    }

    public ImmutableArray<string?> FindTupleNames(CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            var ca = md.GetCustomAttribute(h);
            if (MetadataNames.AttributeTypeName(md, ca) != TupleElementNamesAttribute)
                continue;

            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 1)
                return [];

            var count = blob.ReadInt32();
            if (count < 0)
                return [];

            var names = ImmutableArray.CreateBuilder<string?>(count);
            for (var i = 0; i < count; i++)
                names.Add(blob.ReadSerializedString());
            return names.ToImmutable();
        }

        return [];
    }

    /// <summary>Applies nullable flags (or a context default) and tuple element names.</summary>
    public MetaType Apply(MetaType type, Flags? flags, byte context, ImmutableArray<string?> tupleNames)
    {
        var effective = flags ?? new Flags(context);
        try
        {
            var index = 0;
            var annotated = ApplyFlags(type, effective, ref index);

            if (!tupleNames.IsDefaultOrEmpty)
            {
                var nameIndex = 0;
                annotated = ApplyTupleNames(annotated, tupleNames, ref nameIndex);
            }

            return annotated;
        }
        catch (InvalidOperationException)
        {
            return type; // malformed metadata: degrade to the unannotated type
        }
    }

    private static MetaType ApplyFlags(MetaType type, Flags flags, ref int index)
    {
        switch (type)
        {
            case MetaType.ByRef byRef:
                return byRef with { Element = ApplyFlags(byRef.Element, flags, ref index) };

            case MetaType.Modified modified:
                return modified with { Inner = ApplyFlags(modified.Inner, flags, ref index) };

            case MetaType.Named { IsNullableOfT: true } nullable:
                return nullable with { Args = [ApplyFlags(nullable.Args[0], flags, ref index)] };

            case MetaType.Named { IsValueType: false } reference:
            {
                var flag = flags.At(index++);
                return reference with
                {
                    Nullability = flag,
                    Args = ApplyAll(reference.Args, flags, ref index),
                };
            }

            case MetaType.Named { Args.Length: > 0 } genericValue:
                index++; // generic value types consume a placeholder slot
                return genericValue with { Args = ApplyAll(genericValue.Args, flags, ref index) };

            case MetaType.Named:
                return type; // non-generic value type: skipped

            case MetaType.Array array:
            {
                var flag = flags.At(index++);
                return array with
                {
                    Nullability = flag,
                    Element = ApplyFlags(array.Element, flags, ref index),
                };
            }

            case MetaType.Pointer pointer:
                index++;
                return pointer with { Element = ApplyFlags(pointer.Element, flags, ref index) };

            case MetaType.GenericParam genericParam:
                return genericParam with { Nullability = flags.At(index++) };

            default:
                return type; // function pointers: not walked. TODO when they matter.
        }
    }

    private static ImmutableArray<MetaType> ApplyAll(ImmutableArray<MetaType> types, Flags flags, ref int index)
    {
        var builder = ImmutableArray.CreateBuilder<MetaType>(types.Length);
        foreach (var t in types)
            builder.Add(ApplyFlags(t, flags, ref index));
        return builder.ToImmutable();
    }

    /// <summary>Names cover every tuple element in the type, assigned per tuple in a
    /// depth-first pre-order walk: a tuple takes one name per element, then descends.</summary>
    private static MetaType ApplyTupleNames(MetaType type, ImmutableArray<string?> names, ref int index)
    {
        switch (type)
        {
            case MetaType.Named { IsValueTuple: true } tuple:
            {
                var elementNames = ImmutableArray.CreateBuilder<string?>(tuple.Args.Length);
                for (var i = 0; i < tuple.Args.Length; i++)
                    elementNames.Add(index < names.Length ? names[index++] : null);

                var args = ImmutableArray.CreateBuilder<MetaType>(tuple.Args.Length);
                foreach (var arg in tuple.Args)
                    args.Add(ApplyTupleNames(arg, names, ref index));

                return tuple with { Args = args.ToImmutable(), TupleNames = elementNames.ToImmutable() };
            }

            case MetaType.Named named when named.Args.Length > 0:
            {
                var args = ImmutableArray.CreateBuilder<MetaType>(named.Args.Length);
                foreach (var arg in named.Args)
                    args.Add(ApplyTupleNames(arg, names, ref index));
                return named with { Args = args.ToImmutable() };
            }

            case MetaType.Array array:
                return array with { Element = ApplyTupleNames(array.Element, names, ref index) };

            case MetaType.ByRef byRef:
                return byRef with { Element = ApplyTupleNames(byRef.Element, names, ref index) };

            case MetaType.Pointer pointer:
                return pointer with { Element = ApplyTupleNames(pointer.Element, names, ref index) };

            case MetaType.Modified modified:
                return modified with { Inner = ApplyTupleNames(modified.Inner, names, ref index) };

            default:
                return type;
        }
    }

    private bool TakesByteArray(CustomAttribute attribute)
    {
        var sig = attribute.Constructor.Kind switch
        {
            HandleKind.MethodDefinition =>
                md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                    .DecodeSignature(MetaTypeProvider.Instance, GenericContext.Empty),
            HandleKind.MemberReference =>
                md.GetMemberReference((MemberReferenceHandle)attribute.Constructor)
                    .DecodeMethodSignature(MetaTypeProvider.Instance, GenericContext.Empty),
            _ => default,
        };

        return !sig.ParameterTypes.IsDefaultOrEmpty
            && sig.ParameterTypes.Length == 1
            && sig.ParameterTypes[0].Unwrap() is MetaType.Array;
    }
}
