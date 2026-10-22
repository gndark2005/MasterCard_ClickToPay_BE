using System.Text.Json;
using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>
/// Reads dynamicData as an object or as an array. Mastercard sends an array when it adds entries such as
/// CARDHOLDER_AUTHENTICATION_CRYPTOGRAM next to the payment one; the payment entry (a
/// <see cref="DynamicDataTypes"/> value) is kept, otherwise the first entry. Writes a single object.
/// Errors never include payload values.
/// </summary>
public sealed class DynamicDataJsonConverter : JsonConverter<DynamicDataDto>
{
    private static readonly string[] PaymentTypes =
        [DynamicDataTypes.Cryptogram, DynamicDataTypes.DynamicSecurityCode, DynamicDataTypes.None];

    /// <summary>The dynamicData entry used for the payment, or null when there is none.</summary>
    public static JsonElement? SelectEntry(JsonElement dynamicData)
    {
        switch (dynamicData.ValueKind)
        {
            case JsonValueKind.Object:
                return dynamicData;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Array:
                var entries = dynamicData.EnumerateArray().ToList();
                if (entries.Any(e => e.ValueKind != JsonValueKind.Object))
                {
                    throw new JsonException("dynamicData entries must be JSON objects.");
                }

                return entries.Cast<JsonElement?>().FirstOrDefault(e => PaymentTypes.Contains(TypeOf(e!.Value)))
                    ?? entries.Cast<JsonElement?>().FirstOrDefault();
            default:
                throw new JsonException("dynamicData must be an object or an array.");
        }
    }

    public override DynamicDataDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (SelectEntry(document.RootElement) is not { } entry)
        {
            return null;
        }

        return new DynamicDataDto
        {
            DynamicDataValue = StringOf(entry, "dynamicDataValue"),
            DynamicDataType = StringOf(entry, "dynamicDataType"),
        };
    }

    // DynamicDataDto has no converter of its own, so this is the default object serialization.
    public override void Write(Utf8JsonWriter writer, DynamicDataDto value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);

    private static string? TypeOf(JsonElement entry) =>
        entry.TryGetProperty("dynamicDataType", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static string? StringOf(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => throw new JsonException($"dynamicData.{name} must be a string.")
        };
    }
}
