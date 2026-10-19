using System.Net;
using LaserficheReports.Web;
using Xunit;

public class LocalAccessTests
{
    private static readonly IPAddress[] Machine = [IPAddress.Parse("192.168.101.207"), IPAddress.Parse("fe80::6194:c5df:422:e8f4%16")];

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.101.207", true)]
    [InlineData("::ffff:192.168.101.207", true)]
    [InlineData("fe80::6194:c5df:422:e8f4", true)]
    [InlineData("192.168.101.50", false)]
    [InlineData("10.0.0.7", false)]
    public void OnlyThisMachineIsLocal(string address, bool expected) =>
        Assert.Equal(expected, LocalAccess.IsLocalMachine(IPAddress.Parse(address), () => Machine));

    [Fact]
    public void MissingAddressIsRejected() => Assert.False(LocalAccess.IsLocalMachine(null, () => Machine));

    [Theory]
    [InlineData("http://127.0.0.1:5187", "http", 5187, true)]
    [InlineData("http://localhost:5187", "http", 5187, true)]
    [InlineData("https://desktop-lr7l5jt:5188", "https", 5188, true)]
    [InlineData("https://DESKTOP-LR7L5JT:5188", "https", 5188, true)]
    [InlineData("https://desktop-lr7l5jt", "https", null, true)]
    [InlineData("https://desktop-lr7l5jt:5188", "http", 5188, false)]
    [InlineData("https://desktop-lr7l5jt:5000", "https", 5188, false)]
    [InlineData("https://evil.example:5188", "https", 5188, false)]
    [InlineData("null", "https", 5188, false)]
    public void OnlySameMachineOriginsMayPost(string origin, string scheme, int? port, bool expected) =>
        Assert.Equal(expected, LocalAccess.IsTrustedOrigin(origin, scheme, port, "DESKTOP-LR7L5JT"));
}
