namespace GeoLocationFilter
{
    public static class HeaderDictionaryExtensions
    {
        public static bool AddOrReplace(this IHeaderDictionary headers, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key) || headers == null)
                return false;

            if (headers.ContainsKey(key))
                _ = headers.Remove(key);

            if (value != null)
                _ = headers.TryAdd(key, value);

            return true;
        }
    }
}