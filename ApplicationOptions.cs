namespace GeoLocationFilter
{
    /// <summary>
    /// Application configuration options for branding and behavior
    /// </summary>
    public class SecurityOptions
    {
        /// <summary>
        /// Configuration section name
        /// </summary>
        public const string SectionName = "Security";

        /// <summary>
        /// Loopback + RFC1918 private ranges. Used as the default for
        /// <see cref="LocalIps"/> and <see cref="TrustedProxies"/> when the
        /// configuration does not provide any values. Defaults are applied via
        /// PostConfigure (not property initializers) because the configuration
        /// binder appends list items to pre-initialized defaults instead of
        /// replacing them — which would make the lists impossible to restrict.
        /// </summary>
        public static readonly IReadOnlyList<string> PrivateNetworkDefaults =
        [
            "127.0.0.0/8",     // localhost
            "::1/128",         // localhost (IPv6)
            "10.0.0.0/8",      // private
            "172.16.0.0/12",   // private
            "192.168.0.0/16"   // private
        ];

        public bool IgnoreLocalIps { get; set; } = true;
        public List<string> LocalIps { get; set; } = [];

        /// <summary>
        /// Networks whose forwarding headers (X-Forwarded-For, X-Real-IP, CF-Connecting-IP)
        /// are trusted. Requests arriving directly from other addresses use the socket
        /// address and their headers are ignored, so clients cannot spoof their IP.
        /// </summary>
        public List<string> TrustedProxies { get; set; } = [];

        public bool BlockUnknown { get; set; } = true;
        public List<string> AllowedCountries { get; set; } = [];
        public List<string> BlockedCountries { get; set; } = [];
    }
}