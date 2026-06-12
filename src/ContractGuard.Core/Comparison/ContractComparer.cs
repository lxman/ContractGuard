using ContractGuard.Core.Metadata;
using ContractGuard.Core.Model;
using ContractGuard.Core.TypeNames;

namespace ContractGuard.Core.Comparison;

/// <summary>
/// Compares a contract against an observed assembly surface and produces the gate verdict.
/// </summary>
public sealed class ContractComparer
{
    private readonly AssemblyContract _contract;
    private readonly AssemblySurface _surface;
    private readonly ContractSettings _settings;
    private readonly TypeNameMatcher _matcher;
    private readonly List<Diagnostic> _diagnostics = [];

    private ContractComparer(AssemblyContract contract, AssemblySurface surface)
    {
        _contract = contract;
        _surface = surface;
        _settings = contract.Settings;
        _matcher = new TypeNameMatcher(
            contract.Usings ?? [], _settings.NullableAnnotations, _settings.TupleElementNames);
    }

    public static ComparisonResult Compare(AssemblyContract contract, AssemblySurface surface) =>
        new ContractComparer(contract, surface).Run();

    private ComparisonResult Run()
    {
        if (!string.Equals(_contract.Assembly, _surface.Name, StringComparison.Ordinal))
        {
            Add(DiagnosticIds.AssemblyNameMismatch, DiagnosticSeverity.Error,
                $"Contract governs assembly '{_contract.Assembly}' but the scanned assembly is '{_surface.Name}'.");
            return new ComparisonResult(_diagnostics);
        }

        var governedObserved = new HashSet<TypeContract>();
        foreach (TypeContract governed in _contract.Types)
        {
            TypeContract? observed = FindObservedType(governed);
            if (observed is null)
            {
                Add(DiagnosticIds.TypeMissing, DiagnosticSeverity.Error,
                    $"Governed type '{governed.Type}' was not found in the assembly.", governed.Type);
                continue;
            }

            governedObserved.Add(observed);
            CompareType(governed, observed);
        }

        if (_settings.NewTypes != AllowDeny.Deny) return new ComparisonResult(_diagnostics);
        foreach (TypeContract observed in _surface.Types)
        {
            if (governedObserved.Contains(observed))
                continue;
            if (!ScopeFilter.InScope(observed.Access ?? Accessibility.Public, _settings.Scope))
                continue;

            Add(DiagnosticIds.UnexpectedType, DiagnosticSeverity.Error,
                $"Type '{observed.Type}' is not part of the contract and settings.newTypes is 'deny'.",
                observed.Type);
        }

        return new ComparisonResult(_diagnostics);
    }

    private TypeContract? FindObservedType(TypeContract governed)
    {
        (string name, int arity) = ParseGovernedTypeName(governed);
        return _surface.Types.FirstOrDefault(observed => string.Equals(observed.Type, name, StringComparison.Ordinal) && (observed.TypeParams?.Count ?? 0) == arity);
    }

    private static (string Name, int Arity) ParseGovernedTypeName(TypeContract governed)
    {
        int declaredArity = governed.TypeParams?.Count ?? 0;
        try
        {
            if (TypeNameParser.Parse(governed.Type) is TypeNameNode.Named named)
                return (named.Name, Math.Max(named.Args.Count, declaredArity));
        }
        catch (FormatException)
        {
        }

        return (governed.Type, declaredArity);
    }

