namespace ContractGuard.Core;

/// <summary>Metadata operator names to contract symbols - shared by both front-ends so the
/// vocabulary cannot drift.</summary>
internal static class OperatorNames
{
    public static string Symbol(string metadataName) => metadataName switch
    {
        "op_Addition" or "op_UnaryPlus" => "+",
        "op_Subtraction" or "op_UnaryNegation" => "-",
        "op_Multiply" => "*",
        "op_Division" => "/",
        "op_Modulus" => "%",
        "op_Equality" => "==",
        "op_Inequality" => "!=",
        "op_LessThan" => "<",
        "op_GreaterThan" => ">",
        "op_LessThanOrEqual" => "<=",
        "op_GreaterThanOrEqual" => ">=",
        "op_BitwiseAnd" => "&",
        "op_BitwiseOr" => "|",
        "op_ExclusiveOr" => "^",
        "op_LogicalNot" => "!",
        "op_OnesComplement" => "~",
        "op_Increment" => "++",
        "op_Decrement" => "--",
        "op_LeftShift" => "<<",
        "op_RightShift" => ">>",
        "op_UnsignedRightShift" => ">>>",
        "op_True" => "true",
        "op_False" => "false",
        "op_Implicit" => "implicit",
        "op_Explicit" => "explicit",
        _ => metadataName,
    };
}
