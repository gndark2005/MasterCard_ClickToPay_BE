using System.Text.Json;
using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>
/// Reads dynamicData.dynamicDataType first and deserializes the payload into the matching model:
/// CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM -> <see cref="TokenizedPayloadDto"/> (or <see cref="DsrpPanPayloadDto"/>
/// when a card object is present), DYNAMIC_CARD_SECURITY_CODE -> <see cref="DynamicSecurityCodePayloadDto"/>,
/// NONE or absent -> <see cref="FpanPayloadDto"/>. Writes the concrete model. Errors never include payload values.
/// </summary>
public sealed class DecryptedPayloadJsonConverter : JsonConverter<DecryptedPayloadDto>
{
    public static Type ResolveModel(string? dynamicDataType, bool hasCard) => dynamicDataType switch
    {
        DynamicDataTypes.Cryptogram => hasCard ? typeof(DsrpPanPayloadDto) : typeof(TokenizedPayloadDto),
        DynamicDataTypes.DynamicSecurityCode => typeof(DynamicSecurityCodePayloadDto),
        null or DynamicDataTypes.None => typeof(FpanPayloadDto),
        _ => throw new JsonException("Unsupported dynamicData.dynamicDataType.")
    };

    public override DecryptedPayloadDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The decrypted payload must be a JSON object.");
        }

        string? dynamicDataType = null;
        if (root.TryGetProperty("dynamicData", out var dynamicData) && dynamicData.ValueKind == JsonValueKind.Object &&
            dynamicData.TryGetProperty("dynamicDataType", out var type))
        {
            dynamicDataType = type.ValueKind switch
            {
                JsonValueKind.String => type.GetString(),
                JsonValueKind.Null => null,
                _ => throw new JsonException("dynamicData.dynamicDataType must be a string.")
            };
        }

        var hasCard = root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object;
        return (DecryptedPayloadDto?)root.Deserialize(ResolveModel(dynamicDataType, hasCard), options);
    }

    public override void Write(Utf8JsonWriter writer, DecryptedPayloadDto value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
