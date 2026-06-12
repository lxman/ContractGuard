using ContractGuard.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Accessibility = ContractGuard.Core.Model.Accessibility;
using TypeKind = ContractGuard.Core.Model.TypeKind;

namespace ContractGuard.Authoring;

public sealed record ImportResult(IReadOnlyList<string> Usings, IReadOnlyList<TypeContract> Types);

/// <summary>
/// Decomposes a C# scaffold file (an interface control document, a skeleton the architect
/// wrote) into contract types. Syntax-only, like everything in authoring: type names stay
/// as written, the file's usings carry over to the contract for resolution at verify time.
/// </summary>
public static class ScaffoldImporter
{
    public static ImportResult Import(string source)
    {
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))
            .GetCompilationUnitRoot();

        List<Diagnostic> errors = root.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count > 0)
            throw new FormatException($"Scaffold does not parse: {errors[0].GetMessage()}");

        List<string> usings = root.Usings
            .Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None))
            .Select(u => u.Name?.ToString())
            .OfType<string>()
            .ToList();

        var types = new List<TypeContract>();
        CollectTypes(root.Members, namespacePrefix: string.Empty, declaringPrefix: string.Empty, types);
        return new ImportResult(usings, types);
    }

    private static void CollectTypes(
        IEnumerable<MemberDeclarationSyntax> members, string namespacePrefix, string declaringPrefix,
        List<TypeContract> types)
    {
        foreach (MemberDeclarationSyntax member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    string prefix = namespacePrefix.Length == 0 ? ns.Name.ToString() : $"{namespacePrefix}.{ns.Name}";
                    CollectTypes(ns.Members, prefix, declaringPrefix, types);
                    break;
                case TypeDeclarationSyntax type:
                    types.Add(MapType(type, namespacePrefix, declaringPrefix, types));
                    break;
                case EnumDeclarationSyntax e:
                    types.Add(MapEnum(e, namespacePrefix, declaringPrefix));
                    break;
                case DelegateDeclarationSyntax d:
                    types.Add(MapDelegate(d, namespacePrefix, declaringPrefix));
                    break;
            }
        }
    }

    private static TypeContract MapType(
        TypeDeclarationSyntax type, string namespacePrefix, string declaringPrefix, List<TypeContract> types)
    {
        string fullName = FullName(namespacePrefix, declaringPrefix, type.Identifier.Text);
        TypeKind kind = type switch
        {
            InterfaceDeclarationSyntax => TypeKind.Interface,
            StructDeclarationSyntax => TypeKind.Struct,
            RecordDeclarationSyntax r when r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) =>
                TypeKind.RecordStruct,
            RecordDeclarationSyntax => TypeKind.Record,
            _ => TypeKind.Class,
        };

        (string? extends, List<string> implements) = MapBaseList(type.BaseList, kind);

        var members = new List<MemberContract>();
        foreach (MemberDeclarationSyntax member in type.Members)
        {
            switch (member)
            {
                case TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax:
                    // Nested types become their own governed entries.
                    CollectTypes([member], namespacePrefix, NestPrefix(declaringPrefix, type.Identifier.Text), types);
                    break;
                case FieldDeclarationSyntax field:
                    members.AddRange(DeclarationParser.MapField(field));
                    break;
                default:
                    members.Add(DeclarationParser.Map(member));
                    break;
            }
        }

        return new TypeContract
        {
            Type = fullName,
            Access = MapTypeAccess(type.Modifiers),
            Kind = kind,
            Modifiers = MapTypeModifiers(type.Modifiers, kind),
            Extends = extends,
            Implements = implements.Count == 0 ? null : implements,
            TypeParams = MapTypeParams(type.TypeParameterList, type.ConstraintClauses),
            Members = members.Count == 0 ? null : members,
        };
    }

    private static TypeContract MapEnum(EnumDeclarationSyntax e, string namespacePrefix, string declaringPrefix)
    {
        string fullName = FullName(namespacePrefix, declaringPrefix, e.Identifier.Text);
        var members = new List<MemberContract>();
        long next = 0;
        foreach (EnumMemberDeclarationSyntax member in e.Members)
        {
            ConstantValue value = member.EqualsValue is { } init
                ? DeclarationParser.MapConstant(init.Value)
                : ConstantValue.Of(next);
            if (value.Value is long l)
                next = l + 1;

            members.Add(new FieldContract
            {
                Name = member.Identifier.Text,
                Modifiers = [MemberModifier.Const],
                Type = fullName,
                Value = value,
            });
        }

        return new TypeContract
        {
            Type = fullName,
            Access = MapTypeAccess(e.Modifiers),
            Kind = TypeKind.Enum,
            UnderlyingType = e.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
            Members = members.Count == 0 ? null : members,
        };
    }

    private static TypeContract MapDelegate(DelegateDeclarationSyntax d, string namespacePrefix, string declaringPrefix)
    {
        MemberContract method = DeclarationParser.ParseMember(
            $"public {d.ReturnType} Invoke{d.TypeParameterList}({d.ParameterList.Parameters}){string.Concat(d.ConstraintClauses)};");
        var invoke = (MethodContract)method;

        return new TypeContract
        {
            Type = FullName(namespacePrefix, declaringPrefix, d.Identifier.Text),
            Access = MapTypeAccess(d.Modifiers),
            Kind = TypeKind.Delegate,
            TypeParams = MapTypeParams(d.TypeParameterList, d.ConstraintClauses),
            Returns = invoke.Returns,
            Params = invoke.Params,
        };
    }

    private static (string? Extends, List<string> Implements) MapBaseList(BaseListSyntax? baseList, TypeKind kind)
    {
        var implements = new List<string>();
        if (baseList is null)
            return (null, implements);

        string? extends = null;
        var first = true;
        foreach (BaseTypeSyntax entry in baseList.Types)
        {
            var name = entry.Type.NormalizeWhitespace().ToString();
            // Syntax can't tell a base class from an interface; the conventional I-prefix
            // is the best available signal. Classes only - interfaces/structs implement.
            if (first && kind is TypeKind.Class or TypeKind.Record && !LooksLikeInterface(name))
                extends = name;
            else
                implements.Add(name);
            first = false;
        }

        return (extends, implements);

        static bool LooksLikeInterface(string name)
        {
            string leaf = name.Split('.')[^1];
            return leaf is ['I', _, ..] && char.IsUpper(leaf[1]);
        }
    }

    private static IReadOnlyList<TypeParamContract>? MapTypeParams(
        TypeParameterListSyntax? typeParams, SyntaxList<TypeParameterConstraintClauseSyntax> clauses)
    {
        if (typeParams is null || typeParams.Parameters.Count == 0)
            return null;

        // Reuse the member-level mapper by synthesizing a method with the same generics.
        var method = (MethodContract)DeclarationParser.ParseMember(
            $"void M{typeParams}(){string.Concat(clauses)};");
        return method.TypeParams;
    }

    private static Accessibility? MapTypeAccess(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword))
            return Accessibility.Public;
        if (modifiers.Any(SyntaxKind.InternalKeyword))
            return Accessibility.Internal;
        if (modifiers.Any(SyntaxKind.PrivateKeyword))
            return Accessibility.Private;
        if (modifiers.Any(SyntaxKind.ProtectedKeyword))
            return Accessibility.Protected;
        return null;
    }

    private static IReadOnlyList<TypeModifier>? MapTypeModifiers(SyntaxTokenList modifiers, TypeKind kind)
    {
        var result = new List<TypeModifier>();
        if (modifiers.Any(SyntaxKind.StaticKeyword))
            result.Add(TypeModifier.Static);
        if (modifiers.Any(SyntaxKind.AbstractKeyword) && !modifiers.Any(SyntaxKind.StaticKeyword))
            result.Add(TypeModifier.Abstract);
        if (modifiers.Any(SyntaxKind.SealedKeyword) && kind is TypeKind.Class or TypeKind.Record)
            result.Add(TypeModifier.Sealed);
        if (modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            result.Add(TypeModifier.Readonly);
        if (modifiers.Any(SyntaxKind.RefKeyword))
            result.Add(TypeModifier.Ref);
        return result.Count == 0 ? null : result;
    }

    private static string FullName(string namespacePrefix, string declaringPrefix, string name)
    {
        string nested = declaringPrefix.Length == 0 ? name : $"{declaringPrefix}+{name}";
        return namespacePrefix.Length == 0 ? nested : $"{namespacePrefix}.{nested}";
    }

    private static string NestPrefix(string declaringPrefix, string name) =>
        declaringPrefix.Length == 0 ? name : $"{declaringPrefix}+{name}";
}
