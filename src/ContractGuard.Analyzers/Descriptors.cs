using System.Collections.Immutable;
using ContractGuard.Core.Comparison;
using Microsoft.CodeAnalysis;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace ContractGuard.Analyzers;

/// <summary>One descriptor per comparer diagnostic id - the analyzer speaks the same CG
/// vocabulary as the gate, so suppressions and severity configuration stay unified.</summary>
internal static class Descriptors
{
    public const string InvalidContractId = "CG0002";

    private static readonly ImmutableDictionary<string, DiagnosticDescriptor> ById = Build();

    public static ImmutableArray<DiagnosticDescriptor> All { get; } = [.. ById.Values];

    public static DiagnosticDescriptor For(string id) => ById[id];

    private static ImmutableDictionary<string, DiagnosticDescriptor> Build()
    {
        var titles = new Dictionary<string, string>
        {
            [DiagnosticIds.AssemblyNameMismatch] = "Contract governs a different assembly",
            [InvalidContractId] = "Contract file is invalid",
            [DiagnosticIds.TypeMissing] = "Governed type is missing",
            [DiagnosticIds.TypeKindMismatch] = "Type kind does not match the contract",
            [DiagnosticIds.TypeModifiersMismatch] = "Type modifiers do not match the contract",
            [DiagnosticIds.BaseTypeMismatch] = "Base type does not match the contract",
            [DiagnosticIds.InterfaceMissing] = "Prescribed interface is not implemented",
            [DiagnosticIds.TypeParamsMismatch] = "Type generic parameters do not match the contract",
            [DiagnosticIds.UnderlyingTypeMismatch] = "Enum underlying type does not match the contract",
            [DiagnosticIds.DelegateSignatureMismatch] = "Delegate signature does not match the contract",
            [DiagnosticIds.MemberMissing] = "Prescribed member is missing",
            [DiagnosticIds.MemberSignatureChanged] = "Member signature changed",
            [DiagnosticIds.AccessMismatch] = "Accessibility does not match the contract",
            [DiagnosticIds.ModifiersMismatch] = "Modifiers do not match the contract",
            [DiagnosticIds.ReturnTypeMismatch] = "Type does not match the contract",
            [DiagnosticIds.ParameterNamesChanged] = "Parameter names do not match the contract",
            [DiagnosticIds.ParameterDefaultsChanged] = "Parameter defaults do not match the contract",
            [DiagnosticIds.AccessorsMismatch] = "Accessors do not match the contract",
            [DiagnosticIds.ConstValueChanged] = "Constant value does not match the contract",
            [DiagnosticIds.TypeParamsChanged] = "Generic parameters do not match the contract",
            [DiagnosticIds.ParameterModifiersChanged] = "Parameter modifiers do not match the contract",
            [DiagnosticIds.AttributesMismatch] = "Significant attributes do not match the contract",
            [DiagnosticIds.ForbiddenMemberPresent] = "Forbidden member is present",
            [DiagnosticIds.UnexpectedMember] = "Member is not part of the contract",
            [DiagnosticIds.UnexpectedType] = "Type is not part of the contract",
        };

        ImmutableDictionary<string, DiagnosticDescriptor>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, DiagnosticDescriptor>();
        foreach (KeyValuePair<string, string> entry in titles)
        {
            builder.Add(entry.Key, new DiagnosticDescriptor(
                id: entry.Key,
                title: entry.Value,
                messageFormat: "{0}",
                category: "ContractGuard",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: entry.Value + ". The contract file is the source of truth; "
                    + "change the code back, or change the contract through review.",
                helpLinkUri: "https://github.com/lxman/ContractGuard/blob/main/docs/diagnostics.md#"
                    + entry.Key.ToLowerInvariant()));
        }

        return builder.ToImmutable();
    }
}
