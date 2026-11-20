using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// Enums as Json.NET handled them without a converter: written as their number, read from a name in any case, from a
/// number (also one that names no member) or from a number in a string. An enum that carries its own
/// <c>[JsonConverter]</c> (the string enums of the wire) keeps it.
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum && !Attribute.IsDefined(typeToConvert, typeof(JsonConverterAttribute), inherit: false);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number when reader.TryGetInt64(out var number):
                    return (T)Enum.ToObject(typeof(T), number);
                case JsonTokenType.String:
                    var text = reader.GetString();
                    if (Enum.TryParse<T>(text, ignoreCase: true, out var value))
                        return value;
                    break;
            }

            throw new JsonException(string.Create(CultureInfo.InvariantCulture,
                $"{reader.TokenType} is not a value of {typeof(T).Name}"));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }
}
