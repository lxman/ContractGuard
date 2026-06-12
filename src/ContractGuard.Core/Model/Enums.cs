namespace ContractGuard.Core.Model;

/// <summary>C# accessibility levels. JSON forms include the two-word combinations ("protected internal").</summary>
public enum Accessibility
{
    Public,
    Protected,
    Internal,
    ProtectedInternal,
    PrivateProtected,
    Private,
}

public enum AllowDeny
{
    Allow,
    Deny,
}

/// <summary>Whether the comparer treats an aspect as part of the contract.</summary>
public enum Significance
{
    Significant,
    Ignored,
}

/// <summary>Required: must exist as prescribed. Forbidden: the exact signature must NOT exist.</summary>
public enum EntryMode
{
    Required,
    Forbidden,
}

public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Record,
    RecordStruct,
    Enum,
    Delegate,
}

public enum TypeModifier
{
    Static,
    Abstract,
    Sealed,
    Readonly,
    Ref,
}

/// <summary>
/// Signature-level member modifiers. 'async' is deliberately absent: it is an implementation
/// detail invisible in the binary signature, never a contract concept.
/// </summary>
public enum MemberModifier
{
    Static,
    Abstract,
    Virtual,
    Sealed,
    Override,
    Readonly,
    Const,
    Volatile,
}

/// <summary>Ref kind of a ref-returning member.</summary>
public enum ReturnRefKind
{
    Ref,
    RefReadonly,
}

public enum ParamModifier
{
    Ref,
    Out,
    In,
    RefReadonly,
    Params,
    This,
}

public enum Variance
{
    In,
    Out,
}
