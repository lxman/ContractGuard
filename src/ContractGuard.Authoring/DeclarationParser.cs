using ContractGuard.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Accessibility = ContractGuard.Core.Model.Accessibility;

namespace ContractGuard.Authoring;

/// <summary>
/// Decomposes a C# member declaration string into contract elements. Syntax-only - type
/// names stay as written and resolve at verification time via the contract's usings, which
/// is exactly the resolution path hand-authored contracts already use.
/// </summary>
public static class DeclarationParser
{
    public static MemberContract ParseMember(string declaration)
    {
        // Declarations are written without bodies, and usually without the trailing
        // semicolon Roslyn wants on body-less members - retry with one appended.
        MemberDeclarationSyntax? syntax = TryParse(declaration) ?? TryParse(declaration + ";");
        if (syntax is not null) return Map(syntax);
        MemberDeclarationSyntax? attempt = SyntaxFactory.ParseMemberDeclaration(declaration + ";");
        string? detail = attempt?.GetDiagnostics()
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.GetMessage();
        throw new FormatException(detail is null
            ? $"Not a parseable C# member declaration: '{declaration}'."
            : $"'{declaration}': {detail}");

    }

    private static MemberDeclarationSyntax? TryParse(string text)
    {
        MemberDeclarationSyntax? syntax = SyntaxFactory.ParseMemberDeclaration(text);
        if (syntax is null)
            return null;

        return syntax.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error) ? null : syntax;
    }

    internal static MemberContract Map(MemberDeclarationSyntax syntax) => syntax switch
    {
        MethodDeclarationSyntax m => MapMethod(m),
        ConstructorDeclarationSyntax c => MapConstructor(c),
        PropertyDeclarationSyntax p => MapProperty(p),
        IndexerDeclarationSyntax i => MapIndexer(i),
        EventFieldDeclarationSyntax e => MapEventField(e),
        EventDeclarationSyntax e => MapEvent(e),
        FieldDeclarationSyntax f => MapField(f).Single(),
        OperatorDeclarationSyntax o => MapOperator(o),
        ConversionOperatorDeclarationSyntax c => MapConversion(c),
        _ => throw new FormatException($"'{syntax.Kind()}' is not a member kind a contract can govern."),
    };

    private static MethodContract MapMethod(MethodDeclarationSyntax m)
    {
        (string returns, ReturnRefKind? refKind) = SplitRef(m.ReturnType);
        return new MethodContract
        {
            Name = m.Identifier.Text,
            ExplicitInterface = ExplicitInterfaceName(m.ExplicitInterfaceSpecifier),
            Access = MapAccess(m.Modifiers),
            Modifiers = MapMemberModifiers(m.Modifiers),
            Returns = returns,
            RefKind = refKind,
            TypeParams = MapTypeParams(m.TypeParameterList, m.ConstraintClauses),
            Params = MapParams(m.ParameterList.Parameters),
            Mode = EntryMode.Required,
        };
    }

    private static string? ExplicitInterfaceName(ExplicitInterfaceSpecifierSyntax? specifier) =>
        specifier?.Name.NormalizeWhitespace().ToString();

    private static ConstructorMemberContract MapConstructor(ConstructorDeclarationSyntax c)
    {
        if (c.Modifiers.Any(SyntaxKind.StaticKeyword))
            throw new FormatException("Static constructors are not a contract concept.");

        return new ConstructorMemberContract
        {
            Access = MapAccess(c.Modifiers),
            Params = MapParams(c.ParameterList.Parameters) ?? [],
        };
    }

    private static PropertyContract MapProperty(PropertyDeclarationSyntax p)
    {
        Accessibility? access = MapAccess(p.Modifiers);
        (string type, ReturnRefKind? refKind) = SplitRef(p.Type);
        return new PropertyContract
        {
            Name = p.Identifier.Text,
            ExplicitInterface = ExplicitInterfaceName(p.ExplicitInterfaceSpecifier),
            Access = access,
            Modifiers = MapMemberModifiers(p.Modifiers),
            Type = type,
            RefKind = refKind,
            Accessors = MapAccessors(p.AccessorList, p.ExpressionBody, access),
        };
    }

    private static IndexerContract MapIndexer(IndexerDeclarationSyntax i)
    {
        Accessibility? access = MapAccess(i.Modifiers);
        (string type, ReturnRefKind? refKind) = SplitRef(i.Type);
        return new IndexerContract
        {
            ExplicitInterface = ExplicitInterfaceName(i.ExplicitInterfaceSpecifier),
            Access = access,
            Modifiers = MapMemberModifiers(i.Modifiers),
            Type = type,
            RefKind = refKind,
            Params = MapParams(i.ParameterList.Parameters) ?? [],
            Accessors = MapAccessors(i.AccessorList, i.ExpressionBody, access),
        };
    }

    private static EventContract MapEventField(EventFieldDeclarationSyntax e)
    {
        VariableDeclaratorSyntax declarator = e.Declaration.Variables.Single();
        return new EventContract
        {
            Name = declarator.Identifier.Text,
            Access = MapAccess(e.Modifiers),
            Modifiers = MapMemberModifiers(e.Modifiers),
            Type = Canonical(e.Declaration.Type),
        };
    }

    private static EventContract MapEvent(EventDeclarationSyntax e) => new()
    {
        Name = e.Identifier.Text,
        ExplicitInterface = ExplicitInterfaceName(e.ExplicitInterfaceSpecifier),
        Access = MapAccess(e.Modifiers),
        Modifiers = MapMemberModifiers(e.Modifiers),
        Type = Canonical(e.Type),
    };

    internal static IEnumerable<FieldContract> MapField(FieldDeclarationSyntax f)
    {
        Accessibility? access = MapAccess(f.Modifiers);
        IReadOnlyList<MemberModifier>? modifiers = MapMemberModifiers(f.Modifiers);
        string type = Canonical(f.Declaration.Type);
        foreach (VariableDeclaratorSyntax declarator in f.Declaration.Variables)
        {
            yield return new FieldContract
            {
                Name = declarator.Identifier.Text,
                Access = access,
                Modifiers = modifiers,
                Type = type,
                Value = declarator.Initializer is { } init ? MapConstant(init.Value) : null,
            };
        }
    }

    private static OperatorContract MapOperator(OperatorDeclarationSyntax o) => new()
    {
        Name = o.OperatorToken.Text,
        Returns = Canonical(o.ReturnType),
        Params = MapParams(o.ParameterList.Parameters) ?? [],
    };

    private static OperatorContract MapConversion(ConversionOperatorDeclarationSyntax c) => new()
    {
        Name = c.ImplicitOrExplicitKeyword.Text,
        Returns = Canonical(c.Type),
        Params = MapParams(c.ParameterList.Parameters) ?? [],
    };

    private static AccessorsContract? MapAccessors(
        AccessorListSyntax? accessorList, ArrowExpressionClauseSyntax? expressionBody, Accessibility? memberAccess)
    {
        Accessibility owner = memberAccess ?? Accessibility.Public;
        if (expressionBody is not null)
            return new AccessorsContract { Get = owner };

        if (accessorList is null)
            return null;

        Accessibility? get = null;
        Accessibility? set = null;
        Accessibility? init = null;
        foreach (AccessorDeclarationSyntax accessor in accessorList.Accessors)
        {
            Accessibility access = MapAccess(accessor.Modifiers) ?? owner;
            switch (accessor.Kind())
            {
                case SyntaxKind.GetAccessorDeclaration:
                    get = access;
                    break;
                case SyntaxKind.SetAccessorDeclaration:
                    set = access;
                    break;
                case SyntaxKind.InitAccessorDeclaration:
                    init = access;
                    break;
                default:
                    throw new FormatException($"Accessor '{accessor.Kind()}' is not supported.");
            }
        }

        return new AccessorsContract { Get = get, Set = set, Init = init };
    }

    private static IReadOnlyList<TypeParamContract>? MapTypeParams(
        TypeParameterListSyntax? typeParams, SyntaxList<TypeParameterConstraintClauseSyntax> clauses)
    {
        if (typeParams is null || typeParams.Parameters.Count == 0)
            return null;

        var result = new List<TypeParamContract>();
        foreach (TypeParameterSyntax tp in typeParams.Parameters)
        {
            var constraints = new List<string>();
            TypeParameterConstraintClauseSyntax? clause = clauses.FirstOrDefault(c => c.Name.Identifier.Text == tp.Identifier.Text);
            foreach (TypeParameterConstraintSyntax constraint in clause?.Constraints ?? default)
            {
                constraints.Add(constraint switch
                {
                    ClassOrStructConstraintSyntax cs => cs.ClassOrStructKeyword.Text
                        + (cs.QuestionToken.IsKind(SyntaxKind.QuestionToken) ? "?" : string.Empty),
                    ConstructorConstraintSyntax => "new()",
                    TypeConstraintSyntax tc => Canonical(tc.Type),
                    _ => throw new FormatException($"Constraint '{constraint}' is not supported."),
                });
            }

            result.Add(new TypeParamContract
            {
                Name = tp.Identifier.Text,
                Variance = tp.VarianceKeyword.Kind() switch
                {
                    SyntaxKind.InKeyword => Variance.In,
                    SyntaxKind.OutKeyword => Variance.Out,
                    _ => null,
                },
                Constraints = constraints.Count == 0 ? null : constraints,
            });
        }

        return result;
    }

    private static IReadOnlyList<ParamContract>? MapParams(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        if (parameters.Count == 0)
            return null;

        var result = new List<ParamContract>(parameters.Count);
        foreach (ParameterSyntax p in parameters)
        {
            ParamModifier? modifier = null;
            var hasRef = false;
            var hasReadonly = false;
            foreach (SyntaxToken token in p.Modifiers)
            {
                switch (token.Kind())
                {
                    case SyntaxKind.RefKeyword:
                        hasRef = true;
                        break;
                    case SyntaxKind.ReadOnlyKeyword:
                        hasReadonly = true;
                        break;
                    case SyntaxKind.OutKeyword:
                        modifier = ParamModifier.Out;
                        break;
                    case SyntaxKind.InKeyword:
                        modifier = ParamModifier.In;
                        break;
                    case SyntaxKind.ParamsKeyword:
                        modifier = ParamModifier.Params;
                        break;
                    case SyntaxKind.ThisKeyword:
                        modifier = ParamModifier.This;
                        break;
                    default:
                        throw new FormatException($"Parameter modifier '{token.Text}' is not supported.");
                }
            }

            if (hasRef)
                modifier = hasReadonly ? ParamModifier.RefReadonly : ParamModifier.Ref;

            result.Add(new ParamContract
            {
                Type = Canonical(p.Type ?? throw new FormatException($"Parameter '{p.Identifier.Text}' needs a type.")),
                Name = p.Identifier.Text.Length == 0 ? null : p.Identifier.Text,
                Modifier = modifier,
                Default = p.Default is { } d ? MapConstant(d.Value) : null,
            });
        }

        return result;
    }

    internal static ConstantValue MapConstant(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression } =>
            ConstantValue.DefaultSentinel,
        DefaultExpressionSyntax => ConstantValue.DefaultSentinel,
        LiteralExpressionSyntax literal => ConstantValue.Of(literal.Token.Value),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax operand } =>
            Negate(operand.Token.Value),
        // "EnumType.Member" stores as written; the comparer resolves it against enums
        // defined in the scanned assembly.
        MemberAccessExpressionSyntax memberAccess => ConstantValue.Of(memberAccess.ToString()),
        _ => throw new FormatException($"'{expression}' is not a representable constant."),
    };

    private static ConstantValue Negate(object? value) => value switch
    {
        sbyte or short or int or long => ConstantValue.Of(-Convert.ToInt64(value)),
        float or double => ConstantValue.Of(-Convert.ToDouble(value)),
        decimal m => ConstantValue.Of(-m),
        _ => throw new FormatException($"Cannot negate constant '{value}'."),
    };

    private static Accessibility? MapAccess(SyntaxTokenList modifiers)
    {
        bool hasPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        bool hasProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool hasInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        bool hasPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);

        if (hasPublic)
            return Accessibility.Public;
        if (hasProtected && hasInternal)
            return Accessibility.ProtectedInternal;
        if (hasPrivate && hasProtected)
            return Accessibility.PrivateProtected;
        if (hasProtected)
            return Accessibility.Protected;
        if (hasInternal)
            return Accessibility.Internal;
        if (hasPrivate)
            return Accessibility.Private;
        return null;
    }

    private static IReadOnlyList<MemberModifier>? MapMemberModifiers(SyntaxTokenList modifiers)
    {
        var result = new List<MemberModifier>();
        foreach (SyntaxToken token in modifiers)
        {
            switch (token.Kind())
            {
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.InternalKeyword:
                case SyntaxKind.PrivateKeyword:
                    break; // accessibility, handled separately
                case SyntaxKind.StaticKeyword:
                    result.Add(MemberModifier.Static);
                    break;
                case SyntaxKind.AbstractKeyword:
                    result.Add(MemberModifier.Abstract);
                    break;
                case SyntaxKind.VirtualKeyword:
                    result.Add(MemberModifier.Virtual);
                    break;
                case SyntaxKind.SealedKeyword:
                    result.Add(MemberModifier.Sealed);
                    break;
                case SyntaxKind.OverrideKeyword:
                    result.Add(MemberModifier.Override);
                    break;
                case SyntaxKind.ReadOnlyKeyword:
                    result.Add(MemberModifier.Readonly);
                    break;
                case SyntaxKind.ConstKeyword:
                    result.Add(MemberModifier.Const);
                    break;
                case SyntaxKind.VolatileKeyword:
                    result.Add(MemberModifier.Volatile);
                    break;
                case SyntaxKind.AsyncKeyword:
                    throw new FormatException(
                        "'async' is an implementation detail, not part of the binary signature - "
                        + "prescribe the return type and let implementers choose.");
                case SyntaxKind.PartialKeyword:
                    break; // source-level concept, ignored
                default:
                    throw new FormatException($"Modifier '{token.Text}' is not a contract concept.");
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static (string Type, ReturnRefKind? RefKind) SplitRef(TypeSyntax type)
    {
        if (type is not RefTypeSyntax refType) return (Canonical(type), null);
        ReturnRefKind kind = refType.ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword)
            ? ReturnRefKind.RefReadonly
            : ReturnRefKind.Ref;
        return (Canonical(refType.Type), kind);

    }

    /// <summary>Type names as written, with canonical spacing.</summary>
    internal static string Canonical(TypeSyntax type) => type.NormalizeWhitespace().ToString();
}
