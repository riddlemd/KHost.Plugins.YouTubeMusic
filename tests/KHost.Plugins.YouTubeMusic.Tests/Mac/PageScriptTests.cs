using KHost.Plugins.YouTubeMusic.Mac;

namespace KHost.Plugins.YouTubeMusic.Tests.Mac;

public class PageScriptTests
{
    [Fact]
    public void Compose_SplicesTheArgumentsInPlaceOfTheMarker()
        => Assert.Equal(
            "(function(c,a,o){})(\"level\", 0.25, true);",
            PageScript.Compose("(function(c,a,o){})/*ARGUMENTS*/;", PageCommand.Level, 0.25, appOnly: true));

    // A playlist link comes from a setting; quotes in it must stay inside the string literal.
    [Fact]
    public void Compose_StringArgument_IsAJsonLiteralThatCannotBreakOut()
    {
        var script = PageScript.Compose("f/*ARGUMENTS*/", PageCommand.Navigate, "https://music.youtube.com/\");alert(1);//", appOnly: false);

        Assert.Equal("f(\"navigate\", \"https://music.youtube.com/\\u0022);alert(1);//\", false)", script);
    }

    [Fact]
    public void Compose_NoArgument_PassesNull()
        => Assert.Equal("f(\"state\", null, false)", PageScript.Compose("f/*ARGUMENTS*/", PageCommand.State, null, appOnly: false));

    [Fact]
    public void Compose_TemplateWithoutMarker_Throws()
        => Assert.Throws<InvalidOperationException>(() => PageScript.Compose("f()", PageCommand.State, null, false));

    [Fact]
    public void For_UsesTheEmbeddedPageScript()
    {
        var script = PageScript.For(PageCommand.Next, appOnly: true);

        Assert.Contains("music.youtube.com", script);
        // The keepalive only works from the page's own world; the isolated one has its own window.
        Assert.Contains("if (world === 'main') window._lact = Date.now();", script);
        Assert.EndsWith("(\"next\", null, true);", script.TrimEnd());
        Assert.DoesNotContain("/*ARGUMENTS*/", script);
    }

    // Each name crosses into the page as a string; one the script does not know does nothing.
    [Theory]
    [InlineData(PageCommand.State)]
    [InlineData(PageCommand.Play)]
    [InlineData(PageCommand.Pause)]
    [InlineData(PageCommand.Next)]
    [InlineData(PageCommand.Level)]
    [InlineData(PageCommand.KeepAlive)]
    [InlineData(PageCommand.Navigate)]
    public void For_EveryCommand_IsOneTheScriptAnswers(string command)
        => Assert.Contains($"case '{command}':", PageScript.For(command));
}
