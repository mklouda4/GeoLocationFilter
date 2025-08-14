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

        public bool IgnoreLocalIps { get; set; } = true;
        // Default IP range
        public List<string> LocalIps { get; set; } =
        [
            "127.0.0.0/8",     // localhost
            "10.0.0.0/8",      // private
            "172.16.0.0/12",   // private  
            "192.168.0.0/16"   // private
        ];

        public bool BlockUnknown { get; set; } = true;
        public List<string> AllowedCountries { get; set; } = [];
        public List<string> BlockedCountries { get; set; } = [];
    }
}