    private void CompareType(TypeContract governed, TypeContract observed)
    {
        string typeName = observed.Type;
        string ns = NamespaceOf(typeName);

        Accessibility expectedAccess = governed.Access ?? Accessibility.Public;
        if (expectedAccess != (observed.Access ?? Accessibility.Public))
        {
            Add(DiagnosticIds.AccessMismatch, DiagnosticSeverity.Error,
                $"Type accessibility is '{Wire(observed.Access)}' but the contract prescribes '{Wire(expectedAccess)}'.",
                typeName);
        }

        if (governed.Kind is { } governedKind
            && NormalizeKind(governedKind) != NormalizeKind(observed.Kind ?? TypeKind.Class))
        {
            Add(DiagnosticIds.TypeKindMismatch, DiagnosticSeverity.Error,
                $"Type kind is '{observed.Kind}' but the contract prescribes '{governedKind}'.", typeName);
        }

        if (governed.Modifiers is not null
            && !SetEquals(governed.Modifiers, observed.Modifiers ?? []))
        {
            Add(DiagnosticIds.TypeModifiersMismatch, DiagnosticSeverity.Error,
                $"Type modifiers [{Join(observed.Modifiers)}] do not match prescribed [{Join(governed.Modifiers)}].",
                typeName);
        }

        if (governed.Extends is not null
            && (observed.Extends is null || !_matcher.Matches(governed.Extends, observed.Extends, ns)))
        {
            Add(DiagnosticIds.BaseTypeMismatch, DiagnosticSeverity.Error,
                $"Base type is '{observed.Extends ?? "object"}' but the contract prescribes '{governed.Extends}'.",
                typeName);
        }

        foreach (string iface in governed.Implements ?? [])
        {
            if (!(observed.Implements ?? []).Any(o => _matcher.Matches(iface, o, ns)))
            {
                Add(DiagnosticIds.InterfaceMissing, DiagnosticSeverity.Error,
                    $"Type does not implement prescribed interface '{iface}'.", typeName);
            }
        }

        CompareTypeParams(governed.TypeParams, observed.TypeParams, ns, typeName, member: null);

        if (governed.UnderlyingType is not null
            && (observed.UnderlyingType is null
                || !_matcher.Matches(governed.UnderlyingType, observed.UnderlyingType, ns)))
        {
            Add(DiagnosticIds.UnderlyingTypeMismatch, DiagnosticSeverity.Error,
                $"Enum underlying type is '{observed.UnderlyingType}' but the contract prescribes '{governed.UnderlyingType}'.",
                typeName);
        }

        if (governed.Kind == TypeKind.Delegate)
            CompareDelegate(governed, observed, ns, typeName);

        CompareMembers(governed, observed, ns, typeName);
    }

    private void CompareDelegate(TypeContract governed, TypeContract observed, string ns, string typeName)
    {
        if (governed.Returns is not null
            && (observed.Returns is null || !_matcher.Matches(governed.Returns, observed.Returns, ns)))
        {
            Add(DiagnosticIds.DelegateSignatureMismatch, DiagnosticSeverity.Error,
                $"Delegate returns '{observed.Returns}' but the contract prescribes '{governed.Returns}'.", typeName);
        }

        if (governed.Params is null) return;
        if (!ParamTypesMatch(governed.Params, observed.Params ?? [], ns))
        {
            Add(DiagnosticIds.DelegateSignatureMismatch, DiagnosticSeverity.Error,
                "Delegate parameter list does not match the contract.", typeName);
        }
        else
        {
            CompareParamAspects(
                governed.Params, observed.Params ?? [],
                ResolveNames(null, governed), typeName, "Invoke");
        }
    }

