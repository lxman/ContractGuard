using ContractGuard.Metadata;
using ContractGuard.Model;

namespace ContractGuard.Comparison;

public static class ScopeFilter
{
    /// <summary>Combined accessibility levels count as in scope if either side is listed.
    /// TODO: refine private protected (currently treated as internal).</summary>
    public static bool InScope(Accessibility access, IReadOnlyList<Accessibility> scope) => access switch
    {
        Accessibility.Public => scope.Contains(Accessibility.Public),
        Accessibility.Protected => scope.Contains(Accessibility.Protected),
        Accessibility.Internal => scope.Contains(Accessibility.Internal),
        Accessibility.ProtectedInternal =>
            scope.Contains(Accessibility.Protected) || scope.Contains(Accessibility.Internal),
        Accessibility.PrivateProtected => scope.Contains(Accessibility.Internal),
        _ => scope.Contains(Accessibility.Private),
    };

    /// <summary>Drops out-of-scope types and members from an observed surface.</summary>
    public static AssemblySurface Apply(AssemblySurface surface, IReadOnlyList<Accessibility> scope)
    {
        var types = new List<TypeContract>();
        foreach (var type in surface.Types)
        {
            if (!InScope(type.Access ?? Accessibility.Public, scope))
                continue;

            types.Add(type.Members is null
                ? type
                : type with
                {
                    Members = type.Members
                        .Where(m => InScope(m.Access ?? Accessibility.Public, scope))
                        .ToList(),
                });
        }

        return surface with { Types = types };
    }
}
