using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ContractGuard.Model;

namespace ContractGuard.Metadata;

/// <summary>
/// Reads an assembly's API surface from metadata - no code execution, no dependency
/// resolution. This is the enforcement front-end: it sees the artifact that actually ships,
/// including anything IL weaving did after compilation.
/// </summary>
public static class AssemblyReader
{
    public static AssemblySurface Read(string path) => Read(path, ReaderOptions.Default);

    public static AssemblySurface Read(Stream stream) => Read(stream, ReaderOptions.Default);

    public static AssemblySurface Read(string path, ReaderOptions options)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, options);
    }

    public static AssemblySurface Read(Stream stream, ReaderOptions options)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var md = pe.GetMetadataReader();
        return new Session(md, options).ReadAssembly();
    }

    private sealed class Session(MetadataReader md, ReaderOptions options)
    {
        private const string ReadOnlyAttribute = "System.Runtime.CompilerServices.IsReadOnlyAttribute";
        private const string ByRefLikeAttribute = "System.Runtime.CompilerServices.IsByRefLikeAttribute";
        private const string ExtensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
        private const string ParamArrayAttribute = "System.ParamArrayAttribute";
        private const string ExternalInitModifier = "System.Runtime.CompilerServices.IsExternalInit";

        private readonly NullabilityDecoder _decoder = new(md);

        public AssemblySurface ReadAssembly()
        {
            var name = md.GetString(md.GetAssemblyDefinition().Name);
            var types = new List<TypeContract>();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = ReadType(handle);
                if (type is not null)
                    types.Add(type);
            }

            return new AssemblySurface { Name = name, Types = types };
        }

        private TypeContract? ReadType(TypeDefinitionHandle handle)
        {
            var td = md.GetTypeDefinition(handle);
            var shortName = md.GetString(td.Name);
            if (shortName.StartsWith('<'))
                return null;

            var accessibility = EffectiveTypeAccessibility(td);
            var fullName = MetadataNames.FullName(md, handle);
            var typeContext = _decoder.TypeContext(handle);

            var typeParamNames = td.GetGenericParameters()
                .Select(h => md.GetString(md.GetGenericParameter(h).Name))
                .ToList();
            var context = new GenericContext(typeParamNames, []);

            var kind = ClassifyType(td, context, out var baseTypeName);
            var typeParams = ReadGenericParams(td.GetGenericParameters(), context);

            string? extends = null;
            if (kind == TypeKind.Class && baseTypeName is not null && baseTypeName != "System.Object")
                extends = baseTypeName;

            // TODO: base type / interface nullable annotations (NullableAttribute on the
            // InterfaceImpl rows) are not decoded.
            var implements = new List<string>();
            foreach (var ih in td.GetInterfaceImplementations())
            {
                var impl = md.GetInterfaceImplementation(ih);
                implements.Add(RenderEntity(impl.Interface, context));
            }

            var contract = new TypeContract
            {
                Type = fullName,
                Access = accessibility,
                Kind = kind,
                Modifiers = NullIfEmpty(TypeModifiers(td, kind)),
                TypeParams = NullIfEmpty(typeParams),
                Extends = extends,
                Implements = NullIfEmpty(implements),
            };

            switch (kind)
            {
                case TypeKind.Enum:
                    return contract with
                    {
                        UnderlyingType = EnumUnderlyingType(td, context),
                        Members = NullIfEmpty(ReadEnumMembers(td, context, typeContext)),
                    };
                case TypeKind.Delegate:
                    var invoke = FindMethod(td, "Invoke");
                    if (invoke is MethodDefinition invokeDef)
                    {
                        var methodContext = MethodContext(invokeDef, typeContext);
                        var sig = invokeDef.DecodeSignature(MetaTypeProvider.Instance, context);
                        var (returnType, _) = RenderReturn(invokeDef, sig.ReturnType, methodContext);
                        return contract with
                        {
                            Returns = returnType,
                            Params = BuildParams(invokeDef, sig.ParameterTypes, methodContext),
                        };
                    }

                    return contract;
                default:
                    return contract with
                    {
                        Members = NullIfEmpty(ReadMembers(td, context, typeContext)),
                    };
            }
        }

        private List<MemberContract> ReadMembers(TypeDefinition td, GenericContext typeContext, byte nullableContext)
        {
            var members = new List<MemberContract>();
            var accessorHandles = new HashSet<MethodDefinitionHandle>();
            var isInterface = (td.Attributes & TypeAttributes.Interface) != 0;

            // Properties and events first: their accessor methods must not surface as methods.
            foreach (var ph in td.GetProperties())
            {
                var pd = md.GetPropertyDefinition(ph);
                var accessors = pd.GetAccessors();
                if (!accessors.Getter.IsNil)
                    accessorHandles.Add(accessors.Getter);
                if (!accessors.Setter.IsNil)
                    accessorHandles.Add(accessors.Setter);

                var property = ReadProperty(pd, accessors, typeContext, nullableContext, isInterface);
                if (property is not null)
                    members.Add(property);
            }

            foreach (var eh in td.GetEvents())
            {
                var ed = md.GetEventDefinition(eh);
                var accessors = ed.GetAccessors();
                if (!accessors.Adder.IsNil)
                    accessorHandles.Add(accessors.Adder);
                if (!accessors.Remover.IsNil)
                    accessorHandles.Add(accessors.Remover);

                var evt = ReadEvent(ed, accessors, typeContext, nullableContext, isInterface);
                if (evt is not null)
                    members.Add(evt);
            }

            foreach (var fh in td.GetFields())
            {
                var field = ReadField(md.GetFieldDefinition(fh), typeContext, nullableContext);
                if (field is not null)
                    members.Add(field);
            }

            foreach (var mh in td.GetMethods())
            {
                if (accessorHandles.Contains(mh))
                    continue;

                var method = ReadMethod(md.GetMethodDefinition(mh), typeContext, nullableContext, isInterface);
                if (method is not null)
                    members.Add(method);
            }

            return members;
        }

        private MemberContract? ReadMethod(
            MethodDefinition def, GenericContext typeContext, byte typeNullableContext, bool isInterface)
        {
            var name = md.GetString(def.Name);
            if (name.StartsWith('<') || name == ".cctor")
                return null;

            // Explicit interface implementations ("Namespace.IFoo.Bar") are not part of the
            // public surface shape. TODO: govern them explicitly.
            if (name.Contains('.') && name is not (".ctor"))
                return null;

            var methodParamNames = def.GetGenericParameters()
                .Select(h => md.GetString(md.GetGenericParameter(h).Name))
                .ToList();
            var context = new GenericContext(typeContext.TypeParameters, methodParamNames);
            var sig = def.DecodeSignature(MetaTypeProvider.Instance, context);
            var access = MemberAccessibility(def.Attributes);
            var nullableContext = MethodContext(def, typeNullableContext);

            if (name == ".ctor")
            {
                return new ConstructorMemberContract
                {
                    Access = access,
                    Params = BuildParams(def, sig.ParameterTypes, nullableContext),
                };
            }

            if ((def.Attributes & MethodAttributes.SpecialName) != 0 && name.StartsWith("op_", StringComparison.Ordinal))
            {
                var (operatorReturn, _) = RenderReturn(def, sig.ReturnType, nullableContext);
                return new OperatorContract
                {
                    Name = OperatorSymbol(name),
                    Returns = operatorReturn,
                    Params = BuildParams(def, sig.ParameterTypes, nullableContext),
                };
            }

            var (returns, refKind) = RenderReturn(def, sig.ReturnType, nullableContext);
            var parameters = BuildParams(def, sig.ParameterTypes, nullableContext);

            if (parameters.Count > 0 && MetadataNames.HasAttribute(md, def.GetCustomAttributes(), ExtensionAttribute)
                && parameters[0].Modifier is null)
            {
                parameters[0] = parameters[0] with { Modifier = ParamModifier.This };
            }

            return new MethodContract
            {
                Name = name,
                Access = access,
                Modifiers = NullIfEmpty(MethodModifiers(def, isInterface)),
                Returns = returns,
                RefKind = refKind,
                TypeParams = NullIfEmpty(ReadGenericParams(def.GetGenericParameters(), context)),
                Params = parameters.Count == 0 ? null : parameters,
            };
        }

        private MemberContract? ReadProperty(
            PropertyDefinition pd, PropertyAccessors accessors, GenericContext context,
            byte typeNullableContext, bool isInterface)
        {
            var name = md.GetString(pd.Name);
            if (name.Contains('.'))
                return null; // explicit interface implementation

            MethodDefinition? getter = accessors.Getter.IsNil ? null : md.GetMethodDefinition(accessors.Getter);
            MethodDefinition? setter = accessors.Setter.IsNil ? null : md.GetMethodDefinition(accessors.Setter);
            if (getter is null && setter is null)
                return null;

            var nullableContext = getter is MethodDefinition g0 ? MethodContext(g0, typeNullableContext)
                : setter is MethodDefinition s0 ? MethodContext(s0, typeNullableContext)
                : typeNullableContext;

            var sig = pd.DecodeSignature(MetaTypeProvider.Instance, context);
            var typeMeta = ApplyAnnotations(sig.ReturnType, pd.GetCustomAttributes(), nullableContext);
            var (type, refKind) = SplitByRef(typeMeta);

            Accessibility? getterAccess = getter is MethodDefinition g ? MemberAccessibility(g.Attributes) : null;
            Accessibility? setterAccess = setter is MethodDefinition s ? MemberAccessibility(s.Attributes) : null;
            var access = Broadest(getterAccess, setterAccess);

            var isInit = false;
            if (setter is MethodDefinition setterDef)
            {
                var setterSig = setterDef.DecodeSignature(MetaTypeProvider.Instance, context);
                isInit = setterSig.ReturnType.HasRequiredModifier(ExternalInitModifier);
            }

            var primary = getter ?? setter!.Value;
            var accessorsContract = new AccessorsContract
            {
                Get = getterAccess,
                Set = isInit ? null : setterAccess,
                Init = isInit ? setterAccess : null,
            };

            if (sig.ParameterTypes.Length > 0)
            {
                // Indexer: index parameters come from the getter, or the setter minus 'value'.
                var paramSource = getter ?? setter!.Value;
                return new IndexerContract
                {
                    Access = access,
                    Modifiers = NullIfEmpty(MethodModifiers(primary, isInterface)),
                    Type = type,
                    RefKind = refKind,
                    Params = BuildParams(paramSource, sig.ParameterTypes, nullableContext),
                    Accessors = accessorsContract,
                };
            }

            return new PropertyContract
            {
                Name = name,
                Access = access,
                Modifiers = NullIfEmpty(MethodModifiers(primary, isInterface)),
                Type = type,
                RefKind = refKind,
                Accessors = accessorsContract,
            };
        }

        private EventContract? ReadEvent(
            EventDefinition ed, EventAccessors accessors, GenericContext context,
            byte typeNullableContext, bool isInterface)
        {
            var name = md.GetString(ed.Name);
            if (name.Contains('.'))
                return null;

            if (accessors.Adder.IsNil)
                return null;

            var adder = md.GetMethodDefinition(accessors.Adder);
            var nullableContext = MethodContext(adder, typeNullableContext);
            var typeMeta = ApplyAnnotations(MetaTypeFromEntity(ed.Type, context), ed.GetCustomAttributes(), nullableContext);

            return new EventContract
            {
                Name = name,
                Access = MemberAccessibility(adder.Attributes),
                Modifiers = NullIfEmpty(MethodModifiers(adder, isInterface)),
                Type = MetaTypeRenderer.Render(typeMeta),
            };
        }

        private FieldContract? ReadField(FieldDefinition fd, GenericContext context, byte typeNullableContext)
        {
            var name = md.GetString(fd.Name);
            if (name.StartsWith('<') || name == "value__")
                return null;

            var modifiers = new List<MemberModifier>();
            var isConst = (fd.Attributes & FieldAttributes.Literal) != 0;
            if (isConst)
            {
                modifiers.Add(MemberModifier.Const);
            }
            else
            {
                if ((fd.Attributes & FieldAttributes.Static) != 0)
                    modifiers.Add(MemberModifier.Static);
                if ((fd.Attributes & FieldAttributes.InitOnly) != 0)
                    modifiers.Add(MemberModifier.Readonly);
            }

            var typeMeta = ApplyAnnotations(
                fd.DecodeSignature(MetaTypeProvider.Instance, context), fd.GetCustomAttributes(), typeNullableContext);

            return new FieldContract
            {
                Name = name,
                Access = FieldAccessibility(fd.Attributes),
                Modifiers = NullIfEmpty(modifiers),
                Type = MetaTypeRenderer.Render(typeMeta),
                Value = isConst && !fd.GetDefaultValue().IsNil ? ReadConstant(fd.GetDefaultValue()) : null,
            };
        }

        private List<MemberContract> ReadEnumMembers(TypeDefinition td, GenericContext context, byte nullableContext)
        {
            var members = new List<MemberContract>();
            foreach (var fh in td.GetFields())
            {
                var field = ReadField(md.GetFieldDefinition(fh), context, nullableContext);
                if (field is not null)
                    members.Add(field);
            }

            return members;
        }

        private List<ParamContract> BuildParams(
            MethodDefinition def, ImmutableArray<MetaType> signatureTypes, byte nullableContext)
        {
            var bySequence = new Dictionary<int, Parameter>();
            foreach (var ph in def.GetParameters())
            {
                var p = md.GetParameter(ph);
                bySequence[p.SequenceNumber] = p;
            }

            var result = new List<ParamContract>(signatureTypes.Length);
            for (var i = 0; i < signatureTypes.Length; i++)
            {
                var meta = signatureTypes[i];
                string? name = null;
                ParamModifier? modifier = null;
                ConstantValue? defaultValue = null;

                Parameter? row = bySequence.TryGetValue(i + 1, out var found) ? found : null;
                meta = ApplyAnnotations(meta, row?.GetCustomAttributes(), nullableContext);
                var byRef = meta is MetaType.ByRef;
                if (meta is MetaType.ByRef br)
                    meta = br.Element;
                if (byRef)
                    modifier = ParamModifier.Ref;

                if (row is Parameter p)
                {
                    name = p.Name.IsNil ? null : md.GetString(p.Name);
                    if (byRef)
                    {
                        var isOut = (p.Attributes & ParameterAttributes.Out) != 0;
                        var isIn = (p.Attributes & ParameterAttributes.In) != 0;
                        modifier = isIn ? ParamModifier.In : isOut ? ParamModifier.Out : ParamModifier.Ref;
                    }
                    else if (MetadataNames.HasAttribute(md, p.GetCustomAttributes(), ParamArrayAttribute))
                    {
                        modifier = ParamModifier.Params;
                    }

                    if ((p.Attributes & ParameterAttributes.HasDefault) != 0 && !p.GetDefaultValue().IsNil)
                        defaultValue = ReadConstant(p.GetDefaultValue());
                    else if ((p.Attributes & ParameterAttributes.Optional) != 0)
                        defaultValue = ConstantValue.DefaultSentinel;
                }

                result.Add(new ParamContract
                {
                    Type = MetaTypeRenderer.Render(meta),
                    Name = name,
                    Modifier = modifier,
                    Default = defaultValue,
                });
            }

            return result;
        }

        /// <summary>Return-position annotations live on the parameter row with sequence 0.</summary>
        private (string Type, ReturnRefKind? RefKind) RenderReturn(
            MethodDefinition def, MetaType returnType, byte nullableContext)
        {
            CustomAttributeHandleCollection? returnAttributes = null;
            foreach (var ph in def.GetParameters())
            {
                var p = md.GetParameter(ph);
                if (p.SequenceNumber == 0)
                {
                    returnAttributes = p.GetCustomAttributes();
                    break;
                }
            }

            var meta = ApplyAnnotations(returnType, returnAttributes, nullableContext);
            return SplitByRef(meta);
        }

        private static (string Type, ReturnRefKind? RefKind) SplitByRef(MetaType meta)
        {
            // TODO: distinguish ref readonly returns (modreq InAttribute).
            if (meta.Unwrap() is MetaType.ByRef byRef)
                return (MetaTypeRenderer.Render(byRef.Element), ReturnRefKind.Ref);

            return (MetaTypeRenderer.Render(meta), null);
        }

        private MetaType ApplyAnnotations(MetaType meta, CustomAttributeHandleCollection? attributes, byte context)
        {
            var names = attributes is CustomAttributeHandleCollection a
                ? _decoder.FindTupleNames(a)
                : [];

            NullabilityDecoder.Flags? flags = null;
            byte effectiveContext = 0;
            if (options.DecodeNullableAnnotations)
            {
                flags = attributes is CustomAttributeHandleCollection attrs ? _decoder.FindNullableFlags(attrs) : null;
                effectiveContext = context;
            }

            return _decoder.Apply(meta, flags, effectiveContext, names);
        }

        private byte MethodContext(MethodDefinition def, byte typeContext) =>
            _decoder.FindNullableContext(def.GetCustomAttributes()) ?? typeContext;

        private List<TypeParamContract> ReadGenericParams(
            GenericParameterHandleCollection handles, GenericContext context)
        {
            var result = new List<TypeParamContract>();
            foreach (var h in handles)
            {
                var gp = md.GetGenericParameter(h);
                var attrs = gp.Attributes;
                var isStruct = (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;

                // TODO: constraint nullability ('class?', 'notnull') is not decoded.
                var constraints = new List<string>();
                if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    constraints.Add("class");
                if (isStruct)
                    constraints.Add("struct");

                foreach (var ch in gp.GetConstraints())
                {
                    var constraint = md.GetGenericParameterConstraint(ch);
                    var typeName = RenderEntity(constraint.Type, context);
                    if (typeName is "System.ValueType" or "System.Object")
                        continue;
                    constraints.Add(typeName);
                }

                if (!isStruct && (attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                    constraints.Add("new()");

                Variance? variance = null;
                if ((attrs & GenericParameterAttributes.Covariant) != 0)
                    variance = Variance.Out;
                else if ((attrs & GenericParameterAttributes.Contravariant) != 0)
                    variance = Variance.In;

                result.Add(new TypeParamContract
                {
                    Name = md.GetString(gp.Name),
                    Variance = variance,
                    Constraints = NullIfEmpty(constraints),
                });
            }

            return result;
        }

        private TypeKind ClassifyType(TypeDefinition td, GenericContext context, out string? baseTypeName)
        {
            baseTypeName = td.BaseType.IsNil ? null : RenderEntity(td.BaseType, context);
            if ((td.Attributes & TypeAttributes.Interface) != 0)
                return TypeKind.Interface;

            // TODO: detect records (EqualityContract heuristic); they currently classify as
            // class/struct, and the comparer treats record~class, record-struct~struct.
            return baseTypeName switch
            {
                "System.Enum" => TypeKind.Enum,
                "System.ValueType" => TypeKind.Struct,
                "System.MulticastDelegate" => TypeKind.Delegate,
                _ => TypeKind.Class,
            };
        }

        private List<TypeModifier> TypeModifiers(TypeDefinition td, TypeKind kind)
        {
            var result = new List<TypeModifier>();
            if (kind == TypeKind.Class)
            {
                var isAbstract = (td.Attributes & TypeAttributes.Abstract) != 0;
                var isSealed = (td.Attributes & TypeAttributes.Sealed) != 0;
                if (isAbstract && isSealed)
                {
                    result.Add(TypeModifier.Static);
                }
                else
                {
                    if (isAbstract)
                        result.Add(TypeModifier.Abstract);
                    if (isSealed)
                        result.Add(TypeModifier.Sealed);
                }
            }
            else if (kind == TypeKind.Struct)
            {
                if (MetadataNames.HasAttribute(md, td.GetCustomAttributes(), ReadOnlyAttribute))
                    result.Add(TypeModifier.Readonly);
                if (MetadataNames.HasAttribute(md, td.GetCustomAttributes(), ByRefLikeAttribute))
                    result.Add(TypeModifier.Ref);
            }

            return result;
        }

        private List<MemberModifier> MethodModifiers(MethodDefinition def, bool isInterface)
        {
            var result = new List<MemberModifier>();
            var attrs = def.Attributes;
            if ((attrs & MethodAttributes.Static) != 0)
                result.Add(MemberModifier.Static);

            if (!isInterface)
            {
                var isVirtual = (attrs & MethodAttributes.Virtual) != 0;
                var isNewSlot = (attrs & MethodAttributes.VtableLayoutMask) == MethodAttributes.NewSlot;
                var isFinal = (attrs & MethodAttributes.Final) != 0;
                if ((attrs & MethodAttributes.Abstract) != 0)
                {
                    result.Add(MemberModifier.Abstract);
                }
                else if (isVirtual && !isNewSlot)
                {
                    result.Add(MemberModifier.Override);
                    if (isFinal)
                        result.Add(MemberModifier.Sealed);
                }
                else if (isVirtual && !isFinal)
                {
                    result.Add(MemberModifier.Virtual);
                }
            }

            if (MetadataNames.HasAttribute(md, def.GetCustomAttributes(), ReadOnlyAttribute))
                result.Add(MemberModifier.Readonly);

            return result;
        }

        private string EnumUnderlyingType(TypeDefinition td, GenericContext context)
        {
            foreach (var fh in td.GetFields())
            {
                var fd = md.GetFieldDefinition(fh);
                if ((fd.Attributes & FieldAttributes.Static) == 0)
                    return MetaTypeRenderer.Render(fd.DecodeSignature(MetaTypeProvider.Instance, context));
            }

            return "int";
        }

        private MethodDefinition? FindMethod(TypeDefinition td, string name)
        {
            foreach (var mh in td.GetMethods())
            {
                var def = md.GetMethodDefinition(mh);
                if (md.GetString(def.Name) == name)
                    return def;
            }

            return null;
        }

        private MetaType MetaTypeFromEntity(EntityHandle handle, GenericContext context) => handle.Kind switch
        {
            HandleKind.TypeDefinition => MetaTypeProvider.Instance.GetTypeFromDefinition(
                md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => MetaTypeProvider.Instance.GetTypeFromReference(
                md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification =>
                md.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(MetaTypeProvider.Instance, context),
            _ => new MetaType.Named("?", false, []),
        };

        private string RenderEntity(EntityHandle handle, GenericContext context) =>
            MetaTypeRenderer.Render(MetaTypeFromEntity(handle, context));

        private ConstantValue ReadConstant(ConstantHandle handle)
        {
            var constant = md.GetConstant(handle);
            var blob = md.GetBlobReader(constant.Value);
            var value = constant.TypeCode switch
            {
                ConstantTypeCode.Boolean => (object?)blob.ReadBoolean(),
                ConstantTypeCode.Char => blob.ReadChar(),
                ConstantTypeCode.SByte => blob.ReadSByte(),
                ConstantTypeCode.Byte => blob.ReadByte(),
                ConstantTypeCode.Int16 => blob.ReadInt16(),
                ConstantTypeCode.UInt16 => blob.ReadUInt16(),
                ConstantTypeCode.Int32 => blob.ReadInt32(),
                ConstantTypeCode.UInt32 => blob.ReadUInt32(),
                ConstantTypeCode.Int64 => blob.ReadInt64(),
                ConstantTypeCode.UInt64 => blob.ReadUInt64(),
                ConstantTypeCode.Single => blob.ReadSingle(),
                ConstantTypeCode.Double => blob.ReadDouble(),
                ConstantTypeCode.String => blob.ReadUTF16(blob.Length),
                _ => null,
            };

            return ConstantValue.Of(value);
        }

        /// <summary>A nested type is only as visible as its declaring chain: NestedPublic
        /// inside an internal type is effectively internal. Clamp to the narrowest level
        /// in the chain so scope filtering sees reality.</summary>
        private Accessibility EffectiveTypeAccessibility(TypeDefinition td)
        {
            var access = TypeAccessibility(td.Attributes);
            var declaring = td.GetDeclaringType();
            while (!declaring.IsNil)
            {
                var parent = md.GetTypeDefinition(declaring);
                var parentAccess = TypeAccessibility(parent.Attributes);
                if (Rank(parentAccess) < Rank(access))
                    access = parentAccess;
                declaring = parent.GetDeclaringType();
            }

            return access;
        }

        private static Accessibility TypeAccessibility(TypeAttributes attributes) =>
            (attributes & TypeAttributes.VisibilityMask) switch
            {
                TypeAttributes.Public or TypeAttributes.NestedPublic => Accessibility.Public,
                TypeAttributes.NestedFamily => Accessibility.Protected,
                TypeAttributes.NestedFamORAssem => Accessibility.ProtectedInternal,
                TypeAttributes.NestedFamANDAssem => Accessibility.PrivateProtected,
                TypeAttributes.NestedPrivate => Accessibility.Private,
                _ => Accessibility.Internal,
            };

        private static Accessibility MemberAccessibility(MethodAttributes attributes) =>
            (attributes & MethodAttributes.MemberAccessMask) switch
            {
                MethodAttributes.Public => Accessibility.Public,
                MethodAttributes.Family => Accessibility.Protected,
                MethodAttributes.Assembly => Accessibility.Internal,
                MethodAttributes.FamORAssem => Accessibility.ProtectedInternal,
                MethodAttributes.FamANDAssem => Accessibility.PrivateProtected,
                _ => Accessibility.Private,
            };

        private static Accessibility FieldAccessibility(FieldAttributes attributes) =>
            (attributes & FieldAttributes.FieldAccessMask) switch
            {
                FieldAttributes.Public => Accessibility.Public,
                FieldAttributes.Family => Accessibility.Protected,
                FieldAttributes.Assembly => Accessibility.Internal,
                FieldAttributes.FamORAssem => Accessibility.ProtectedInternal,
                FieldAttributes.FamANDAssem => Accessibility.PrivateProtected,
                _ => Accessibility.Private,
            };

        private static Accessibility Broadest(Accessibility? a, Accessibility? b)
        {
            if (a is null)
                return b ?? Accessibility.Private;
            if (b is null)
                return a.Value;
            return Rank(a.Value) >= Rank(b.Value) ? a.Value : b.Value;
        }

        private static int Rank(Accessibility access) => access switch
        {
            Accessibility.Public => 5,
            Accessibility.ProtectedInternal => 4,
            Accessibility.Protected => 3,
            Accessibility.Internal => 2,
            Accessibility.PrivateProtected => 1,
            _ => 0,
        };

        private static string OperatorSymbol(string metadataName) => metadataName switch
        {
            "op_Addition" or "op_UnaryPlus" => "+",
            "op_Subtraction" or "op_UnaryNegation" => "-",
            "op_Multiply" => "*",
            "op_Division" => "/",
            "op_Modulus" => "%",
            "op_Equality" => "==",
            "op_Inequality" => "!=",
            "op_LessThan" => "<",
            "op_GreaterThan" => ">",
            "op_LessThanOrEqual" => "<=",
            "op_GreaterThanOrEqual" => ">=",
            "op_BitwiseAnd" => "&",
            "op_BitwiseOr" => "|",
            "op_ExclusiveOr" => "^",
            "op_LogicalNot" => "!",
            "op_OnesComplement" => "~",
            "op_Increment" => "++",
            "op_Decrement" => "--",
            "op_LeftShift" => "<<",
            "op_RightShift" => ">>",
            "op_UnsignedRightShift" => ">>>",
            "op_True" => "true",
            "op_False" => "false",
            "op_Implicit" => "implicit",
            "op_Explicit" => "explicit",
            _ => metadataName,
        };

        private static IReadOnlyList<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;
    }
}
