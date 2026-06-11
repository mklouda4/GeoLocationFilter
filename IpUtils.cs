using System.Net;

namespace GeoLocationFilter
{
    /// <summary>
    /// Helpers for parsing and matching IP addresses, CIDR ranges and comma-separated lists.
    /// </summary>
    public static class IpUtils
    {
        public static List<string> ParseList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            return [.. value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrEmpty(v))];
        }

        public static List<string> ParseCountryList(string? value)
            => [.. ParseList(value).Select(c => c.ToUpperInvariant())];

        public static bool IsInAnyRange(IPAddress ip, IEnumerable<string> ranges)
            => ranges.Any(range => IsInRange(ip, range));

        /// <summary>
        /// Matches an IP against a CIDR range ("10.0.0.0/8") or a single address ("10.0.0.1").
        /// IPv4-mapped IPv6 addresses are normalized to IPv4 before comparison.
        /// </summary>
        public static bool IsInRange(IPAddress ip, string range)
        {
            if (string.IsNullOrWhiteSpace(range))
                return false;

            ip = Normalize(ip);

            var parts = range.Split('/');
            if (!IPAddress.TryParse(parts[0].Trim(), out var baseAddress))
                return false;

            baseAddress = Normalize(baseAddress);

            if (parts.Length == 1)
                return ip.Equals(baseAddress);

            if (parts.Length != 2 || !int.TryParse(parts[1], out var prefixLength))
                return false;

            if (ip.AddressFamily != baseAddress.AddressFamily)
                return false;

            var maxPrefix = baseAddress.GetAddressBytes().Length * 8;
            if (prefixLength < 0 || prefixLength > maxPrefix)
                return false;

            return PrefixEquals(ip, baseAddress, prefixLength);
        }

        public static IPAddress Normalize(IPAddress ip)
            => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

        private static bool PrefixEquals(IPAddress a, IPAddress b, int prefixLength)
        {
            var aBytes = a.GetAddressBytes();
            var bBytes = b.GetAddressBytes();
            if (aBytes.Length != bBytes.Length)
                return false;

            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (aBytes[i] != bBytes[i])
                    return false;
            }

            if (remainingBits > 0)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((aBytes[fullBytes] & mask) != (bBytes[fullBytes] & mask))
                    return false;
            }

            return true;
        }
    }
}
