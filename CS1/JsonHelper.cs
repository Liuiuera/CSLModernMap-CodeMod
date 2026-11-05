using System.Globalization;
using Newtonsoft.Json;

namespace CSLModernMap.Traffic
{
    internal static class JsonHelper
    {
        // Keep zero, false and null fields in the traffic snapshot contract.
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
            Culture = CultureInfo.InvariantCulture
        };

        public static string Serialize(object obj)
        {
            return JsonConvert.SerializeObject(obj, Settings);
        }
    }
}
