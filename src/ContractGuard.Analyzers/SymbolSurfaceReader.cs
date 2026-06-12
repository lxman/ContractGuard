using System.Text;
using ContractGuard.Core;
using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using Microsoft.CodeAnalysis;
using Accessibility = ContractGuard.Core.Model.Accessibility;
using TypeKind = ContractGuard.Core.Model.TypeKind;
using Variance = ContractGuard.Core.Model.Variance;

namespace ContractGuard.Analyzers;

/// <summary>
/// The Roslyn front-end: reads an assembly's API surface from symbols, producing the same
/// model the metadata reader produces from a built assembly. Every quirk here that mimics a
/// metadata limitation (record structs as structs, decimal defaults as the sentinel,
/// unmanaged rendered as struct) is deliberate: the consistency harness asserts the two
/// front-ends emit identical models, so the analyzer and the gate can never disagree.
/// </summary>
public static class SymbolSurfaceReader
{
    public static AssemblySurface Read(IAssemblySymbol assembly, ReaderOptions options)
    {
        var types = new List<TypeContract>();
        CollectTypes(assembly.GlobalNamespace, types, options);
        return new AssemblySurface { Name = assembly.Name, Types = types };
    }

    private static void CollectTypes(INamespaceSymbol ns, List<TypeContract> types, ReaderOptions options)
    {
        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            CollectTypes(child, types, options);

        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
            CollectType(type, types, options);
    }

    private static void CollectType(INamedTypeSymbol type, List<TypeContract> types, ReaderOptions options)
    {
        TypeContract? contract = ReadType(type, options);
        if (contract is not null)
            types.Add(contract);

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            CollectType(nested, types, options);
    }

