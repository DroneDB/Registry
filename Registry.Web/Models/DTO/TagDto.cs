using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO;

[JsonConverter(typeof(TagConverter))]
public class TagDto
{
    public TagDto(string organizationSlug, string datasetSlug)
    {
        OrganizationSlug = organizationSlug;
        DatasetSlug = datasetSlug;
    }

    public string OrganizationSlug { get; }
    public string DatasetSlug { get; }

    public override string ToString()
    {
        return $"{OrganizationSlug}/{DatasetSlug}";
    }
}

public class TagConverter : JsonConverter<TagDto>
{
    public override void Write(Utf8JsonWriter writer, TagDto value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override TagDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new FormatException("Expected 'tag' to be a string");
        return reader.GetString()!.ToTag();
    }
}
