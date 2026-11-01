using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pitwall.Data;

internal sealed class StringToBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString() ?? string.Empty;
            return bool.Parse(str);
        }

        // fallback to default handling
        return reader.GetBoolean();
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
