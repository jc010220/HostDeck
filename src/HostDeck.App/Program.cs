using Velopack;

namespace HostDeck.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack: Installations-/Update-/Deinstallations-Hooks (beendet den Prozess in diesen Fällen selbst).
        // Ohne Installation (portable Start, Entwicklung) ist das ein No-op.
        VelopackApp.Build().Run();

        // Fehler in eingebetteten WinForms-/ActiveX-Controls (RDP) nicht am WPF-Handler vorbei den Prozess beenden lassen
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Services.Log.Error("WinForms/ActiveX", e.Exception);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
