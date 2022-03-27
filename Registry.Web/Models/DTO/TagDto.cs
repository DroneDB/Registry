using System;
using Newtonsoft.Json;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO
{
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
        public override void WriteJson(JsonWriter writer, TagDto value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }

        public override TagDto ReadJson(JsonReader reader, Type objectType, TagDto existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            if (!(reader.Value is string val)) 
                throw new FormatException("Expected 'tag' to be a string");
            return val.ToTag();
        }
    }
}