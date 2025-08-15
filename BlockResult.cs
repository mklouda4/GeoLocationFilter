namespace GeoLocationFilter
{
    public record BlockResult(bool IsBlocked, string? CountryCode, string? Reason, string? Host, string? Uri, string? UserAgent);
}