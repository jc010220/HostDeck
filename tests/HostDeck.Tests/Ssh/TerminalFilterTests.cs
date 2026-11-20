using HostDeck.Protocols.Ssh;

namespace HostDeck.Tests.Ssh;

public class OscParserTests
{
    [Theory]
    [InlineData("\u001b]7;file://web01/var/log\u0007", "/var/log")]
    [InlineData("\u001b]7;file://web01/home/a%20b\u001b\\", "/home/a b")]
    [InlineData("prompt\u001b]7;file:///tmp\u0007$ ", "/tmp")]
    public void Extracts_directory(string data, string expected)
    {
        Assert.Equal([expected], new OscWorkingDirectoryParser().Feed(data));
    }

    [Fact]
    public void Handles_sequence_split_across_chunks()
    {
        var p = new OscWorkingDirectoryParser();
        Assert.Empty(p.Feed("abc\u001b"));
        Assert.Empty(p.Feed("]7;file://h/et"));
        Assert.Equal(["/etc"], p.Feed("c\u001b\\rest"));
    }

    [Fact]
    public void Ignores_other_osc_and_csi()
    {
        var p = new OscWorkingDirectoryParser();
        Assert.Empty(p.Feed("\u001b]0;window title\u0007\u001b[31mred\u001b[0m"));
    }
}

public class TerminalHighlighterTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void Colors_errors_warnings_success_and_ips()
    {
        var h = new TerminalHighlighter();
        var result = h.Process("ERROR: connection to 10.0.0.1 failed, warning ignored, OK\r\n");
        Assert.Contains($"{Esc}[91mERROR{Esc}[39m", result);
        Assert.Contains($"{Esc}[96m10.0.0.1{Esc}[39m", result);
        Assert.Contains($"{Esc}[91mfailed{Esc}[39m", result);
        Assert.Contains($"{Esc}[93mwarning{Esc}[39m", result);
        Assert.Contains($"{Esc}[92mOK{Esc}[39m", result);
    }

    [Fact]
    public void Does_not_touch_text_already_colored_by_program()
    {
        var h = new TerminalHighlighter();
        var input = $"{Esc}[32merror in green{Esc}[0m then error";
        var result = h.Process(input);
        Assert.StartsWith($"{Esc}[32merror in green{Esc}[0m", result);       // vom Programm gefärbt ⇒ unverändert
        Assert.EndsWith($"then {Esc}[91merror{Esc}[39m", result);            // danach wieder Standardfarbe ⇒ färben
    }

    [Fact]
    public void Never_splits_escape_sequences_across_chunks()
    {
        var h = new TerminalHighlighter();
        var a = h.Process("abc " + Esc + "[3");
        var b = h.Process("1mfailed" + Esc + "[0m failed");
        Assert.Equal("abc ", a);
        Assert.Equal($"{Esc}[31mfailed{Esc}[0m {Esc}[91mfailed{Esc}[39m", b);
    }

    [Fact]
    public void Leaves_fullscreen_programs_alone()
    {
        var h = new TerminalHighlighter();
        var r = h.Process($"{Esc}[?1049herror{Esc}[?1049l error");
        Assert.Equal($"{Esc}[?1049herror{Esc}[?1049l {Esc}[91merror{Esc}[39m", r);
    }

    [Fact]
    public void Does_not_match_inside_words_or_invalid_ips()
    {
        var h = new TerminalHighlighter();
        Assert.Equal("terrors okay 999.1.1.1", h.Process("terrors okay 999.1.1.1"));
    }

    [Fact]
    public void Can_be_disabled()
    {
        var h = new TerminalHighlighter { Enabled = false };
        Assert.Equal("error", h.Process("error"));
    }
}