    private static TypeContract? ReadType(INamedTypeSymbol type, ReaderOptions options)
    {
        if (type.Name.Length == 0 || type.Name[0] == '<')
            return null;

        bool decode = options.DecodeNullableAnnotations;
        var fullName = new StringBuilder();
        SymbolTypeRenderer.AppendFullName(fullName, type);

        // Record structs have no metadata marker; the metadata front-end reports them as
        // structs, so this one does too.
        TypeKind kind = type.TypeKind switch
        {
            Microsoft.CodeAnalysis.TypeKind.Interface => TypeKind.Interface,
            Microsoft.CodeAnalysis.TypeKind.Enum => TypeKind.Enum,
            Microsoft.CodeAnalysis.TypeKind.Delegate => TypeKind.Delegate,
            Microsoft.CodeAnalysis.TypeKind.Struct => TypeKind.Struct,
            _ => type.IsRecord ? TypeKind.Record : TypeKind.Class,
        };

        string? extends = null;
        if (kind is TypeKind.Class or TypeKind.Record
            && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
        {
            extends = SymbolTypeRenderer.Render(baseType, decode);
        }

        // The compiler emits the transitive closure of each DECLARED interface into the
        // InterfaceImpl table (but not interfaces inherited through the base class), so the
        // symbol side walks the same closure. Caught in the field by a store class declaring
        // IQueryableRoleStore<T>, whose IRoleStore<T> and IDisposable bases the direct
        // Interfaces list never showed.
        List<string> implements = type.Interfaces
            .SelectMany(i => new[] { i }.Concat(i.AllInterfaces))
            .Select(i => SymbolTypeRenderer.Render(i, decode))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var contract = new TypeContract
        {
            Type = fullName.ToString(),
            Access = EffectiveAccessibility(type),
            Kind = kind,
            Modifiers = NullIfEmpty(TypeModifiers(type, kind)),
            TypeParams = NullIfEmpty(ReadTypeParams(type.TypeParameters, options)),
            Extends = extends,
            Implements = NullIfEmpty(implements),
            Attributes = CollectAttributes(type, options),
            SourceLocation = LocationOf(type),
        };

        switch (kind)
        {
            case TypeKind.Enum:
                return contract with
                {
                    UnderlyingType = type.EnumUnderlyingType is { } underlying
                        ? SymbolTypeRenderer.Render(underlying, decode)
                        : "int",
                    Members = NullIfEmpty(ReadEnumMembers(type, options)),
                };
            case TypeKind.Delegate:
                if (type.DelegateInvokeMethod is { } invoke)
                {
                    return contract with
                    {
                        Returns = SymbolTypeRenderer.Render(invoke.ReturnType, decode),
                        Params = BuildParams(invoke, options),
                    };
                }

                return contract;
            default:
                List<MemberContract> members = ReadMembers(type, options);
                return contract with
                {
                    Members = NullIfEmpty(members),
                    SourceLocation = members.Select(m => m.SourceLocation).FirstOrDefault(l => l is not null)
                        ?? contract.SourceLocation,
                };
        }
    }

    /// <summary>Grouped like the metadata reader: properties, events, fields, methods.</summary>
    private static List<MemberContract> ReadMembers(INamedTypeSymbol type, ReaderOptions options)
    {
        var members = new List<MemberContract>();
        bool isInterface = type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface;
        bool isRecord = type.IsRecord && type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class;
        ISymbol[] all = type.GetMembers().ToArray();

        foreach (IPropertySymbol property in all.OfType<IPropertySymbol>())
        {
            if (isRecord && property.Name == "EqualityContract")
                continue;

            members.Add(ReadProperty(property, isInterface, options));
        }

        foreach (IEventSymbol evt in all.OfType<IEventSymbol>())
            members.Add(ReadEvent(evt, isInterface, options));

        foreach (IFieldSymbol field in all.OfType<IFieldSymbol>())
        {
            FieldContract? contract = ReadField(field, options);
            if (contract is not null)
                members.Add(contract);
        }

        foreach (IMethodSymbol method in all.OfType<IMethodSymbol>())
        {
            if (isRecord && method.Name == "PrintMembers")
                continue;

            MemberContract? contract = ReadMethod(method, isInterface, options);
            if (contract is not null)
                members.Add(contract);
        }

        return members;
    }

    private static MemberContract? ReadMethod(IMethodSymbol method, bool isInterface, ReaderOptions options)
    {
        if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
            or MethodKind.StaticConstructor)
        {
            return null;
        }

        // Roslyn synthesizes a parameterless constructor symbol for structs; metadata has
        // no such .ctor row. (Classes' implicit constructors ARE emitted and stay.)
        if (method.MethodKind == MethodKind.Constructor
            && method.IsImplicitlyDeclared
            && method.ContainingType.IsValueType)
        {
            return null;
        }

        string name = method.MethodKind == MethodKind.ExplicitInterfaceImplementation
            ? method.ExplicitInterfaceImplementations[0].Name
            : method.Name;
        if (name.Length == 0 || name[0] == '<')
            return null;

        bool decode = options.DecodeNullableAnnotations;
        string? location = LocationOf(method);
        IReadOnlyList<string>? attributes = CollectAttributes(method, options);

        if (method.MethodKind == MethodKind.Constructor)
        {
            return new ConstructorMemberContract
            {
                Access = MapAccessibility(method.DeclaredAccessibility),
                Params = BuildParams(method, options) ?? [],
                Attributes = attributes,
                SourceLocation = location,
            };
        }

        if (method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
        {
            return new OperatorContract
            {
                Name = OperatorNames.Symbol(method.Name),
                Returns = SymbolTypeRenderer.Render(method.ReturnType, decode),
                Params = BuildParams(method, options) ?? [],
                Attributes = attributes,
                SourceLocation = location,
            };
        }

        string? explicitInterface = method.MethodKind == MethodKind.ExplicitInterfaceImplementation
            ? SymbolTypeRenderer.Render(
                method.ExplicitInterfaceImplementations[0].ContainingType, decodeNullable: false)
            : null;

        List<ParamContract>? parameters = BuildParams(method, options);
        if (parameters is { Count: > 0 } && method.IsExtensionMethod && parameters[0].Modifier is null)
            parameters[0] = parameters[0] with { Modifier = ParamModifier.This };

        return new MethodContract
        {
            Name = name,
            ExplicitInterface = explicitInterface,
            Access = MapAccessibility(method.DeclaredAccessibility),
            Modifiers = explicitInterface is null ? NullIfEmpty(MethodModifiers(method, isInterface)) : null,
            Returns = SymbolTypeRenderer.Render(method.ReturnType, decode),
            RefKind = method.RefKind switch
            {
                RefKind.Ref => ReturnRefKind.Ref,
                RefKind.RefReadOnly => ReturnRefKind.RefReadonly,
                _ => null,
            },
            TypeParams = NullIfEmpty(ReadTypeParams(method.TypeParameters, options)),
            Params = parameters,
            Attributes = attributes,
            SourceLocation = location,
        };
    }

    private static MemberContract ReadProperty(IPropertySymbol property, bool isInterface, ReaderOptions options)
    {
        bool decode = options.DecodeNullableAnnotations;
        IMethodSymbol primary = property.GetMethod ?? property.SetMethod!;
        bool isExplicit = !property.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
        string? explicitInterface = isExplicit
            ? SymbolTypeRenderer.Render(
                property.ExplicitInterfaceImplementations[0].ContainingType, decodeNullable: false)
            : null;

        var accessors = new AccessorsContract
        {
            Get = property.GetMethod is { } g ? MapAccessibility(g.DeclaredAccessibility) : null,
            Set = property.SetMethod is { IsInitOnly: false } s ? MapAccessibility(s.DeclaredAccessibility) : null,
            Init = property.SetMethod is { IsInitOnly: true } i ? MapAccessibility(i.DeclaredAccessibility) : null,
        };

        ReturnRefKind? refKind = property.RefKind switch
        {
            RefKind.Ref => ReturnRefKind.Ref,
            RefKind.RefReadOnly => ReturnRefKind.RefReadonly,
            _ => null,
        };

        string typeName = SymbolTypeRenderer.Render(property.Type, decode);
        string? location = LocationOf(property);
        IReadOnlyList<string>? attributes = CollectAttributes(property, options);
        IReadOnlyList<MemberModifier>? modifiers =
            isExplicit ? null : NullIfEmpty(MethodModifiers(primary, isInterface));

        if (property.IsIndexer)
        {
            return new IndexerContract
            {
                ExplicitInterface = explicitInterface,
                Access = MapAccessibility(property.DeclaredAccessibility),
                Modifiers = modifiers,
                Type = typeName,
                RefKind = refKind,
                Params = BuildParams(property.Parameters, options) ?? [],
                Accessors = accessors,
                Attributes = attributes,
                SourceLocation = location,
            };
        }

        string name = isExplicit ? property.ExplicitInterfaceImplementations[0].Name : property.Name;
        return new PropertyContract
        {
            Name = name,
            ExplicitInterface = explicitInterface,
            Access = MapAccessibility(property.DeclaredAccessibility),
            Modifiers = modifiers,
            Type = typeName,
            RefKind = refKind,
            Accessors = accessors,
            Attributes = attributes,
            SourceLocation = location,
        };
    }

    private static EventContract ReadEvent(IEventSymbol evt, bool isInterface, ReaderOptions options)
    {
        bool isExplicit = !evt.ExplicitInterfaceImplementations.IsDefaultOrEmpty;
        IMethodSymbol modifierSource = evt.AddMethod ?? evt.RemoveMethod!;
        return new EventContract
        {
            Name = isExplicit ? evt.ExplicitInterfaceImplementations[0].Name : evt.Name,
            ExplicitInterface = isExplicit
                ? SymbolTypeRenderer.Render(evt.ExplicitInterfaceImplementations[0].ContainingType, decodeNullable: false)
                : null,
            Access = MapAccessibility(evt.DeclaredAccessibility),
            Modifiers = isExplicit ? null : NullIfEmpty(MethodModifiers(modifierSource, isInterface)),
            Type = SymbolTypeRenderer.Render(evt.Type, options.DecodeNullableAnnotations),
            Attributes = CollectAttributes(evt, options),
            SourceLocation = LocationOf(evt),
        };
    }

    private static FieldContract? ReadField(IFieldSymbol field, ReaderOptions options)
    {
        if (field.Name.Length == 0 || field.Name[0] == '<' || field.AssociatedSymbol is not null)
            return null;

        var modifiers = new List<MemberModifier>();
        if (field.IsConst)
        {
            modifiers.Add(MemberModifier.Const);
        }
        else
        {
            if (field.IsStatic)
                modifiers.Add(MemberModifier.Static);
            if (field.IsReadOnly)
                modifiers.Add(MemberModifier.Readonly);
        }

        if (field.IsVolatile)
            modifiers.Add(MemberModifier.Volatile);

        return new FieldContract
        {
            Name = field.Name,
            Access = MapAccessibility(field.DeclaredAccessibility),
            Modifiers = NullIfEmpty(modifiers),
            Type = SymbolTypeRenderer.Render(field.Type, options.DecodeNullableAnnotations),
            Value = field.IsConst && field.HasConstantValue ? ConstantValue.Of(field.ConstantValue) : null,
            Attributes = CollectAttributes(field, options),
            SourceLocation = LocationOf(field),
        };
    }

    private static List<MemberContract> ReadEnumMembers(INamedTypeSymbol type, ReaderOptions options)
    {
        var members = new List<MemberContract>();
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
        {
            FieldContract? contract = ReadField(field, options);
            if (contract is not null)
                members.Add(contract);
        }

        return members;
    }

    private static List<ParamContract>? BuildParams(IMethodSymbol method, ReaderOptions options) =>
        BuildParams(method.Parameters, options) is { Count: > 0 } list ? list : null;

    private static List<ParamContract>? BuildParams(
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters, ReaderOptions options)
    {
        if (parameters.IsDefaultOrEmpty)
            return [];

        bool decode = options.DecodeNullableAnnotations;
        var result = new List<ParamContract>(parameters.Length);
        foreach (IParameterSymbol p in parameters)
        {
            ParamModifier? modifier = p.RefKind switch
            {
                RefKind.Ref => ParamModifier.Ref,
                RefKind.Out => ParamModifier.Out,
                RefKind.In => ParamModifier.In,
                RefKind.RefReadOnlyParameter => ParamModifier.RefReadonly,
                _ => p.IsParams ? ParamModifier.Params : null,
            };

            result.Add(new ParamContract
            {
                Type = SymbolTypeRenderer.Render(p.Type, decode),
                Name = p.Name.Length == 0 ? null : p.Name,
                Modifier = modifier,
                Default = MapDefault(p),
            });
        }

        return result;
    }

    /// <summary>Mimics what metadata can represent: struct '= default' compiles to a nullref
    /// constant (read as null); decimal constants decode from DecimalConstantAttribute on
    /// both sides; DateTime constants (interop-only) stay the sentinel.</summary>
    private static ConstantValue? MapDefault(IParameterSymbol p)
    {
        if (!p.HasExplicitDefaultValue)
            return null;

        if (p.Type.SpecialType == SpecialType.System_DateTime)
            return ConstantValue.DefaultSentinel;

        return ConstantValue.Of(p.ExplicitDefaultValue);
    }

    private static List<TypeParamContract> ReadTypeParams(
        System.Collections.Immutable.ImmutableArray<ITypeParameterSymbol> typeParameters, ReaderOptions options)
    {
        bool decode = options.DecodeNullableAnnotations;
        var result = new List<TypeParamContract>(typeParameters.Length);
        foreach (ITypeParameterSymbol tp in typeParameters)
        {
            var constraints = new List<string>();
            if (tp.HasReferenceTypeConstraint)
            {
                constraints.Add(decode && tp.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                    ? "class?"
                    : "class");
            }

            if (tp.HasUnmanagedTypeConstraint)
                constraints.Add("unmanaged");
            else if (tp.HasValueTypeConstraint)
                constraints.Add("struct");

            if (decode && tp.HasNotNullConstraint)
                constraints.Add("notnull");

            foreach (ITypeSymbol constraintType in tp.ConstraintTypes)
                constraints.Add(SymbolTypeRenderer.Render(constraintType, decode));

            if (tp.HasConstructorConstraint && !tp.HasValueTypeConstraint && !tp.HasUnmanagedTypeConstraint)
                constraints.Add("new()");

            result.Add(new TypeParamContract
            {
                Name = tp.Name,
                Variance = tp.Variance switch
                {
                    VarianceKind.In => Variance.In,
                    VarianceKind.Out => Variance.Out,
                    _ => null,
                },
                Constraints = constraints.Count == 0 ? null : constraints,
            });
        }

        return result;
    }

    private static List<MemberModifier> MethodModifiers(IMethodSymbol method, bool isInterface)
    {
        var result = new List<MemberModifier>();
        if (method.IsStatic)
            result.Add(MemberModifier.Static);

        // Mirror of the metadata side: static interface members keep their abstract/virtual
        // distinction; instance interface members are implicitly abstract and stay unmarked.
        if (isInterface && method.IsStatic)
        {
            if (method.IsAbstract)
                result.Add(MemberModifier.Abstract);
            else if (method.IsVirtual)
                result.Add(MemberModifier.Virtual);
        }

        if (!isInterface)
        {
            if (method.IsAbstract)
            {
                result.Add(MemberModifier.Abstract);
            }
            else if (method.IsOverride)
            {
                result.Add(MemberModifier.Override);
                if (method.IsSealed)
                    result.Add(MemberModifier.Sealed);
            }
            else if (method.IsVirtual)
            {
                result.Add(MemberModifier.Virtual);
            }
        }

        // Per-member IsReadOnlyAttribute is only emitted when the member is individually
        // marked; in a readonly struct the type-level flag covers everything and metadata
        // shows no member modifier. Mimic that.
        if (method.IsReadOnly && method.ContainingType is not { IsValueType: true, IsReadOnly: true })
            result.Add(MemberModifier.Readonly);

        return result;
    }

    private static List<TypeModifier> TypeModifiers(INamedTypeSymbol type, TypeKind kind)
    {
        var result = new List<TypeModifier>();
        switch (kind)
        {
            case TypeKind.Class or TypeKind.Record:
                if (type.IsStatic)
                {
                    result.Add(TypeModifier.Static);
                }
                else
                {
                    if (type.IsAbstract)
                        result.Add(TypeModifier.Abstract);
                    if (type.IsSealed)
                        result.Add(TypeModifier.Sealed);
                }

                break;
            case TypeKind.Struct:
                if (type.IsReadOnly)
                    result.Add(TypeModifier.Readonly);
                if (type.IsRefLikeType)
                    result.Add(TypeModifier.Ref);
                break;
        }

        return result;
    }

    /// <summary>Nested visibility clamps to the declaring chain, same as the metadata side.</summary>
    private static Accessibility EffectiveAccessibility(INamedTypeSymbol type)
    {
        Accessibility access = MapAccessibility(type.DeclaredAccessibility);
        INamedTypeSymbol? containing = type.ContainingType;
        while (containing is not null)
        {
            Accessibility parent = MapAccessibility(containing.DeclaredAccessibility);
            if (Rank(parent) < Rank(access))
                access = parent;
            containing = containing.ContainingType;
        }

        return access;
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

    private static Accessibility MapAccessibility(Microsoft.CodeAnalysis.Accessibility accessibility) =>
        accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => Accessibility.Public,
            Microsoft.CodeAnalysis.Accessibility.Protected => Accessibility.Protected,
            Microsoft.CodeAnalysis.Accessibility.Internal => Accessibility.Internal,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Accessibility.ProtectedInternal,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Accessibility.PrivateProtected,
            _ => Accessibility.Private,
        };

    private static IReadOnlyList<string>? CollectAttributes(ISymbol symbol, ReaderOptions options)
    {
        if (!options.CollectAttributes)
            return null;

        var names = new List<string>();
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass)
            {
                var sb = new StringBuilder();
                SymbolTypeRenderer.AppendFullName(sb, attributeClass);
                names.Add(sb.ToString());
            }
        }

        return NullIfEmpty(names);
    }

    private static string? LocationOf(ISymbol symbol)
    {
        foreach (Location location in symbol.Locations)
        {
            if (!location.IsInSource)
                continue;

            FileLinePositionSpan span = location.GetLineSpan();
            return $"{span.Path}({span.StartLinePosition.Line + 1})";
        }

        return null;
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;
}
