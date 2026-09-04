using System.Linq;
using System.Reflection;
using Jot.ViewModels;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The members SettingsPage.xaml binds to, pinned by name.
///
/// WPF bindings fail SILENTLY: a renamed or mistyped path produces a dead control — a toggle that
/// never saves, a button that never fires, a status line that stays blank — with no exception and
/// nothing in the log. Nothing else in this suite would notice, because the XAML is not compiled
/// against the view-model's shape.
/// </summary>
public class SettingsBindingSurfaceTests
{
    [Theory]
    // Better punctuation (English): toggle, download state, download command.
    [InlineData("RestoreEnglishPunctuation")]
    [InlineData("PunctuationDownload")]
    [InlineData("DownloadPunctuationModelCommand")]
    // The neighbouring English-engine row, so a rename there is caught the same way.
    [InlineData("EnglishDownload")]
    [InlineData("DownloadEnglishModelCommand")]
    public void The_settings_page_binding_paths_exist(string member)
    {
        Assert.True(
            typeof(SettingsViewModel).GetMember(member, BindingFlags.Public | BindingFlags.Instance).Length > 0,
            $"SettingsPage.xaml binds {{Binding {member}}}, which SettingsViewModel no longer exposes — " +
            "the control is silently dead.");
    }

    /// <summary>Every download surface must be refreshed together; a row left out of the list shows
    /// a stale "Not installed" after a data-folder move.</summary>
    [Fact]
    public void Every_download_surface_is_exposed_for_binding()
    {
        string[] rows = ["Download", "VocabularyDownload", "EnglishDownload", "PunctuationDownload"];
        Assert.All(rows, r => Assert.NotNull(typeof(SettingsViewModel).GetProperty(r)));
    }
}
