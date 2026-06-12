using System.Text.Json;
using System.Text.Json.Serialization;
using ContractGuard.Core.Model;

namespace ContractGuard.Core.Serialization;

/// <summary>
/// Enum converter with an explicit string map. Several contract enums need JSON forms no
/// naming policy produces ("protected internal", "record-struct", "ref readonly"), and an
/// explicit table doubles as the single source of truth for the wire vocabulary.
/// </summary>
internal sealed class MappedEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private readonly Dictionary<T, string> _toJson;
    private readonly Dictionary<string, T> _fromJson;

    public MappedEnumConverter(params (T Value, string Name)[] map)
    {
        _toJson = map.ToDictionary(m => m.Value, m => m.Name);
        _fromJson = map.ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal);
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string for {typeof(T).Name}.");

        string text = reader.GetString()!;
        if (_fromJson.TryGetValue(text, out T value))
            return value;

        throw new JsonException(
            $"'{text}' is not a valid {typeof(T).Name}. Allowed: {string.Join(", ", _fromJson.Keys)}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(_toJson[value]);

    /// <summary>The wire name of a value - also used by the declaration renderer so the
    /// vocabulary stays single-sourced.</summary>
    public string NameOf(T value) => _toJson[value];
}

internal static class EnumMaps
{
    public static readonly MappedEnumConverter<Accessibility> Accessibility = new(
        (Model.Accessibility.Public, "public"),
        (Model.Accessibility.Protected, "protected"),
        (Model.Accessibility.Internal, "internal"),
        (Model.Accessibility.ProtectedInternal, "protected internal"),
        (Model.Accessibility.PrivateProtected, "private protected"),
        (Model.Accessibility.Private, "private"));

    public static readonly MappedEnumConverter<AllowDeny> AllowDeny = new(
        (Model.AllowDeny.Allow, "allow"),
        (Model.AllowDeny.Deny, "deny"));

    public static readonly MappedEnumConverter<Significance> Significance = new(
        (Model.Significance.Significant, "significant"),
        (Model.Significance.Ignored, "ignored"));

    public static readonly MappedEnumConverter<EntryMode> EntryMode = new(
        (Model.EntryMode.Required, "required"),
        (Model.EntryMode.Forbidden, "forbidden"));

    public static readonly MappedEnumConverter<TypeKind> TypeKind = new(
        (Model.TypeKind.Class, "class"),
        (Model.TypeKind.Struct, "struct"),
        (Model.TypeKind.Interface, "interface"),
        (Model.TypeKind.Record, "record"),
        (Model.TypeKind.RecordStruct, "record-struct"),
        (Model.TypeKind.Enum, "enum"),
        (Model.TypeKind.Delegate, "delegate"));

    public static readonly MappedEnumConverter<TypeModifier> TypeModifier = new(
        (Model.TypeModifier.Static, "static"),
        (Model.TypeModifier.Abstract, "abstract"),
        (Model.TypeModifier.Sealed, "sealed"),
        (Model.TypeModifier.Readonly, "readonly"),
        (Model.TypeModifier.Ref, "ref"));

    public static readonly MappedEnumConverter<MemberModifier> MemberModifier = new(
        (Model.MemberModifier.Static, "static"),
        (Model.MemberModifier.Abstract, "abstract"),
        (Model.MemberModifier.Virtual, "virtual"),
        (Model.MemberModifier.Sealed, "sealed"),
        (Model.MemberModifier.Override, "override"),
        (Model.MemberModifier.Readonly, "readonly"),
        (Model.MemberModifier.Const, "const"),
        (Model.MemberModifier.Volatile, "volatile"));

    public static readonly MappedEnumConverter<ReturnRefKind> ReturnRefKind = new(
        (Model.ReturnRefKind.Ref, "ref"),
        (Model.ReturnRefKind.RefReadonly, "ref readonly"));

    public static readonly MappedEnumConverter<ParamModifier> ParamModifier = new(
        (Model.ParamModifier.Ref, "ref"),
        (Model.ParamModifier.Out, "out"),
        (Model.ParamModifier.In, "in"),
        (Model.ParamModifier.RefReadonly, "ref readonly"),
        (Model.ParamModifier.Params, "params"),
        (Model.ParamModifier.This, "this"));

    public static readonly MappedEnumConverter<Variance> Variance = new(
        (Model.Variance.In, "in"),
        (Model.Variance.Out, "out"));
}
