using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Metadata;

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
        using FileStream stream = File.OpenRead(path);
        return Read(stream, options, path);
    }

    public static AssemblySurface Read(Stream stream, ReaderOptions options) => Read(stream, options, null);

    private static AssemblySurface Read(Stream stream, ReaderOptions options, string? assemblyPath)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        MetadataReader md = pe.GetMetadataReader();
        using SourceLocator? locator = options.IncludeSourceLocations
            ? SourceLocator.TryCreate(pe, assemblyPath)
            : null;
        return new Session(md, options, locator).ReadAssembly();
    }

    private sealed class Session(MetadataReader md, ReaderOptions options, SourceLocator? locator)
    {
        private const string ReadOnlyAttribute = "System.Runtime.CompilerServices.IsReadOnlyAttribute";
        private const string ByRefLikeAttribute = "System.Runtime.CompilerServices.IsByRefLikeAttribute";
        private const string ExtensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
        private const string ParamArrayAttribute = "System.ParamArrayAttribute";
        private const string ExternalInitModifier = "System.Runtime.CompilerServices.IsExternalInit";
        private const string InModifier = "System.Runtime.InteropServices.InAttribute";
        private const string RequiresLocationModifier = "System.Runtime.CompilerServices.RequiresLocationAttribute";
        private const string VolatileModifier = "System.Runtime.CompilerServices.IsVolatile";

        private readonly NullabilityDecoder _decoder = new(md);

        public AssemblySurface ReadAssembly()
        {
            string name = md.GetString(md.GetAssemblyDefinition().Name);
            var types = new List<TypeContract>();
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                TypeContract? type = ReadType(handle);
                if (type is not null)
                    types.Add(type);
            }

            return new AssemblySurface { Name = name, Types = types };
        }

        private TypeContract? ReadType(TypeDefinitionHandle handle)
        {
            TypeDefinition td = md.GetTypeDefinition(handle);
            string shortName = md.GetString(td.Name);
            if (shortName.StartsWith('<'))
                return null;

            Accessibility accessibility = EffectiveTypeAccessibility(td);
            string fullName = MetadataNames.FullName(md, handle);
            byte typeContext = _decoder.TypeContext(handle);

            List<string> typeParamNames = td.GetGenericParameters()
                .Select(h => md.GetString(md.GetGenericParameter(h).Name))
                .ToList();
            var context = new GenericContext(typeParamNames, []);

            TypeKind kind = ClassifyType(td, context, out string? baseTypeName);
            var isRecord = kind == TypeKind.Class && IsRecordClass(td, context);
            if (isRecord)
                kind = TypeKind.Record;
            List<TypeParamContract> typeParams = ReadGenericParams(td.GetGenericParameters(), context, typeContext);

            // Base-type annotations ride on a NullableAttribute on the type itself.
            string? extends = null;
            if (kind is TypeKind.Class or TypeKind.Record
                && baseTypeName is not null && baseTypeName != "System.Object")
            {
                MetaType baseMeta = ApplyAnnotations(
                    MetaTypeFromEntity(td.BaseType, context), td.GetCustomAttributes(), typeContext);
                extends = MetaTypeRenderer.Render(baseMeta);
            }

            // Interface annotations ride on the InterfaceImpl rows.
            var implements = new List<string>();
            foreach (InterfaceImplementationHandle ih in td.GetInterfaceImplementations())
            {
                InterfaceImplementation impl = md.GetInterfaceImplementation(ih);
                MetaType ifaceMeta = ApplyAnnotations(
                    MetaTypeFromEntity(impl.Interface, context), impl.GetCustomAttributes(), typeContext);
                implements.Add(MetaTypeRenderer.Render(ifaceMeta));
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
                    MethodDefinition? invoke = FindMethod(td, "Invoke");
                    if (invoke is { } invokeDef)
                    {
                        byte methodContext = MethodContext(invokeDef, typeContext);
                        MethodSignature<MetaType> sig = invokeDef.DecodeSignature(MetaTypeProvider.Instance, context);
                        (string returnType, _) = RenderReturn(invokeDef, sig.ReturnType, methodContext);
                        return contract with
                        {
                            Returns = returnType,
                            Params = BuildParams(invokeDef, sig.ParameterTypes, methodContext),
                        };
                    }

                    return contract;
                default:
                    List<MemberContract> members = ReadMembers(td, context, typeContext, isRecord);
                    return contract with
                    {
                        Members = NullIfEmpty(members),
                        SourceLocation = members.Select(m => m.SourceLocation).FirstOrDefault(l => l is not null),
                    };
            }
        }

        /// <summary>The compiler pattern for record classes: a protected virtual
        /// EqualityContract property returning System.Type. Record structs have no marker
        /// and stay classified as struct.</summary>
        private bool IsRecordClass(TypeDefinition td, GenericContext context)
        {
            foreach (PropertyDefinitionHandle ph in td.GetProperties())
            {
                PropertyDefinition pd = md.GetPropertyDefinition(ph);
                if (md.GetString(pd.Name) != "EqualityContract")
                    continue;

                MethodDefinitionHandle getter = pd.GetAccessors().Getter;
                if (getter.IsNil)
                    return false;

                MethodDefinition g = md.GetMethodDefinition(getter);
                if ((g.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Family)
                    return false;

                MethodSignature<MetaType> sig = pd.DecodeSignature(MetaTypeProvider.Instance, context);
                return MetaTypeRenderer.Render(sig.ReturnType.Unwrap()) == "System.Type";
            }

            return false;
        }

        private List<MemberContract> ReadMembers(
            TypeDefinition td, GenericContext typeContext, byte nullableContext, bool isRecord)
        {
            var members = new List<MemberContract>();
            var accessorHandles = new HashSet<MethodDefinitionHandle>();
            bool isInterface = (td.Attributes & TypeAttributes.Interface) != 0;

            // Properties and events first: their accessor methods must not surface as methods.
            foreach (PropertyDefinitionHandle ph in td.GetProperties())
            {
                PropertyDefinition pd = md.GetPropertyDefinition(ph);
                PropertyAccessors accessors = pd.GetAccessors();
                if (!accessors.Getter.IsNil)
                    accessorHandles.Add(accessors.Getter);
                if (!accessors.Setter.IsNil)
                    accessorHandles.Add(accessors.Setter);

                // Record plumbing the compiler synthesizes is not governable surface;
                // public synthesized members (Equals, operators, Deconstruct) are.
                if (isRecord && md.GetString(pd.Name) == "EqualityContract")
                    continue;

                MemberContract? property = ReadProperty(pd, accessors, typeContext, nullableContext, isInterface);
                if (property is not null)
                    members.Add(property);
            }

            foreach (EventDefinitionHandle eh in td.GetEvents())
            {
                EventDefinition ed = md.GetEventDefinition(eh);
                EventAccessors accessors = ed.GetAccessors();
                if (!accessors.Adder.IsNil)
                    accessorHandles.Add(accessors.Adder);
                if (!accessors.Remover.IsNil)
                    accessorHandles.Add(accessors.Remover);

                EventContract? evt = ReadEvent(ed, accessors, typeContext, nullableContext, isInterface);
                if (evt is not null)
                    members.Add(evt);
            }

            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                FieldContract? field = ReadField(md.GetFieldDefinition(fh), typeContext, nullableContext);
                if (field is not null)
                    members.Add(field);
            }

            foreach (MethodDefinitionHandle mh in td.GetMethods())
            {
                if (accessorHandles.Contains(mh))
                    continue;

                MethodDefinition def = md.GetMethodDefinition(mh);
                if (isRecord && md.GetString(def.Name) == "PrintMembers")
                    continue; // record plumbing, like EqualityContract

                MemberContract? method = ReadMethod(mh, def, typeContext, nullableContext, isInterface);
                if (method is not null)
                    members.Add(method);
            }

            return members;
        }

        private MemberContract? ReadMethod(
            MethodDefinitionHandle handle, MethodDefinition def, GenericContext typeContext,
            byte typeNullableContext, bool isInterface)
        {
            string name = md.GetString(def.Name);
            if (name.StartsWith('<') || name == ".cctor")
                return null;

            // Explicit interface implementations carry the interface in their metadata name
            // ("Namespace.IFoo.Bar"); the last dot separates member from interface, and dots
            // inside generic arguments always precede a closing '>'.
            string? explicitInterface = null;
            if (name.Contains('.') && name is not (".ctor"))
            {
                int split = name.LastIndexOf('.');
                explicitInterface = TypeNames.TypeNameCanonicalizer.Canonicalize(name[..split]);
                name = name[(split + 1)..];
            }

            List<string> methodParamNames = def.GetGenericParameters()
                .Select(h => md.GetString(md.GetGenericParameter(h).Name))
                .ToList();
            var context = new GenericContext(typeContext.TypeParameters, methodParamNames);
            MethodSignature<MetaType> sig = def.DecodeSignature(MetaTypeProvider.Instance, context);
            Accessibility access = MemberAccessibility(def.Attributes);
            byte nullableContext = MethodContext(def, typeNullableContext);
            string? location = locator?.Find(handle);
            IReadOnlyList<string>? attributes = CollectAttributes(def.GetCustomAttributes());

            if (name == ".ctor")
            {
                return new ConstructorMemberContract
                {
                    Access = access,
                    Params = BuildParams(def, sig.ParameterTypes, nullableContext),
                    Attributes = attributes,
                    SourceLocation = location,
                };
            }

            if ((def.Attributes & MethodAttributes.SpecialName) != 0 && name.StartsWith("op_", StringComparison.Ordinal))
            {
                (string operatorReturn, _) = RenderReturn(def, sig.ReturnType, nullableContext);
                return new OperatorContract
                {
                    Name = OperatorNames.Symbol(name),
                    Returns = operatorReturn,
                    Params = BuildParams(def, sig.ParameterTypes, nullableContext),
                    Attributes = attributes,
                    SourceLocation = location,
                };
            }

            (string returns, ReturnRefKind? refKind) = RenderReturn(def, sig.ReturnType, nullableContext);
            List<ParamContract> parameters = BuildParams(def, sig.ParameterTypes, nullableContext);

            if (parameters.Count > 0 && MetadataNames.HasAttribute(md, def.GetCustomAttributes(), ExtensionAttribute)
                && parameters[0].Modifier is null)
            {
                parameters[0] = parameters[0] with { Modifier = ParamModifier.This };
            }

            return new MethodContract
            {
                Name = name,
                ExplicitInterface = explicitInterface,
                Access = access,
                // Explicit implementations are virtual+final+newslot plumbing; C# allows no
                // modifiers on them, so none are reported.
                Modifiers = explicitInterface is null ? NullIfEmpty(MethodModifiers(def, isInterface)) : null,
                Returns = returns,
                RefKind = refKind,
                TypeParams = NullIfEmpty(ReadGenericParams(def.GetGenericParameters(), context, nullableContext)),
                Params = parameters.Count == 0 ? null : parameters,
                Attributes = attributes,
                SourceLocation = location,
            };
        }

        private MemberContract? ReadProperty(
            PropertyDefinition pd, PropertyAccessors accessors, GenericContext context,
            byte typeNullableContext, bool isInterface)
        {
            string name = md.GetString(pd.Name);
            string? explicitInterface = null;
            if (name.Contains('.'))
            {
                int split = name.LastIndexOf('.');
                explicitInterface = TypeNames.TypeNameCanonicalizer.Canonicalize(name[..split]);
                name = name[(split + 1)..];
            }

            MethodDefinition? getter = accessors.Getter.IsNil ? null : md.GetMethodDefinition(accessors.Getter);
            MethodDefinition? setter = accessors.Setter.IsNil ? null : md.GetMethodDefinition(accessors.Setter);
            if (getter is null && setter is null)
                return null;

            byte nullableContext = getter is { } g0 ? MethodContext(g0, typeNullableContext)
                : setter is { } s0 ? MethodContext(s0, typeNullableContext)
                : typeNullableContext;

            MethodSignature<MetaType> sig = pd.DecodeSignature(MetaTypeProvider.Instance, context);
            MetaType typeMeta = ApplyAnnotations(sig.ReturnType, pd.GetCustomAttributes(), nullableContext);
            (string type, ReturnRefKind? refKind) = SplitByRef(typeMeta);

            Accessibility? getterAccess = getter is { } g ? MemberAccessibility(g.Attributes) : null;
            Accessibility? setterAccess = setter is { } s ? MemberAccessibility(s.Attributes) : null;
            Accessibility access = Broadest(getterAccess, setterAccess);

            var isInit = false;
            if (setter is { } setterDef)
            {
                MethodSignature<MetaType> setterSig = setterDef.DecodeSignature(MetaTypeProvider.Instance, context);
                isInit = setterSig.ReturnType.HasRequiredModifier(ExternalInitModifier);
            }

            MethodDefinition primary = getter ?? setter!.Value;
            var accessorsContract = new AccessorsContract
            {
                Get = getterAccess,
                Set = isInit ? null : setterAccess,
                Init = isInit ? setterAccess : null,
            };

            string? location = locator?.Find(!accessors.Getter.IsNil ? accessors.Getter : accessors.Setter);
            IReadOnlyList<string>? attributes = CollectAttributes(pd.GetCustomAttributes());

            if (sig.ParameterTypes.Length <= 0)
                return new PropertyContract
                {
                    Name = name,
                    ExplicitInterface = explicitInterface,
                    Access = access,
                    Modifiers = explicitInterface is null ? NullIfEmpty(MethodModifiers(primary, isInterface)) : null,
                    Type = type,
                    RefKind = refKind,
                    Accessors = accessorsContract,
                    Attributes = attributes,
                    SourceLocation = location,
                };
            // Indexer: index parameters come from the getter, or the setter minus 'value'.
            MethodDefinition paramSource = getter ?? setter!.Value;
            return new IndexerContract
            {
                ExplicitInterface = explicitInterface,
                Access = access,
                Modifiers = explicitInterface is null ? NullIfEmpty(MethodModifiers(primary, isInterface)) : null,
                Type = type,
                RefKind = refKind,
                Params = BuildParams(paramSource, sig.ParameterTypes, nullableContext),
                Accessors = accessorsContract,
                Attributes = attributes,
                SourceLocation = location,
            };

        }

        private EventContract? ReadEvent(
            EventDefinition ed, EventAccessors accessors, GenericContext context,
            byte typeNullableContext, bool isInterface)
        {
            string name = md.GetString(ed.Name);
            string? explicitInterface = null;
            if (name.Contains('.'))
            {
                int split = name.LastIndexOf('.');
                explicitInterface = TypeNames.TypeNameCanonicalizer.Canonicalize(name[..split]);
                name = name[(split + 1)..];
            }

            if (accessors.Adder.IsNil)
                return null;

            MethodDefinition adder = md.GetMethodDefinition(accessors.Adder);
            byte nullableContext = MethodContext(adder, typeNullableContext);
            MetaType typeMeta = ApplyAnnotations(MetaTypeFromEntity(ed.Type, context), ed.GetCustomAttributes(), nullableContext);

            return new EventContract
            {
                Name = name,
                ExplicitInterface = explicitInterface,
                Access = MemberAccessibility(adder.Attributes),
                Modifiers = explicitInterface is null ? NullIfEmpty(MethodModifiers(adder, isInterface)) : null,
                Type = MetaTypeRenderer.Render(typeMeta),
                Attributes = CollectAttributes(ed.GetCustomAttributes()),
                SourceLocation = locator?.Find(accessors.Adder),
            };
        }

        private FieldContract? ReadField(FieldDefinition fd, GenericContext context, byte typeNullableContext)
        {
            string name = md.GetString(fd.Name);
            if (name.StartsWith('<') || name == "value__")
                return null;

            var modifiers = new List<MemberModifier>();
            bool isConst = (fd.Attributes & FieldAttributes.Literal) != 0;
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

            MetaType typeMeta = ApplyAnnotations(
                fd.DecodeSignature(MetaTypeProvider.Instance, context), fd.GetCustomAttributes(), typeNullableContext);
            if (typeMeta.HasRequiredModifier(VolatileModifier))
                modifiers.Add(MemberModifier.Volatile);

            return new FieldContract
            {
                Name = name,
                Access = FieldAccessibility(fd.Attributes),
                Modifiers = NullIfEmpty(modifiers),
                Type = MetaTypeRenderer.Render(typeMeta),
                Value = isConst && !fd.GetDefaultValue().IsNil ? ReadConstant(fd.GetDefaultValue()) : null,
                Attributes = CollectAttributes(fd.GetCustomAttributes()),
            };
        }

        /// <summary>Full attribute type names on a member, when the reader is asked to
        /// collect them (the comparer filters to settings.significantAttributes).</summary>
        private IReadOnlyList<string>? CollectAttributes(CustomAttributeHandleCollection attributes)
        {
            if (!options.CollectAttributes)
                return null;

            var names = new List<string>();
            foreach (CustomAttributeHandle h in attributes)
            {
                if (MetadataNames.AttributeTypeName(md, md.GetCustomAttribute(h)) is { } name)
                    names.Add(name);
            }

            return NullIfEmpty(names);
        }

        private List<MemberContract> ReadEnumMembers(TypeDefinition td, GenericContext context, byte nullableContext)
        {
            var members = new List<MemberContract>();
            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                FieldContract? field = ReadField(md.GetFieldDefinition(fh), context, nullableContext);
                if (field is not null)
                    members.Add(field);
            }

            return members;
        }

        private List<ParamContract> BuildParams(
            MethodDefinition def, ImmutableArray<MetaType> signatureTypes, byte nullableContext)
        {
            var bySequence = new Dictionary<int, Parameter>();
            foreach (ParameterHandle ph in def.GetParameters())
            {
                Parameter p = md.GetParameter(ph);
                bySequence[p.SequenceNumber] = p;
            }

            var result = new List<ParamContract>(signatureTypes.Length);
            for (var i = 0; i < signatureTypes.Length; i++)
            {
                MetaType meta = signatureTypes[i];
                string? name = null;
                ParamModifier? modifier = null;
                ConstantValue? defaultValue = null;

                Parameter? row = bySequence.TryGetValue(i + 1, out Parameter found) ? found : null;
                meta = ApplyAnnotations(meta, row?.GetCustomAttributes(), nullableContext);
                // 'ref readonly' params: RequiresLocation rides as a row attribute on
                // ordinary methods and as a modopt on virtual/interface signatures.
                bool requiresLocation = meta.HasModifier(RequiresLocationModifier)
                    || (row is { } loc && MetadataNames.HasAttribute(md, loc.GetCustomAttributes(), RequiresLocationModifier));
                bool byRef = meta.Unwrap() is MetaType.ByRef;
                if (meta.Unwrap() is MetaType.ByRef br)
                    meta = br.Element;
                if (byRef)
                    modifier = requiresLocation ? ParamModifier.RefReadonly : ParamModifier.Ref;

                if (row is { } p)
                {
                    name = p.Name.IsNil ? null : md.GetString(p.Name);
                    if (byRef && !requiresLocation)
                    {
                        bool isOut = (p.Attributes & ParameterAttributes.Out) != 0;
                        bool isIn = (p.Attributes & ParameterAttributes.In) != 0;
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
            foreach (ParameterHandle ph in def.GetParameters())
            {
                Parameter p = md.GetParameter(ph);
                if (p.SequenceNumber != 0) continue;
                returnAttributes = p.GetCustomAttributes();
                break;
            }

            MetaType meta = ApplyAnnotations(returnType, returnAttributes, nullableContext);
            return SplitByRef(meta);
        }

        private static (string Type, ReturnRefKind? RefKind) SplitByRef(MetaType meta)
        {
            if (meta.Unwrap() is MetaType.ByRef byRef)
            {
                ReturnRefKind kind = meta.HasModifier(InModifier)
                    ? ReturnRefKind.RefReadonly
                    : ReturnRefKind.Ref;
                return (MetaTypeRenderer.Render(byRef.Element), kind);
            }

            return (MetaTypeRenderer.Render(meta), null);
        }

        private MetaType ApplyAnnotations(MetaType meta, CustomAttributeHandleCollection? attributes, byte context)
        {
            ImmutableArray<string?> names = attributes is { } a
                ? _decoder.FindTupleNames(a)
                : [];

            NullabilityDecoder.Flags? flags = null;
            byte effectiveContext = 0;
            if (!options.DecodeNullableAnnotations) return _decoder.Apply(meta, flags, effectiveContext, names);
            flags = attributes is { } attrs ? _decoder.FindNullableFlags(attrs) : null;
            effectiveContext = context;

            return _decoder.Apply(meta, flags, effectiveContext, names);
        }

        private byte MethodContext(MethodDefinition def, byte typeContext) =>
            _decoder.FindNullableContext(def.GetCustomAttributes()) ?? typeContext;

        private List<TypeParamContract> ReadGenericParams(
            GenericParameterHandleCollection handles, GenericContext context, byte nullableContext)
        {
            var result = new List<TypeParamContract>();
            foreach (GenericParameterHandle h in handles)
            {
                GenericParameter gp = md.GetGenericParameter(h);
                GenericParameterAttributes attrs = gp.Attributes;
                bool isClass = (attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0;
                bool isStruct = (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;

                // Constraint nullability rides on a NullableAttribute on the generic
                // parameter row (context-compressed like everything else): 'class' + flag 2
                // is 'class?'; flag 1 with no class/struct constraint is 'notnull'.
                // TODO: annotations on constraint TYPES (IFoo?).
                byte? gpNullability = null;
                if (options.DecodeNullableAnnotations)
                {
                    gpNullability = _decoder.FindNullableFlags(gp.GetCustomAttributes()) is { } gpFlags
                        ? gpFlags.At(0)
                        : nullableContext;
                }

                var constraints = new List<string>();
                if (isClass)
                    constraints.Add(gpNullability == 2 ? "class?" : "class");
                if (isStruct)
                    constraints.Add("struct");
                if (!isClass && !isStruct && gpNullability == 1)
                    constraints.Add("notnull");

                foreach (GenericParameterConstraintHandle ch in gp.GetConstraints())
                {
                    GenericParameterConstraint constraint = md.GetGenericParameterConstraint(ch);
                    MetaType constraintMeta = MetaTypeFromEntity(constraint.Type, context);
                    if (MetaTypeRenderer.Render(constraintMeta) is "System.ValueType" or "System.Object")
                        continue;

                    // Constraint-type annotations (where T : IFoo?) ride on the constraint row.
                    MetaType annotated = ApplyAnnotations(
                        constraintMeta, constraint.GetCustomAttributes(), nullableContext);
                    constraints.Add(MetaTypeRenderer.Render(annotated));
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

            // Record classes are detected separately via IsRecordClass; record structs have
            // no metadata marker and the comparer treats record-struct~struct.
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
            switch (kind)
            {
                case TypeKind.Class or TypeKind.Record:
                {
                    bool isAbstract = (td.Attributes & TypeAttributes.Abstract) != 0;
                    bool isSealed = (td.Attributes & TypeAttributes.Sealed) != 0;
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

                    break;
                }
                case TypeKind.Struct:
                {
                    if (MetadataNames.HasAttribute(md, td.GetCustomAttributes(), ReadOnlyAttribute))
                        result.Add(TypeModifier.Readonly);
                    if (MetadataNames.HasAttribute(md, td.GetCustomAttributes(), ByRefLikeAttribute))
                        result.Add(TypeModifier.Ref);
                    break;
                }
            }

            return result;
        }

        private List<MemberModifier> MethodModifiers(MethodDefinition def, bool isInterface)
        {
            var result = new List<MemberModifier>();
            MethodAttributes attrs = def.Attributes;
            if ((attrs & MethodAttributes.Static) != 0)
                result.Add(MemberModifier.Static);

            if (!isInterface)
            {
                bool isVirtual = (attrs & MethodAttributes.Virtual) != 0;
                bool isNewSlot = (attrs & MethodAttributes.VtableLayoutMask) == MethodAttributes.NewSlot;
                bool isFinal = (attrs & MethodAttributes.Final) != 0;
                if ((attrs & MethodAttributes.Abstract) != 0)
                {
                    result.Add(MemberModifier.Abstract);
                }
                else switch (isVirtual)
                {
                    case true when !isNewSlot:
                    {
                        result.Add(MemberModifier.Override);
                        if (isFinal)
                            result.Add(MemberModifier.Sealed);
                        break;
                    }
                    case true when !isFinal:
                        result.Add(MemberModifier.Virtual);
                        break;
                }
            }

            if (MetadataNames.HasAttribute(md, def.GetCustomAttributes(), ReadOnlyAttribute))
                result.Add(MemberModifier.Readonly);

            return result;
        }

        private string EnumUnderlyingType(TypeDefinition td, GenericContext context)
        {
            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                FieldDefinition fd = md.GetFieldDefinition(fh);
                if ((fd.Attributes & FieldAttributes.Static) == 0)
                    return MetaTypeRenderer.Render(fd.DecodeSignature(MetaTypeProvider.Instance, context));
            }

            return "int";
        }

        private MethodDefinition? FindMethod(TypeDefinition td, string name)
        {
            foreach (MethodDefinitionHandle mh in td.GetMethods())
            {
                MethodDefinition def = md.GetMethodDefinition(mh);
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
            Constant constant = md.GetConstant(handle);
            BlobReader blob = md.GetBlobReader(constant.Value);
            object? value = constant.TypeCode switch
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
            Accessibility access = TypeAccessibility(td.Attributes);
            TypeDefinitionHandle declaring = td.GetDeclaringType();
            while (!declaring.IsNil)
            {
                TypeDefinition parent = md.GetTypeDefinition(declaring);
                Accessibility parentAccess = TypeAccessibility(parent.Attributes);
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


        private static IReadOnlyList<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;
    }
}
