using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ContractGuard.Model;

namespace ContractGuard.Serialization;

/// <summary>
/// Members are a discriminated union on the "kind" property. The converter is registered for
/// the abstract base only (CanConvert is exact), so deserializing the concrete variant goes
/// through the default object mapping without recursing.
/// </summary>
internal sealed class MemberContractConverter : JsonConverter<MemberContract>
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(MemberContract);

    public override MemberContract Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            throw new JsonException(
                "Member entries are decomposed elements, not C# declaration strings. "
                + "Use 'contractguard add' to decompose a declaration into elements.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A member entry must be an object with a 'kind' property.");

        var node = JsonNode.Parse(ref reader)!.AsObject();
        var kind = node["kind"]?.GetValue<string>()
            ?? throw new JsonException("Member entry is missing its 'kind' property.");
        node.Remove("kind");

        Type target = kind switch
        {
            "method" => typeof(MethodContract),
            "constructor" => typeof(ConstructorMemberContract),
            "property" => typeof(PropertyContract),
            "indexer" => typeof(IndexerContract),
            "event" => typeof(EventContract),
            "field" => typeof(FieldContract),
            "operator" => typeof(OperatorContract),
            _ => throw new JsonException($"'{kind}' is not a member kind."),
        };

        return (MemberContract)JsonSerializer.Deserialize(node, target, options)!;
    }

    public override void Write(Utf8JsonWriter writer, MemberContract value, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options)!.AsObject();
        var ordered = new JsonObject { ["kind"] = value.KindName };
        foreach (var (key, propertyValue) in node.ToList())
        {
            node.Remove(key);
            ordered[key] = propertyValue;
        }

        ordered.WriteTo(writer, options);
    }
}
