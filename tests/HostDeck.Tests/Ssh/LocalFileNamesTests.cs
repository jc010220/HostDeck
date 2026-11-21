using HostDeck.Protocols.Ssh;

namespace HostDeck.Tests.Ssh;

public class LocalFileNamesTests
{
    [Theory]
    [InlineData(@"..\..\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\evil.bat", "evil.bat")]
    [InlineData("../../.bashrc", ".bashrc")]
    [InlineData("..", "download")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("a:b*c?.log", "a_b_c_.log")]
    [InlineData("report.pdf. . ", "report.pdf")]
    [InlineData("line\nbreak.txt", "line_break.txt")]
    public void Server_file_names_cannot_escape_the_target_folder(string remote, string expected)
        => Assert.Equal(expected, LocalFileNames.Sanitize(remote));

    [Fact]
    public void Existing_files_are_never_overwritten()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "log.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "log (1).txt"), "x");
            Assert.Equal(Path.Combine(dir, "log (2).txt"), LocalFileNames.UniqueTarget(dir, "log.txt"));
            Assert.Throws<InvalidOperationException>(() => LocalFileNames.UniqueTarget(dir, "../x.txt"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
