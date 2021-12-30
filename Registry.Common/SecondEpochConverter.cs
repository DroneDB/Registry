using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Registry.Common
{
    public class SecondEpochConverter : DateTimeConverterBase
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteRawValue((((DateTime)value - Epoch).TotalMilliseconds / 1000.0).ToString(CultureInfo.InvariantCulture));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.Value == null) { return null; }

            double val;

            if (reader.ValueType == typeof(int)) val = (int)reader.Value;
            else if (reader.ValueType == typeof(long)) val = (long)reader.Value;
            else if (reader.ValueType == typeof(double)) val = (double)reader.Value;
            else throw new InvalidCastException();

            return Epoch.AddSeconds(val);
        }
    }
}