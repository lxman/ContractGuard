using System.Globalization;

namespace ContractGuard.Core.Model;

/// <summary>
/// A constant (parameter default, const field value) as a typed value. Numeric values are
/// normalized on construction - integrals to long, float to double - so that values read from
/// contract JSON and values decoded from metadata compare equal. <see cref="DefaultSentinel"/>
/// represents default(T) for value types with no representable constant (JSON form
/// {"$special": "default"}).
/// </summary>
public sealed class ConstantValue : IEquatable<ConstantValue>
{
    public static readonly ConstantValue DefaultSentinel = new(null, isDefaultSentinel: true);

    public static ConstantValue Of(object? value) => new(Normalize(value), isDefaultSentinel: false);

    private ConstantValue(object? value, bool isDefaultSentinel)
    {
        Value = value;
        IsDefaultSentinel = isDefaultSentinel;
    }

    public object? Value { get; }

    public bool IsDefaultSentinel { get; }

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        bool or string or double or decimal or long => value,
        char c => c.ToString(),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong ul => unchecked((long)ul),
        float f => (double)f,
        _ => value,
    };

    public bool Equals(ConstantValue? other)
    {
        if (other is null)
            return false;

        // Metadata encodes both 'null' and 'default(TStruct)' as a nullref constant, so the
        // two cannot be told apart at comparison time and are treated as equal.
        bool thisNullish = IsDefaultSentinel || Value is null;
        bool otherNullish = other.IsDefaultSentinel || other.Value is null;
        if (thisNullish || otherNullish)
            return thisNullish && otherNullish;

        // Numeric values bridge representations: JSON may parse 9.99 where metadata decoded
        // the decimal 9.99m. Compare through decimal when both sides are numeric.
        if (TryAsDecimal(Value, out decimal a) && TryAsDecimal(other.Value, out decimal b))
            return a == b;

        return Equals(Value, other.Value);
    }

    public override bool Equals(object? obj) => Equals(obj as ConstantValue);

    public override int GetHashCode()
    {
        if (IsDefaultSentinel || Value is null)
            return 0;
        if (TryAsDecimal(Value, out decimal d))
            return d.GetHashCode();
        return Value.GetHashCode();
    }

    private static bool TryAsDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case decimal m:
                result = m;
                return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d)
                && d is >= (double)decimal.MinValue and <= (double)decimal.MaxValue:
                result = (decimal)d;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    public override string ToString() => IsDefaultSentinel
        ? "default"
        : Value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => $"\"{s}\"",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var v => v.ToString() ?? "?",
        };
}
