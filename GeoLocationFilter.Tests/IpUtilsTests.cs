using System.Net;

namespace GeoLocationFilter.Tests
{
    public class IpUtilsTests
    {
        [Theory]
        // IPv4 CIDR
        [InlineData("192.168.1.100", "192.168.0.0/16", true)]
        [InlineData("192.169.0.1", "192.168.0.0/16", false)]
        [InlineData("10.20.30.40", "10.0.0.0/8", true)]
        [InlineData("11.0.0.1", "10.0.0.0/8", false)]
        [InlineData("172.16.0.1", "172.16.0.0/12", true)]
        [InlineData("172.31.255.255", "172.16.0.0/12", true)]
        [InlineData("172.32.0.1", "172.16.0.0/12", false)]
        [InlineData("127.0.0.1", "127.0.0.0/8", true)]
        // non-byte-aligned prefix
        [InlineData("192.168.1.5", "192.168.1.0/28", true)]
        [InlineData("192.168.1.20", "192.168.1.0/28", false)]
        // /32 exact
        [InlineData("1.2.3.4", "1.2.3.4/32", true)]
        [InlineData("1.2.3.5", "1.2.3.4/32", false)]
        // /0 matches everything in the same family
        [InlineData("8.8.8.8", "0.0.0.0/0", true)]
        // bare IP without prefix = exact match
        [InlineData("1.2.3.4", "1.2.3.4", true)]
        [InlineData("1.2.3.5", "1.2.3.4", false)]
        // base address with host bits set is masked, not rejected
        [InlineData("192.168.1.7", "192.168.1.5/24", true)]
        // IPv6
        [InlineData("::1", "::1/128", true)]
        [InlineData("::2", "::1/128", false)]
        [InlineData("fd00::1234", "fd00::/8", true)]
        [InlineData("2001:db8::1", "fd00::/8", false)]
        // IPv4-mapped IPv6 normalizes to IPv4
        [InlineData("::ffff:192.168.1.10", "192.168.0.0/16", true)]
        [InlineData("::ffff:8.8.8.8", "192.168.0.0/16", false)]
        // family mismatch
        [InlineData("192.168.1.1", "::1/128", false)]
        [InlineData("::1", "127.0.0.0/8", false)]
        // invalid ranges
        [InlineData("1.2.3.4", "", false)]
        [InlineData("1.2.3.4", "not-an-ip/8", false)]
        [InlineData("1.2.3.4", "1.2.3.0/abc", false)]
        [InlineData("1.2.3.4", "1.2.3.0/33", false)]
        [InlineData("1.2.3.4", "1.2.3.0/-1", false)]
        [InlineData("1.2.3.4", "1.2.3.0/24/extra", false)]
        public void IsInRange_MatchesExpected(string ip, string range, bool expected)
        {
            Assert.Equal(expected, IpUtils.IsInRange(IPAddress.Parse(ip), range));
        }

        [Fact]
        public void IsInAnyRange_MatchesAnyOfTheRanges()
        {
            var ranges = new[] { "10.0.0.0/8", "192.168.0.0/16" };

            Assert.True(IpUtils.IsInAnyRange(IPAddress.Parse("192.168.5.5"), ranges));
            Assert.True(IpUtils.IsInAnyRange(IPAddress.Parse("10.1.1.1"), ranges));
            Assert.False(IpUtils.IsInAnyRange(IPAddress.Parse("8.8.8.8"), ranges));
            Assert.False(IpUtils.IsInAnyRange(IPAddress.Parse("8.8.8.8"), []));
        }

        [Fact]
        public void ParseList_SplitsAndTrims()
        {
            Assert.Equal(["10.0.0.0/8", "192.168.0.0/16"], IpUtils.ParseList(" 10.0.0.0/8 , 192.168.0.0/16 ,, "));
            Assert.Empty(IpUtils.ParseList(null));
            Assert.Empty(IpUtils.ParseList(""));
            Assert.Empty(IpUtils.ParseList(" , ,"));
        }

        [Fact]
        public void ParseCountryList_NormalizesToUpperInvariant()
        {
            Assert.Equal(["CZ", "SK", "DE"], IpUtils.ParseCountryList("cz, Sk ,DE"));
            Assert.Empty(IpUtils.ParseCountryList(null));
        }
    }
}