    private void CompareMembers(TypeContract governed, TypeContract observed, string ns, string typeName)
    {
        IReadOnlyList<MemberContract> observedMembers = observed.Members ?? [];
        var consumed = new HashSet<MemberContract>();

        foreach (MemberContract entry in governed.Members ?? [])
        {
            MemberContract? match = observedMembers.FirstOrDefault(o => !consumed.Contains(o) && IdentityMatch(entry, o, ns));

            if (entry.Mode == EntryMode.Forbidden)
            {
                if (match is not null)
                {
                    consumed.Add(match);
                    Add(DiagnosticIds.ForbiddenMemberPresent, DiagnosticSeverity.Error,
                        $"Forbidden member is present: {DeclarationRenderer.Render(entry, ShortName(typeName))}.",
                        typeName, entry.DisplayName, entry.Reason);
                }

                continue;
            }

            if (match is null)
            {
                MemberContract? closest = ClosestCandidate(entry, observedMembers, consumed);
                if (closest is not null)
                {
                    consumed.Add(closest);
                    Add(DiagnosticIds.MemberSignatureChanged, DiagnosticSeverity.Error,
                        $"Prescribed: {DeclarationRenderer.Render(entry, ShortName(typeName))}; "
                        + $"found: {DeclarationRenderer.Render(closest, ShortName(typeName))}.",
                        typeName, entry.DisplayName, entry.Reason);
                }
                else
                {
                    Add(DiagnosticIds.MemberMissing, DiagnosticSeverity.Error,
                        $"Missing {entry.KindName}: {DeclarationRenderer.Render(entry, ShortName(typeName))}.",
                        typeName, entry.DisplayName, entry.Reason);
                }

                continue;
            }

            consumed.Add(match);
            CompareMatchedMember(entry, match, governed, ns, typeName);
        }

        if ((governed.NewMembers ?? _settings.NewMembers) != AllowDeny.Deny) return;
        foreach (MemberContract extra in observedMembers)
        {
            if (consumed.Contains(extra))
                continue;
            if (!ScopeFilter.InScope(extra.Access ?? Accessibility.Public, _settings.Scope))
                continue;

            Add(DiagnosticIds.UnexpectedMember, DiagnosticSeverity.Error,
                $"Member is not part of the contract and newMembers is 'deny': "
                + $"{DeclarationRenderer.Render(extra, ShortName(typeName))}.",
                typeName, extra.DisplayName);
        }
    }

    private bool IdentityMatch(MemberContract contract, MemberContract observed, string ns) =>
        (contract, observed) switch
        {
            (MethodContract c, MethodContract o) =>
                c.Name == o.Name
                && (c.TypeParams?.Count ?? 0) == (o.TypeParams?.Count ?? 0)
                && ParamTypesMatch(c.Params ?? [], o.Params ?? [], ns),
            (ConstructorMemberContract c, ConstructorMemberContract o) =>
                ParamTypesMatch(c.Params, o.Params, ns),
            (PropertyContract c, PropertyContract o) => c.Name == o.Name,
            (IndexerContract c, IndexerContract o) => ParamTypesMatch(c.Params, o.Params, ns),
            (EventContract c, EventContract o) => c.Name == o.Name,
            (FieldContract c, FieldContract o) => c.Name == o.Name,
            (OperatorContract c, OperatorContract o) =>
                c.Name == o.Name && ParamTypesMatch(c.Params, o.Params, ns),
            _ => false,
        };

    private static MemberContract? ClosestCandidate(
        MemberContract entry, IReadOnlyList<MemberContract> observedMembers, HashSet<MemberContract> consumed)
    {
        List<MemberContract> candidates = observedMembers
            .Where(o => !consumed.Contains(o) && o.KindName == entry.KindName && SameName(entry, o))
            .ToList();
        if (candidates.Count == 0)
            return null;

        // Prefer the candidate whose parameter count is closest to the prescription.
        int targetCount = ParamCount(entry);
        return candidates.OrderBy(c => Math.Abs(ParamCount(c) - targetCount)).First();

        static bool SameName(MemberContract a, MemberContract b) => (a, b) switch
        {
            (MethodContract x, MethodContract y) => x.Name == y.Name,
            (OperatorContract x, OperatorContract y) => x.Name == y.Name,
            (ConstructorMemberContract, ConstructorMemberContract) => true,
            (IndexerContract, IndexerContract) => true,
            _ => a.DisplayName == b.DisplayName,
        };

        static int ParamCount(MemberContract m) => m switch
        {
            MethodContract x => x.Params?.Count ?? 0,
            ConstructorMemberContract x => x.Params.Count,
            IndexerContract x => x.Params.Count,
            OperatorContract x => x.Params.Count,
            _ => 0,
        };
    }

