using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Serialization;

/// <summary>
/// Parameters have two JSON forms: the compact ["type", "name"] pair for plain parameters,
/// and an object when a modifier or default value is involved. Mapping is done manually here
/// (the converter is registered for the concrete type, so delegating back to the serializer
/// would recurse).
/// </summary>
internal sealed class ParamContractConverter : JsonConverter<ParamContract>
{
    public override ParamContract Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.StartArray => ReadPair(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader),
            _ => throw new JsonException(
                "A parameter is either a [\"type\", \"name\"] pair or an object with a 'type' property."),
        };
    }

    private static ParamContract ReadPair(ref Utf8JsonReader reader)
    {
        JsonArray node = JsonNode.Parse(ref reader)!.AsArray();
        if (node.Count != 2 || node.Any(n => n?.GetValueKind() != JsonValueKind.String))
        {
            throw new JsonException(
                "The compact parameter form is exactly [\"type\", \"name\"]; use the object form for anything more.");
        }

        return new ParamContract
        {
            Type = node[0]!.GetValue<string>(),
            Name = node[1]!.GetValue<string>(),
        };
    }

    private static ParamContract ReadObject(ref Utf8JsonReader reader)
    {
        JsonObject node = JsonNode.Parse(ref reader)!.AsObject();
        string? type = null;
        string? name = null;
        ParamModifier? modifier = null;
        ConstantValue? defaultValue = null;

        foreach ((string key, JsonNode? value) in node.ToList())
        {
            switch (key)
            {
                case "type":
                    type = value?.GetValue<string>();
                    break;
                case "name":
                    name = value?.GetValue<string>();
                    break;
                case "modifier":
                    modifier = ParseModifier(value?.GetValue<string>());
                    break;
                case "default":
                    defaultValue = ConstantValueConverter.FromNode(value);
                    break;
                default:
                    throw new JsonException($"Unknown parameter property '{key}'.");
            }
        }

        return string.IsNullOrEmpty(type)
            ? throw new JsonException("A parameter object requires a non-empty 'type'.")
            : new ParamContract { Type = type, Name = name, Modifier = modifier, Default = defaultValue };
    }

    private static ParamModifier ParseModifier(string? text) => text switch
    {
        "ref" => ParamModifier.Ref,
        "out" => ParamModifier.Out,
        "in" => ParamModifier.In,
        "ref readonly" => ParamModifier.RefReadonly,
        "params" => ParamModifier.Params,
        "this" => ParamModifier.This,
        _ => throw new JsonException($"'{text}' is not a valid parameter modifier."),
    };

    public override void Write(Utf8JsonWriter writer, ParamContract value, JsonSerializerOptions options)
    {
        if (value.Name is not null && value.Modifier is null && value.Default is null)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value.Type);
            writer.WriteStringValue(value.Name);
            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        if (value.Name is not null)
            writer.WriteString("name", value.Name);
        if (value.Modifier is { } m)
        {
            writer.WritePropertyName("modifier");
            EnumMaps.ParamModifier.Write(writer, m, options);
        }

        if (value.Default is not null)
        {
            writer.WritePropertyName("default");
            new ConstantValueConverter().Write(writer, value.Default, options);
        }

        writer.WriteEndObject();
    }
}
