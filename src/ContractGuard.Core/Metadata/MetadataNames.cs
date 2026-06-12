using System.Reflection.Metadata;

namespace ContractGuard.Core.Metadata;

internal static class MetadataNames
{
    /// <summary>Strips the generic arity suffix: "List`1" -> "List".</summary>
    public static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>Full name of a type definition; nested types use '+'.</summary>
    public static string FullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        TypeDefinition td = md.GetTypeDefinition(handle);
        string name = StripArity(md.GetString(td.Name));
        TypeDefinitionHandle declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return FullName(md, declaring) + "+" + name;

        string ns = td.Namespace.IsNil ? string.Empty : md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Full name of a type reference; nested types use '+'.</summary>
    public static string FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        TypeReference tr = md.GetTypeReference(handle);
        string name = StripArity(md.GetString(tr.Name));
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(md, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;

        string ns = tr.Namespace.IsNil ? string.Empty : md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Full type name of a custom attribute's constructor owner, or null.</summary>
    public static string? AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                MethodDefinition def = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return FullName(md, def.GetDeclaringType());
            case HandleKind.MemberReference:
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return mr.Parent.Kind switch
                {
                    HandleKind.TypeReference => FullName(md, (TypeReferenceHandle)mr.Parent),
                    HandleKind.TypeDefinition => FullName(md, (TypeDefinitionHandle)mr.Parent),
                    _ => null,
                };
            default:
                return null;
        }
    }

    public static bool HasAttribute(MetadataReader md, CustomAttributeHandleCollection attributes, string fullName)
    {
        foreach (CustomAttributeHandle h in attributes)
        {
            if (AttributeTypeName(md, md.GetCustomAttribute(h)) == fullName)
                return true;
        }

        return false;
    }
}