    private void CompareMatchedMember(
        MemberContract contract, MemberContract observed, TypeContract governedType, string ns, string typeName)
    {
        string member = contract.DisplayName;

        if (contract is not OperatorContract)
        {
            Accessibility expected = contract.Access ?? Accessibility.Public;
            Accessibility actual = observed.Access ?? Accessibility.Public;
            if (expected != actual)
            {
                Add(DiagnosticIds.AccessMismatch, DiagnosticSeverity.Error,
                    $"Accessibility is '{Wire(actual)}' but the contract prescribes '{Wire(expected)}'.",
                    typeName, member, contract.Reason);
            }
        }

        (IReadOnlyList<MemberModifier> contractModifiers, IReadOnlyList<MemberModifier> observedModifiers) = (ModifiersOf(contract), ModifiersOf(observed));
        if (!SetEquals(contractModifiers, observedModifiers))
        {
            Add(DiagnosticIds.ModifiersMismatch, DiagnosticSeverity.Error,
                $"Modifiers [{Join(observedModifiers)}] do not match prescribed [{Join(contractModifiers)}].",
                typeName, member, contract.Reason);
        }

        (string? contractReturn, string? observedReturn) = (ReturnTypeOf(contract), ReturnTypeOf(observed));
        if (contractReturn is not null
            && (observedReturn is null || !_matcher.Matches(contractReturn, observedReturn, ns)))
        {
            Add(DiagnosticIds.ReturnTypeMismatch, DiagnosticSeverity.Error,
                $"Type is '{observedReturn}' but the contract prescribes '{contractReturn}'.",
                typeName, member, contract.Reason);
        }

        if (RefKindOf(contract) != RefKindOf(observed))
        {
            Add(DiagnosticIds.ReturnTypeMismatch, DiagnosticSeverity.Error,
                "Ref return kind does not match the contract.", typeName, member, contract.Reason);
        }

        if (contract is MethodContract cm && observed is MethodContract om)
            CompareTypeParams(cm.TypeParams, om.TypeParams, ns, typeName, member);

        IReadOnlyList<ParamContract>? contractParams = ParamsOf(contract);
        IReadOnlyList<ParamContract>? observedParams = ParamsOf(observed);
        if (contractParams is not null && observedParams is not null)
        {
            CompareParamAspects(
                contractParams, observedParams,
                ResolveNames(contract, governedType), typeName, member, contract.Reason);
        }

        switch (contract)
        {
            case PropertyContract { Accessors: { } pa } when observed is PropertyContract opc:
                CompareAccessors(pa, opc.Accessors, typeName, member, contract.Reason);
                break;
            case IndexerContract { Accessors: { } ia } when observed is IndexerContract oic:
                CompareAccessors(ia, oic.Accessors, typeName, member, contract.Reason);
                break;
            case FieldContract { Value: { } prescribedValue } when observed is FieldContract of
                                                                   && !prescribedValue.Equals(of.Value):
                Add(DiagnosticIds.ConstValueChanged, DiagnosticSeverity.Error,
                    $"Constant value is {of.Value?.ToString() ?? "absent"} but the contract prescribes {prescribedValue}.",
                    typeName, member, contract.Reason);
                break;
        }
    }

    private void CompareTypeParams(
        IReadOnlyList<TypeParamContract>? contract, IReadOnlyList<TypeParamContract>? observed,
        string ns, string typeName, string? member)
    {
        if (contract is null)
            return;

        string id = member is null ? DiagnosticIds.TypeParamsMismatch : DiagnosticIds.TypeParamsChanged;
        IReadOnlyList<TypeParamContract> observedList = observed ?? [];
        if (contract.Count != observedList.Count)
        {
            Add(id, DiagnosticSeverity.Error,
                $"Generic parameter count is {observedList.Count} but the contract prescribes {contract.Count}.",
                typeName, member);
            return;
        }

        for (var i = 0; i < contract.Count; i++)
        {
            TypeParamContract c = contract[i];
            TypeParamContract o = observedList[i];
            if (c.Variance != o.Variance)
            {
                Add(id, DiagnosticSeverity.Error,
                    $"Variance of '{c.Name}' is '{o.Variance?.ToString().ToLowerInvariant() ?? "none"}' "
                    + $"but the contract prescribes '{c.Variance?.ToString().ToLowerInvariant() ?? "none"}'.",
                    typeName, member);
            }

            if (c.Constraints is null)
                continue;

            IReadOnlyList<string> observedConstraints = o.Constraints ?? [];
            bool matched = c.Constraints.Count == observedConstraints.Count
                           && c.Constraints.All(cc => observedConstraints.Any(oc => ConstraintMatches(cc, oc, ns)));
            if (!matched)
            {
                Add(id, DiagnosticSeverity.Error,
                    $"Constraints on '{c.Name}' [{string.Join(", ", observedConstraints)}] do not match "
                    + $"prescribed [{string.Join(", ", c.Constraints)}].",
                    typeName, member);
            }
        }
    }

