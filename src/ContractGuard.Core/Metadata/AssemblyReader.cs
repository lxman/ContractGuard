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
    public static AssemblySurface Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static AssemblySurface Read(Stream stream)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var md = pe.GetMetadataReader();
        return new Session(md).ReadAssembly();
    }

    private sealed class Session(MetadataReader md)
    {
        private const string ReadOnlyAttribute = "System.Runtime.CompilerServices.IsReadOnlyAttribute";
        private const string ByRefLikeAttribute = "System.Runtime.CompilerServices.IsByRefLikeAttribute";
        private const string ExtensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
        private const string ParamArrayAttribute = "System.ParamArrayAttribute";

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

            var accessibility = TypeAccessibility(td.Attributes);
            var fullName = MetadataNames.FullName(md, handle);

            var typeParamNames = td.GetGenericParameters()
                .Select(h => md.GetString(md.GetGenericParameter(h).Name))
                .ToList();
            var context = new GenericContext(typeParamNames, []);

            var kind = ClassifyType(td, context, out var baseTypeName);
            var typeParams = ReadGenericParams(td.GetGenericParameters(), context);

            string? extends = null;
            if (kind == TypeKind.Class && baseTypeName is not null && baseTypeName != "System.Object")
                extends = baseTypeName;

            var implements = new List<string>();
            foreach (var ih in td.GetInterfaceImplementations())
            {
                var impl = md.GetInterfaceImplementation(ih);
                implements.Add(TypeNameFromEntity(impl.Interface, context));
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
                        Members = NullIfEmpty(ReadEnumMembers(td, context)),
                    };
                case TypeKind.Delegate:
                    var invoke = FindMethod(td, "Invoke");
                    if (invoke is MethodDefinition invokeDef)
                    {
                        var sig = invokeDef.DecodeSignature(TypeNameProvider.Instance, context);
                        return contract with
                        {
                            Returns = StripRef(sig.ReturnType, out _),
                            Params = BuildParams(invokeDef, sig.ParameterTypes),
                        };
                    }

                    return contract;
                default:
                    return contract with
                    {
                        Members = NullIfEmpty(ReadMembers(td, context)),
                    };
            }
        }

        private List<MemberContract> ReadMembers(TypeDefinition td, GenericContext typeContext)
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

                var property = ReadProperty(pd, accessors, typeContext, isInterface);
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

                var evt = ReadEvent(ed, accessors, typeContext, isInterface);
                if (evt is not null)
                    members.Add(evt);
            }

            foreach (var fh in td.GetFields())
            {
                var field = ReadField(md.GetFieldDefinition(fh), typeContext);
                if (field is not null)
                    members.Add(field);
            }

            foreach (var mh in td.GetMethods())
            {
                if (accessorHandles.Contains(mh))
                    continue;

                var method = ReadMethod(md.GetMethodDefinition(mh), typeContext, isInterface);
                if (method is not null)
                    members.Add(method);
            }

            return members;
        }

        private MemberContract? ReadMethod(MethodDefinition def, GenericContext typeContext, bool isInterface)
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
            var sig = def.DecodeSignature(TypeNameProvider.Instance, context);
            var access = MemberAccessibility(def.Attributes);

            if (name == ".ctor")
            {
                return new ConstructorMemberContract
                {
                    Access = access,
                    Params = BuildParams(def, sig.ParameterTypes),
                };
            }

            if ((def.Attributes & MethodAttributes.SpecialName) != 0 && name.StartsWith("op_", StringComparison.Ordinal))
            {
                return new OperatorContract
                {
                    Name = OperatorSymbol(name),
                    Returns = StripRef(sig.ReturnType, out _),
                    Params = BuildParams(def, sig.ParameterTypes),
                };
            }

            var returns = StripRef(sig.ReturnType, out var refKind);
            var parameters = BuildParams(def, sig.ParameterTypes);

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
            PropertyDefinition pd, PropertyAccessors accessors, GenericContext context, bool isInterface)
        {
            var name = md.GetString(pd.Name);
            if (name.Contains('.'))
                return null; // explicit interface implementation

            MethodDefinition? getter = accessors.Getter.IsNil ? null : md.GetMethodDefinition(accessors.Getter);
            MethodDefinition? setter = accessors.Setter.IsNil ? null : md.GetMethodDefinition(accessors.Setter);
            if (getter is null && setter is null)
                return null;

            var sig = pd.DecodeSignature(TypeNameProvider.Instance, context);
            var type = StripRef(sig.ReturnType, out var refKind);

            Accessibility? getterAccess = getter is MethodDefinition g ? MemberAccessibility(g.Attributes) : null;
            Accessibility? setterAccess = setter is MethodDefinition s ? MemberAccessibility(s.Attributes) : null;
            var access = Broadest(getterAccess, setterAccess);

            var isInit = false;
            if (setter is MethodDefinition setterDef)
            {
                var setterSig = setterDef.DecodeSignature(TypeNameProvider.Instance, context);
                isInit = setterSig.ReturnType == TypeNameProvider.InitMarker;
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
                    Params = BuildParams(paramSource, sig.ParameterTypes),
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
            EventDefinition ed, EventAccessors accessors, GenericContext context, bool isInterface)
        {
            var name = md.GetString(ed.Name);
            if (name.Contains('.'))
                return null;

            if (accessors.Adder.IsNil)
                return null;

            var adder = md.GetMethodDefinition(accessors.Adder);
            return new EventContract
            {
                Name = name,
                Access = MemberAccessibility(adder.Attributes),
                Modifiers = NullIfEmpty(MethodModifiers(adder, isInterface)),
                Type = TypeNameFromEntity(ed.Type, context),
            };
        }

        private FieldContract? ReadField(FieldDefinition fd, GenericContext context)
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

            return new FieldContract
            {
                Name = name,
                Access = FieldAccessibility(fd.Attributes),
                Modifiers = NullIfEmpty(modifiers),
                Type = fd.DecodeSignature(TypeNameProvider.Instance, context),
                Value = isConst && !fd.GetDefaultValue().IsNil ? ReadConstant(fd.GetDefaultValue()) : null,
            };
        }

        private List<MemberContract> ReadEnumMembers(TypeDefinition td, GenericContext context)
        {
            var members = new List<MemberContract>();
            foreach (var fh in td.GetFields())
            {
                var field = ReadField(md.GetFieldDefinition(fh), context);
                if (field is not null)
                    members.Add(field);
            }

            return members;
        }

        private List<ParamContract> BuildParams(MethodDefinition def, IReadOnlyList<string> signatureTypes)
        {
            var bySequence = new Dictionary<int, Parameter>();
            foreach (var ph in def.GetParameters())
            {
                var p = md.GetParameter(ph);
                bySequence[p.SequenceNumber] = p;
            }

            var result = new List<ParamContract>(signatureTypes.Count);
            for (var i = 0; i < signatureTypes.Count; i++)
            {
                var type = StripRefPrefix(signatureTypes[i], out var byRef);
                string? name = null;
                ParamModifier? modifier = byRef ? ParamModifier.Ref : null;
                ConstantValue? defaultValue = null;

                if (bySequence.TryGetValue(i + 1, out var row))
                {
                    name = row.Name.IsNil ? null : md.GetString(row.Name);
                    if (byRef)
                    {
                        var isOut = (row.Attributes & ParameterAttributes.Out) != 0;
                        var isIn = (row.Attributes & ParameterAttributes.In) != 0;
                        modifier = isIn ? ParamModifier.In : isOut ? ParamModifier.Out : ParamModifier.Ref;
                    }
                    else if (MetadataNames.HasAttribute(md, row.GetCustomAttributes(), ParamArrayAttribute))
                    {
                        modifier = ParamModifier.Params;
                    }

                    if ((row.Attributes & ParameterAttributes.HasDefault) != 0 && !row.GetDefaultValue().IsNil)
                        defaultValue = ReadConstant(row.GetDefaultValue());
                    else if ((row.Attributes & ParameterAttributes.Optional) != 0)
                        defaultValue = ConstantValue.DefaultSentinel;
                }

                result.Add(new ParamContract { Type = type, Name = name, Modifier = modifier, Default = defaultValue });
            }

            return result;
        }

        private List<TypeParamContract> ReadGenericParams(
            GenericParameterHandleCollection handles, GenericContext context)
        {
            var result = new List<TypeParamContract>();
            foreach (var h in handles)
            {
                var gp = md.GetGenericParameter(h);
                var attrs = gp.Attributes;
                var isStruct = (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;

                var constraints = new List<string>();
                if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    constraints.Add("class");
                if (isStruct)
                    constraints.Add("struct");

                foreach (var ch in gp.GetConstraints())
                {
                    var constraint = md.GetGenericParameterConstraint(ch);
                    var typeName = TypeNameFromEntity(constraint.Type, context);
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
            baseTypeName = td.BaseType.IsNil ? null : TypeNameFromEntity(td.BaseType, context);
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
                    return fd.DecodeSignature(TypeNameProvider.Instance, context);
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

        private string TypeNameFromEntity(EntityHandle handle, GenericContext context) => handle.Kind switch
        {
            HandleKind.TypeDefinition => MetadataNames.FullName(md, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => MetadataNames.FullName(md, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification =>
                md.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(TypeNameProvider.Instance, context),
            _ => "?",
        };

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

        private static string StripRef(string type, out ReturnRefKind? refKind)
        {
            // TODO: distinguish ref readonly returns (modreq InAttribute).
            if (type.StartsWith("ref ", StringComparison.Ordinal))
            {
                refKind = ReturnRefKind.Ref;
                return type[4..];
            }

            refKind = null;
            return type;
        }

        private static string StripRefPrefix(string type, out bool byRef)
        {
            byRef = type.StartsWith("ref ", StringComparison.Ordinal);
            return byRef ? type[4..] : type;
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

            static int Rank(Accessibility access) => access switch
            {
                Accessibility.Public => 5,
                Accessibility.ProtectedInternal => 4,
                Accessibility.Protected => 3,
                Accessibility.Internal => 2,
                Accessibility.PrivateProtected => 1,
                _ => 0,
            };
        }

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
