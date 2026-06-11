using Microsoft.Extensions.Options;
using System.Net;

namespace GeoLocationFilter
{
    public interface IClientIpResolver
    {
        /// <summary>
        /// Resolves the real client IP for the request. Forwarding headers
        /// (CF-Connecting-IP, X-Real-IP, X-Forwarded-For) are honored only when the
        /// direct peer is a trusted proxy — otherwise they can be spoofed by the client.
        /// </summary>
        string? GetClientIp(HttpContext context);
    }

    public class ClientIpResolver(IOptionsMonitor<SecurityOptions> options) : IClientIpResolver
    {
        private const string ContextItemKey = "__GeoFilter_ClientIp";

        private static readonly string[] SingleIpHeaders = ["CF-Connecting-IP", "X-Real-IP"];

        public string? GetClientIp(HttpContext context)
        {
            if (context.Items.TryGetValue(ContextItemKey, out var cached))
                return (string?)cached;

            var clientIp = Resolve(context);
            context.Items[ContextItemKey] = clientIp;
            return clientIp;
        }

        private string? Resolve(HttpContext context)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote == null)
                return null;

            remote = IpUtils.Normalize(remote);

            var trustedProxies = options.CurrentValue.TrustedProxies;
            if (!IpUtils.IsInAnyRange(remote, trustedProxies))
                return remote.ToString();

            // Direct peer is a trusted proxy — forwarding headers can be honored.
            foreach (var header in SingleIpHeaders)
            {
                if (TryParseHeaderIp(context.Request.Headers[header].FirstOrDefault(), out var headerIp))
                    return headerIp.ToString();
            }

            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                var entries = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);

                // Walk right to left and return the first hop that is not a trusted
                // proxy — entries further left were supplied by untrusted parties.
                IPAddress? leftmostValid = null;
                for (var i = entries.Length - 1; i >= 0; i--)
                {
                    if (!TryParseHeaderIp(entries[i], out var hop))
                        continue;

                    leftmostValid = hop;
                    if (!IpUtils.IsInAnyRange(hop, trustedProxies))
                        return hop.ToString();
                }

                // Whole chain is trusted proxies — use the leftmost (origin) entry.
                if (leftmostValid != null)
                    return leftmostValid.ToString();
            }

            return remote.ToString();
        }

        private static bool TryParseHeaderIp(string? value, out IPAddress ip)
        {
            ip = IPAddress.None;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // X-Forwarded-For entries may carry a port ("1.2.3.4:5678", "[::1]:80")
            if (!IPEndPoint.TryParse(value.Trim(), out var endpoint))
                return false;

            ip = IpUtils.Normalize(endpoint.Address);
            return true;
        }
    }
}
