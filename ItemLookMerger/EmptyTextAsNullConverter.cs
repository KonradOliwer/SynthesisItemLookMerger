using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItemLookMerger;

/// <summary>
/// Reads an empty text as a JSON null; everything else is read and written as the serializer's own converters do. The Synthesis settings
/// screen saves a plugin picker left unset as "", which the plugin name converter rejects, while it reads null as "not chosen".
/// It goes on the setting as an attribute because Synthesis reads settings.json with its own serializer settings, and a
/// converter named on a property (or on its items) is chosen before the serializer's converters.
/// </summary>
public sealed class EmptyTextAsNullConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => true;

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
        reader.TokenType == JsonToken.String && (string?)reader.Value == string.Empty
            ? serializer.Deserialize(JValue.CreateNull().CreateReader(), objectType)
            : serializer.Deserialize(reader, objectType);

    /// <summary>
    /// A converter on a property hides the serializer's matching converter even when it cannot write, so it calls that converter
    /// directly; serializing the value again through the serializer would be taken for a self-referencing loop.
    /// </summary>
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        serializer.Converters.First(converter => converter.CanWrite && converter.CanConvert(value!.GetType())).WriteJson(writer, value, serializer);
}
