namespace ContractGuard.Core.Comparison;

public enum DiagnosticSeverity
{
    Error,
    Warning,
}

public sealed record Diagnostic(
    string Id,
    DiagnosticSeverity Severity,
    string Message,
    string? TypeName = null,
    string? Member = null,
    string? Reason = null)
{
    /// <summary>Source file/line of the offending member ("path(line)"), resolved from the
    /// portable PDB when available.</summary>
    public string? SourceLocation { get; init; }

    public override string ToString()
    {
        string location = (TypeName, Member) switch
        {
            (null, _) => string.Empty,
            (var t, null) => $" [{t}]",
            (var t, var m) => $" [{t}.{m}]",
        };

        string reason = Reason is null ? string.Empty : $" ({Reason})";
        string source = SourceLocation is null ? string.Empty : $" @ {SourceLocation}";
        return $"{Id}: {Message}{location}{reason}{source}";
    }

    /// <summary>
    /// MSBuild-canonical line ("origin : category code: text"), recognized by Exec and
    /// surfaced in the IDE error list. The origin is the offending source location when the
    /// PDB provided one, else the given fallback (the contract file). Lives here so the CLI
    /// and the gate executable cannot drift.
    /// </summary>
    public string ToMsBuildString(string fallbackOrigin)
    {
        string severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        string context = (TypeName, Member) switch
        {
            (null, _) => string.Empty,
            (var t, null) => $" [{t}]",
            (var t, var m) => $" [{t}.{m}]",
        };
        string reason = Reason is null ? string.Empty : $" ({Reason})";
        return $"{SourceLocation ?? fallbackOrigin} : {severity} {Id}: {Message}{context}{reason}";
    }
}

public sealed record ComparisonResult(IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Passed => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
}

public static class DiagnosticIds
{
    public const string AssemblyNameMismatch = "CG0001";

    public const string TypeMissing = "CG0100";
    public const string TypeKindMismatch = "CG0101";
    public const string TypeModifiersMismatch = "CG0102";
    public const string BaseTypeMismatch = "CG0103";
    public const string InterfaceMissing = "CG0104";
    public const string TypeParamsMismatch = "CG0105";
    public const string UnderlyingTypeMismatch = "CG0106";
    public const string DelegateSignatureMismatch = "CG0107";

    public const string MemberMissing = "CG0200";
    public const string MemberSignatureChanged = "CG0201";
    public const string AccessMismatch = "CG0202";
    public const string ModifiersMismatch = "CG0203";
    public const string ReturnTypeMismatch = "CG0204";
    public const string ParameterNamesChanged = "CG0205";
    public const string ParameterDefaultsChanged = "CG0206";
    public const string AccessorsMismatch = "CG0207";
    public const string ConstValueChanged = "CG0208";
    public const string TypeParamsChanged = "CG0209";
    public const string ParameterModifiersChanged = "CG0210";
    public const string AttributesMismatch = "CG0211";

    public const string ForbiddenMemberPresent = "CG0300";

    public const string UnexpectedMember = "CG0400";

    public const string UnexpectedType = "CG0500";
}
