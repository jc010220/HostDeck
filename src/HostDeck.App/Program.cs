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

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
