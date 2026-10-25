using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class SteamRequestGateTests
{
    [Fact]
    public async Task Grant_Echo_Must_Match_Current_Request_Before_Consumption()
    {
        var identifier = new string('a', 32);
        var output = new StringWriter();
        await SteamRequestGate.ExchangeAsync(identifier, output,
            new StringReader($"{SteamRequestGate.Granted} {identifier}\n"));
        Assert.Equal($"{SteamRequestGate.Request} {identifier}{Environment.NewLine}"
            + $"{SteamRequestGate.Consumed} {identifier}{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unsolicited")]
    [InlineData("missing")]
    public async Task Wrong_Or_Missing_Grant_Is_Native_Failure_Even_Before_First_Call(string kind)
    {
        var reply = kind switch
        {
            "stale" => $"{SteamRequestGate.Granted} {new string('b', 32)}\n",
            "unsolicited" => "unsolicited line\n",
            _ => ""
        };
        var output = new StringWriter();
        var error = await Assert.ThrowsAsync<SteamProtocolException>(() =>
            SteamRequestGate.ExchangeAsync(new string('a', 32), output, new StringReader(reply)));
        Assert.DoesNotContain(SteamRequestGate.Consumed, output.ToString());
        Assert.False(SteamRequestGate.IsLocalRefusal(error, 0));
        Assert.False(SteamRequestGate.IsLocalRefusal(error, 1));
    }

    [Fact]
    public async Task Banked_Duplicate_Grant_Cannot_Authorize_Later_Submit()
    {
        var first = new string('a', 32);
        var second = new string('b', 32);
        var input = new StringReader($"{SteamRequestGate.Granted} {first}\n"
            + $"{SteamRequestGate.Granted} {first}\n");
        var output = new StringWriter();
        var outbound = new List<string>();
        await SteamRequestGate.ExchangeAsync(first, output, input);
        outbound.Add("query");
        var error = await Assert.ThrowsAsync<SteamProtocolException>(async () =>
        {
            await SteamRequestGate.ExchangeAsync(second, output, input);
            outbound.Add("submit");
        });
        Assert.Equal(["query"], outbound);
        Assert.DoesNotContain($"{SteamRequestGate.Consumed} {second}", output.ToString());
        Assert.False(SteamRequestGate.IsLocalRefusal(error, 1));
    }
}
