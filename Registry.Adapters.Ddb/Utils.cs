using System;

namespace Registry.Adapters.Ddb
{
    public static class Utils
    {
        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime UnixTimestampToDateTime(long timestamp)
        {
            return UnixEpoch.Add(TimeSpan.FromSeconds(timestamp)).ToLocalTime();
        }

    }
}
