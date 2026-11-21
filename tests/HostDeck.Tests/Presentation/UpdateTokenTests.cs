using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class UpdateTokenTests
{
    [Fact]
    public void Token_from_settings_is_stored_encrypted_and_never_prefilled()
    {
        var (vm, dialogs, settings, store) = MainViewModelTests.Create();
        var applied = 0;
        vm.SettingsApplied += () => applied++;

        dialogs.SettingsAnswer = s =>
        {
            Assert.False(s.HasUpdateToken);
            s.UpdateSource = "https://github.com/jc010220/HostDeck";
            s.UpdateToken = "  github_pat_test  ";
            return true;
        };
        vm.OpenSettingsCommand.Execute(null);

        Assert.Equal("github_pat_test", store.Documents[SettingsViewModel.UpdateTokenDocument]);
        Assert.Equal("https://github.com/jc010220/HostDeck", settings.Stored.UpdateSource);
        Assert.Equal(1, applied);

        // erneut öffnen: Token als vorhanden gemeldet, aber nicht im Klartext vorbefüllt; leer lassen = behalten
        dialogs.SettingsAnswer = s =>
        {
            Assert.True(s.HasUpdateToken);
            Assert.Equal("", s.UpdateToken);
            return true;
        };
        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal("github_pat_test", store.Documents[SettingsViewModel.UpdateTokenDocument]);
    }
}