    private bool ConstraintMatches(string contract, string observed, string ns) =>
        contract is "class" or "class?" or "struct" or "notnull" or "unmanaged" or "new()"
            ? string.Equals(contract, observed, StringComparison.Ordinal)
            : _matcher.Matches(contract, observed, ns);

    private bool ParamTypesMatch(
        IReadOnlyList<ParamContract> contract, IReadOnlyList<ParamContract> observed, string ns)
    {
        if (contract.Count != observed.Count)
            return false;

        return !contract.Where((t, i) => !_matcher.Matches(t.Type, observed[i].Type, ns)).Any();
    }

    private void CompareParamAspects(
        IReadOnlyList<ParamContract> contract, IReadOnlyList<ParamContract> observed,
        Significance names, string typeName, string? member, string? reason = null)
    {
        for (var i = 0; i < Math.Min(contract.Count, observed.Count); i++)
        {
            ParamContract c = contract[i];
            ParamContract o = observed[i];

            if (c.Modifier != o.Modifier)
            {
                Add(DiagnosticIds.ParameterModifiersChanged, DiagnosticSeverity.Error,
                    $"Parameter {i + 1} modifier is '{WireModifier(o.Modifier)}' but the contract prescribes "
                    + $"'{WireModifier(c.Modifier)}'.",
                    typeName, member, reason);
            }

            if (names == Significance.Significant
                && c.Name is not null && o.Name is not null
                && !string.Equals(c.Name, o.Name, StringComparison.Ordinal))
            {
                Add(DiagnosticIds.ParameterNamesChanged, DiagnosticSeverity.Error,
                    $"Parameter {i + 1} is named '{o.Name}' but the contract prescribes '{c.Name}'.",
                    typeName, member, reason);
            }

            if (_settings.DefaultValues == Significance.Significant && !DefaultsMatch(c, o))
            {
                Add(DiagnosticIds.ParameterDefaultsChanged, DiagnosticSeverity.Error,
                    $"Parameter {i + 1} default is {o.Default?.ToString() ?? "absent"} but the contract "
                    + $"prescribes {c.Default?.ToString() ?? "absent"}.",
                    typeName, member, reason);
            }
        }
    }

    /// <summary>
    /// A contract default written as "EnumType.Member" resolves against the enum's members
    /// when the enum is defined in the scanned assembly; cross-assembly enums still need
    /// the underlying numeric value.
    /// </summary>
    private bool DefaultsMatch(ParamContract contract, ParamContract observed)
    {
        if (Equals(contract.Default, observed.Default))
            return true;

        if (contract.Default?.Value is not string written || !written.Contains('.')
            || observed.Default?.Value is not long observedValue)
        {
            return false;
        }

        var lastDot = written.LastIndexOf('.');
        var memberName = written[(lastDot + 1)..];
        TypeContract? enumType = _surface.Types.FirstOrDefault(t =>
            t.Kind == TypeKind.Enum && t.Type == observed.Type);
        if (enumType is null)
            return false;

        FieldContract? enumMember = enumType.Members?.OfType<FieldContract>()
            .FirstOrDefault(f => f.Name == memberName);
        return enumMember?.Value is { } value && value.Equals(ConstantValue.Of(observedValue));
    }

