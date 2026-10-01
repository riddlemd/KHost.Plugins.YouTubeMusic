using KHost.Plugins.YouTubeMusic.Helper;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public class HelperProtocolTests
{
    // main.swift reads "id", "cmd", "value", "url" and "home"; anything absent is left out entirely.
    [Fact]
    public void EncodeRequest_BareCommand_CarriesOnlyIdAndCommand()
        => Assert.Equal("""{"id":7,"cmd":"state"}""", HelperProtocol.EncodeRequest(7, HelperCommand.State));

    [Fact]
    public void EncodeRequest_WithArguments_CarriesThemUnderTheirWireNames()
    {
        Assert.Equal("""{"id":1,"cmd":"level","value":0.25}""", HelperProtocol.EncodeRequest(1, HelperCommand.Level, value: 0.25));
        Assert.Equal("""{"id":2,"cmd":"show","home":true}""", HelperProtocol.EncodeRequest(2, HelperCommand.Show, home: true));
    }

    // A playlist link is data: it must arrive as one JSON string, whatever it holds.
    [Fact]
    public void EncodeRequest_UrlWithQuotesAndNewline_StaysOneLineOfJson()
    {
        var line = HelperProtocol.EncodeRequest(3, HelperCommand.Load, url: "https://music.youtube.com/watch?list=a\"b\nc");

        Assert.DoesNotContain('\n', line);
        Assert.Equal("https://music.youtube.com/watch?list=a\"b\nc",
            System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public void Decode_Hello_ReadsWhoAndWhich()
    {
        var message = HelperProtocol.Decode("""{"version":"1.0.0","pid":42,"event":"hello","owner":"user","protocol":1}""");

        Assert.Equal(HelperProtocol.HelloEvent, message!.Event);
        Assert.Equal(new HelperHello(1, "1.0.0", "user", 42), message.Hello);
        Assert.Null(message.Reply);
    }

    // A hello with fields of the wrong type is still a hello: the connection must not drop over it.
    [Fact]
    public void Decode_HelloWithOddFields_ReadsWhatItCan()
        => Assert.Equal(new HelperHello(0, null, null, 0), HelperProtocol.Decode("""{"event":"hello","protocol":"1","pid":"x","version":2}""")!.Hello);

    [Fact]
    public void Decode_Busy_IsAnEventWithNoHello()
    {
        var message = HelperProtocol.Decode("""{"event":"busy"}""");

        Assert.Equal(HelperProtocol.BusyEvent, message!.Event);
        Assert.Null(message.Hello);
    }

    [Fact]
    public void Decode_Reply_CarriesIdOutcomeAndThePagesAnswer()
    {
        var reply = HelperProtocol.Decode("""{"id":9,"ok":true,"result":{"page":true,"ok":true}}""")!.Reply!;

        Assert.Equal(9, reply.Id);
        Assert.True(reply.Ok);
        Assert.True(reply.Result!.Value.GetProperty("page").GetBoolean());
    }

    [Fact]
    public void Decode_Refusal_CarriesTheError()
    {
        var reply = HelperProtocol.Decode("""{"id":3,"ok":false,"error":"url must be https://music.youtube.com/..."}""")!.Reply!;

        Assert.False(reply.Ok);
        Assert.Equal("url must be https://music.youtube.com/...", reply.Error);
        Assert.Null(reply.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-01 WebKit chatter")]
    [InlineData("[1]")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"id":"x","ok":true}""")]
    [InlineData("""{"id":1.5,"ok":true}""")]
    public void Decode_NotAMessage_IsNull(string? line)
        => Assert.Null(HelperProtocol.Decode(line));
}
