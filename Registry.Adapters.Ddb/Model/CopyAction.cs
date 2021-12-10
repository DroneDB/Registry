using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DDB.Bindings.Model
{
    [JsonConverter(typeof(CopyActionConverter))]
    public class CopyAction
    {

        public CopyAction(string source, string dest)
        {
            Source = source;
            Destination = dest;
        }
        public string Source { get; }
        public string Destination { get; }

        public override string ToString()
        {
            return $"CPY -> {Source} TO {Destination}";
        }

    }

    public class CopyActionConverter : JsonConverter<CopyAction>
    {
        public override void WriteJson(JsonWriter writer, CopyAction value, JsonSerializer serializer)
        {
            writer.WriteValue(new[] { value.Source, value.Destination });
        }

        public override CopyAction ReadJson(JsonReader reader, Type objectType, CopyAction existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            JArray array = JArray.Load(reader);

            if (array.Count != 2)
                throw new FormatException("Invalid format, expected array with two elements");

            return new CopyAction(array[0].ToObject<string>(), array[1].ToObject<string>());
        }
    }
}