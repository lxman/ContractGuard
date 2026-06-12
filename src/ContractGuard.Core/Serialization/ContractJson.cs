using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Serialization;

/// <summary>
/// Loads and saves contract files. Reading is strict (unknown properties are errors, matching
/// the schema's additionalProperties: false) but tolerates // comments and trailing commas,
/// since contracts are hand-edited governance files.
/// </summary>
public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            // Contracts are hand-edited governance files: keep "Task<Result>" readable
            // instead of escaping angle brackets.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    static typeInfo =>
                    {
                        if (typeInfo.Kind == JsonTypeInfoKind.Object)
                            typeInfo.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                    },
                },
            },
        };

        options.Converters.Add(EnumMaps.Accessibility);
        options.Converters.Add(EnumMaps.AllowDeny);
        options.Converters.Add(EnumMaps.Significance);
        options.Converters.Add(EnumMaps.EntryMode);
        options.Converters.Add(EnumMaps.TypeKind);
        options.Converters.Add(EnumMaps.TypeModifier);
        options.Converters.Add(EnumMaps.MemberModifier);
        options.Converters.Add(EnumMaps.ReturnRefKind);
        options.Converters.Add(EnumMaps.ParamModifier);
        options.Converters.Add(EnumMaps.Variance);
        options.Converters.Add(new MemberContractConverter());
        options.Converters.Add(new ParamContractConverter());
        options.Converters.Add(new ConstantValueConverter());
        return options;
    }

    public static AssemblyContract Load(string path) => Parse(File.ReadAllText(path));

    public static AssemblyContract Parse(string json) =>
        JsonSerializer.Deserialize<AssemblyContract>(json, Options)
        ?? throw new InvalidDataException("Contract JSON deserialized to null.");

    public static string Serialize(AssemblyContract contract) => JsonSerializer.Serialize(contract, Options);

    public static void Save(AssemblyContract contract, string path) =>
        File.WriteAllText(path, Serialize(contract) + Environment.NewLine);
}
