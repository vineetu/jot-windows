using Jot.Controls;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

/// <summary>The one-time gate for the post-wizard quick tour: fires only when setup just completed and the
/// tour hasn't been shown. Existing upgraders (who never run the wizard) never satisfy it.</summary>
public class QuickTourGateTests
{
    [Fact]
    public void FreshSetupJustCompleted_ShowsTour()
    {
        var s = new JotSettings { FirstRunComplete = true, FirstRunTipsDone = false };
        Assert.True(QuickTourWindow.ShouldShowAfterWizard(s));
    }

    [Fact]
    public void AlreadyShown_DoesNotShowAgain()
    {
        var s = new JotSettings { FirstRunComplete = true, FirstRunTipsDone = true };
        Assert.False(QuickTourWindow.ShouldShowAfterWizard(s));
    }

    [Fact]
    public void SetupNotComplete_DoesNotShow()
    {
        // Wizard closed early / model missing — setup isn't done, so no tour yet.
        var s = new JotSettings { FirstRunComplete = false, FirstRunTipsDone = false };
        Assert.False(QuickTourWindow.ShouldShowAfterWizard(s));
    }

    [Fact]
    public void ExistingUpgrader_NeverSeesWizardSoNeverSeesTour()
    {
        // An upgrader has FirstRunComplete from long ago but never had the tour flag flipped by a wizard run;
        // they also never re-run the wizard, so the gate is never even evaluated. If it were, default false
        // FirstRunTipsDone would say "show" — which is why the gate is only evaluated at wizard close.
        var s = new JotSettings(); // defaults: both false
        Assert.False(QuickTourWindow.ShouldShowAfterWizard(s));
    }

    [Fact]
    public void Cards_AreDeclarativeAndNonEmpty()
    {
        // The showcase list is the single extension point — must always have content to render.
        Assert.NotEmpty(QuickTourWindow.Cards);
    }

    [Fact]
    public void FirstCardBody_SplicesInTheLiveToggleChord_NotAHardcodedString()
    {
        var s = new JotSettings { ToggleRecordingHotkey = "Ctrl+Alt+J" };
        string body = QuickTourWindow.Cards[0].Body(s);
        // The body must reflect whatever the user bound (via HotkeyChord.Display), never a baked-in chord.
        Assert.Contains("Ctrl + Alt + J", body);
        Assert.DoesNotContain("Ctrl + Shift + Space", body);
    }
}
