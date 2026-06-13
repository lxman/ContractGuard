using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Serialization;

/// <summary>
/// Constants are typed JSON values: string, number, boolean, or null. The object form
/// {"$special": "default"} represents default(T) for value types with no representable constant.
/// </summary>
internal sealed class ConstantValueConverter : JsonConverter<ConstantValue>
{
    public override bool HandleNull => true;

    public override ConstantValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return ConstantValue.Of(null);
            case JsonTokenType.String:
                return ConstantValue.Of(reader.GetString());
            case JsonTokenType.True:
                return ConstantValue.Of(true);
            case JsonTokenType.False:
                return ConstantValue.Of(false);
            case JsonTokenType.Number:
                // Integers stay longs; fractional literals prefer decimal so they compare
                // exactly against DecimalConstantAttribute-decoded values.
                if (reader.TryGetInt64(out long l))
                    return ConstantValue.Of(l);
                return reader.TryGetDecimal(out decimal m)
                    ? ConstantValue.Of(m)
                    : ConstantValue.Of(reader.GetDouble());
            case JsonTokenType.StartObject:
                JsonObject node = JsonNode.Parse(ref reader)!.AsObject();
                return ReadSpecial(node);
            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}' for a constant value.");
        }
    }

    public static ConstantValue FromNode(JsonNode? node)
    {
        return node switch
        {
            null => ConstantValue.Of(null),
            JsonObject obj => ReadSpecial(obj),
            _ => node.GetValueKind() switch
            {
                JsonValueKind.String => ConstantValue.Of(node.GetValue<string>()),
                JsonValueKind.True => ConstantValue.Of(true),
                JsonValueKind.False => ConstantValue.Of(false),
                JsonValueKind.Number => node.AsValue().TryGetValue<long>(out long l)
                    ? ConstantValue.Of(l)
                    : node.AsValue().TryGetValue<decimal>(out decimal m)
                        ? ConstantValue.Of(m)
                        : ConstantValue.Of(node.GetValue<double>()),
                var kind => throw new JsonException($"Unexpected '{kind}' for a constant value."),
            }
        };
    }

    private static ConstantValue ReadSpecial(JsonObject obj)
    {
        if (obj.Count == 1 && obj.TryGetPropertyValue("$special", out JsonNode? special))
        {
            switch (special?.GetValue<string>())
            {
                case "default": return ConstantValue.DefaultSentinel;
                // JSON has no literal for non-finite doubles; they ride in the $special form.
                case "NaN": return ConstantValue.Of(double.NaN);
                case "Infinity": return ConstantValue.Of(double.PositiveInfinity);
                case "-Infinity": return ConstantValue.Of(double.NegativeInfinity);
            }
        }

        throw new JsonException(
            "The object form of a constant must be {\"$special\": \"default\"|\"NaN\"|\"Infinity\"|\"-Infinity\"}.");
    }

    public override void Write(Utf8JsonWriter writer, ConstantValue value, JsonSerializerOptions options)
    {
        if (value.IsDefaultSentinel)
        {
            writer.WriteStartObject();
            writer.WriteString("$special", "default");
            writer.WriteEndObject();
            return;
        }

        switch (value.Value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                WriteDouble(writer, d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            default:
                throw new JsonException($"Cannot serialize constant of type {value.Value.GetType()}.");
        }
    }

    private static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        string? special = double.IsNaN(value) ? "NaN"
            : double.IsPositiveInfinity(value) ? "Infinity"
            : double.IsNegativeInfinity(value) ? "-Infinity"
            : null;
        if (special is null)
        {
            writer.WriteNumberValue(value);
            return;
        }

        // JSON forbids NaN/Infinity as numbers; carry them in the $special object form.
        writer.WriteStartObject();
        writer.WriteString("$special", special);
        writer.WriteEndObject();
    }
}
