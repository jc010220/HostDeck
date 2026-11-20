using System.Text;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Ssh;

/// <summary>End-to-End auf ViewModel-Ebene gegen den Test-SSH-Server (siehe SshIntegrationTests).</summary>
public class TerminalViewModelIntegrationTests
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH");
    private static string User => Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH_USER")!;
    private static string Password => Environment.GetEnvironmentVariable("HOSTDECK_TEST_SSH_PASSWORD")!;

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return condition();
    }

    private sealed class Capture
    {
        private readonly StringBuilder _sb = new();
        public void Add(string s) { lock (_sb) _sb.Append(s); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
    }

    [Fact]
    public async Task Quick_connect_multi_exec_and_sftp_follow()
    {
        Assert.SkipWhen(Target is null, "Kein Test-SSH-Server konfiguriert (HOSTDECK_TEST_SSH).");
        var (vm, dialogs, _, store) = Presentation.MainViewModelTests.Create();
        dialogs.PromptAnswer = msg => msg.Contains("assword", StringComparison.OrdinalIgnoreCase) ? Password : null;

        // Zwei SSH-Tabs per Quick-Connect (ohne gespeichertes Passwort ⇒ Abfrage über Dialog)
        vm.QuickConnectText = $"ssh {User}@{Target}";
        vm.QuickConnectCommand.Execute(null);
        var tab1 = vm.Workspace.ActiveTab!;
        vm.QuickConnectText = $"ssh {User}@{Target}";
        vm.QuickConnectCommand.Execute(null);
        var tab2 = vm.Workspace.ActiveTab!;

        Assert.NotNull(tab1.Terminal);
        Assert.True(await WaitUntil(() => tab1.State == SessionState.Connected && tab2.State == SessionState.Connected),
            $"Status: {tab1.State}/{tab2.State}");
        Assert.True(dialogs.HostKeyPrompts.Count >= 1);       // TOFU-Nachfrage
        Assert.Single(store.HostKeys);                          // gespeichert

        var out1 = new Capture();
        var out2 = new Capture();
        using var s1 = tab1.Terminal!.Subscribe(out1.Add);
        using var s2 = tab2.Terminal!.Subscribe(out2.Add);
        Assert.Contains("Verbinde mit", out1.Text);            // Verlauf wird nachgeliefert

        // Ohne Multi-Exec: nur Tab 1
        tab1.Terminal.OnUserInput("echo einzel-$((6*7))\r");
        Assert.True(await WaitUntil(() => out1.Text.Contains("einzel-42")), out1.Text);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("einzel-42", out2.Text);

        // Mit Multi-Exec: beide
        vm.IsMultiExecutionEnabled = true;
        tab1.Terminal.OnUserInput("echo multi-$((50+5))\r");
        Assert.True(await WaitUntil(() => out1.Text.Contains("multi-55") && out2.Text.Contains("multi-55")), out2.Text);

        // SFTP-Browser des aktiven Tabs folgt dem Terminal-Verzeichnis
        vm.IsMultiExecutionEnabled = false;
        Assert.True(await WaitUntil(() => vm.ActiveSftp is not null && vm.ActiveSftp.Path.Length > 0), "SFTP nicht bereit");
        tab2.Terminal.OnUserInput("cd /tmp\r");
        Assert.True(await WaitUntil(() => vm.ActiveSftp!.Path == "/tmp"), $"SFTP-Pfad: {vm.ActiveSftp!.Path}");

        // Tab schließen trennt die Verbindung
        vm.Workspace.Close(tab1);
        Assert.True(await WaitUntil(() => !tab1.Terminal.IsConnected));
        vm.Workspace.Close(tab2);
    }
}