    private void CompareAccessors(
        AccessorsContract contract, AccessorsContract? observed, string typeName, string member, string? reason)
    {
        AccessorsContract actual = observed ?? new AccessorsContract();
        Check(contract.Get, actual.Get, "get");
        Check(contract.Set, actual.Set, "set");
        Check(contract.Init, actual.Init, "init");
        return;

        void Check(Accessibility? prescribed, Accessibility? found, string keyword)
        {
            if (prescribed == found)
                return;

            string message = (prescribed, found) switch
            {
                (null, not null) => $"Has a '{keyword}' accessor the contract does not prescribe.",
                (not null, null) => $"Missing prescribed '{keyword}' accessor.",
                _ => $"'{keyword}' accessor is '{Wire(found)}' but the contract prescribes '{Wire(prescribed)}'.",
            };

            Add(DiagnosticIds.AccessorsMismatch, DiagnosticSeverity.Error, message, typeName, member, reason);
        }
    }

    private Significance ResolveNames(MemberContract? member, TypeContract type)
    {
        Significance? memberSetting = member switch
        {
            MethodContract m => m.ParameterNames,
            ConstructorMemberContract c => c.ParameterNames,
            IndexerContract i => i.ParameterNames,
            OperatorContract o => o.ParameterNames,
            _ => null,
        };

        return memberSetting ?? type.ParameterNames ?? _settings.ParameterNames;
    }

    private static IReadOnlyList<MemberModifier> ModifiersOf(MemberContract m) => m switch
    {
        MethodContract x => x.Modifiers ?? [],
        PropertyContract x => x.Modifiers ?? [],
        IndexerContract x => x.Modifiers ?? [],
        EventContract x => x.Modifiers ?? [],
        FieldContract x => x.Modifiers ?? [],
        _ => [],
    };

    private static string? ReturnTypeOf(MemberContract m) => m switch
    {
        MethodContract x => x.Returns,
        PropertyContract x => x.Type,
        IndexerContract x => x.Type,
        EventContract x => x.Type,
        FieldContract x => x.Type,
        OperatorContract x => x.Returns,
        _ => null,
    };

    private static ReturnRefKind? RefKindOf(MemberContract m) => m switch
    {
        MethodContract x => x.RefKind,
        PropertyContract x => x.RefKind,
        IndexerContract x => x.RefKind,
        _ => null,
    };

    private static IReadOnlyList<ParamContract>? ParamsOf(MemberContract m) => m switch
    {
        MethodContract x => x.Params ?? [],
        ConstructorMemberContract x => x.Params,
        IndexerContract x => x.Params,
        OperatorContract x => x.Params,
        _ => null,
    };

    private static TypeKind NormalizeKind(TypeKind kind) => kind switch
    {
        // Record classes are detected from metadata (EqualityContract pattern) and compare
        // exactly; record structs have no metadata marker, so they normalize to struct.
        TypeKind.RecordStruct => TypeKind.Struct,
        var k => k,
    };

    private static string NamespaceOf(string fullTypeName)
    {
        string outer = fullTypeName.Split('+')[0];
        int dot = outer.LastIndexOf('.');
        return dot < 0 ? string.Empty : outer[..dot];
    }

    private static string ShortName(string fullTypeName)
    {
        int lastPlus = fullTypeName.LastIndexOf('+');
        string tail = lastPlus >= 0 ? fullTypeName[(lastPlus + 1)..] : fullTypeName;
        int dot = tail.LastIndexOf('.');
        return dot < 0 ? tail : tail[(dot + 1)..];
    }

    private static bool SetEquals<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) =>
        a.Count == b.Count && a.All(b.Contains);

    private static string Join<T>(IReadOnlyList<T>? items) =>
        string.Join(", ", (items ?? []).Select(i => i?.ToString()?.ToLowerInvariant()));

    private static string Wire(Accessibility? access) =>
        Serialization.EnumMaps.Accessibility.NameOf(access ?? Accessibility.Public);

    private static string WireModifier(ParamModifier? modifier) =>
        modifier is { } m ? Serialization.EnumMaps.ParamModifier.NameOf(m) : "none";

    private void Add(
        string id, DiagnosticSeverity severity, string message,
        string? typeName = null, string? member = null, string? reason = null) =>
        _diagnostics.Add(new Diagnostic(id, severity, message, typeName, member, reason));
}
