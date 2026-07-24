using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Jot.Recording;
using Jot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>One card in the quick tour: an icon, a title, and a body built at show-time so it can splice in
/// live values (e.g. the user's real toggle chord). Adding a card for a future feature is a one-entry change
/// to <see cref="QuickTourWindow.Cards"/> — nothing else moves.</summary>
internal sealed record TourCard(SymbolRegular Icon, string Title, System.Func<JotSettings, string> Body);

/// <summary>
/// One-time "quick tour" shown right after the setup wizard closes with setup complete — a friendly,
/// zero-jargon nudge that teaches the essentials (toggle to dictate, the live pill + Esc, where to
/// customize). Dismissed with "Got it"; re-runnable from Recents/Help. The show-once state lives in settings
/// (<see cref="JotSettings.FirstRunTipsDone"/>); closing it (any way) marks it done so it never nags again.
///
/// The content is a declarative list (<see cref="Cards"/>) the window renders as-is — a small showcase system
/// meant to grow: drop a new <see cref="TourCard"/> in when a feature ships (vocabulary, prompts, rewrite…).
/// A future per-feature tour (its own named card list) would slot in beside this one; we deliberately keep to
/// a single list until that's actually needed.
/// </summary>
public partial class QuickTourWindow : FluentWindow
{
    private readonly ISettingsStore _settings;

    /// <summary>The tour's cards, in order. Bodies are factories so they read live settings when shown — never
    /// a hardcoded shortcut string. Extend by adding an entry; the window renders whatever's here.</summary>
    internal static readonly IReadOnlyList<TourCard> Cards = new[]
    {
        new TourCard(SymbolRegular.Keyboard24, "Start and stop dictating",
            s => $"Press {HotkeyChord.Display(s.ToggleRecordingHotkey)} anywhere to start, then press it again " +
                 "to stop — or click the Jot icon in your taskbar tray."),
        new TourCard(SymbolRegular.Mic24, "Watch it as you speak",
            _ => "A small floating pill shows your words appearing live. Press Esc anytime to stop and save what " +
                 "you've said."),
        new TourCard(SymbolRegular.Settings24, "Make it yours",
            _ => "Change your shortcuts anytime on the Shortcuts page — you can even set a key to hold down while " +
                 "you talk. Pick your language and AI helper in Settings."),
        // Future feature cards slot in here (e.g. custom vocabulary when it ships) — one entry, no window changes.
    };

    /// <summary>The one place that decides whether the post-wizard tour fires: setup is complete and the
    /// tour hasn't been shown yet. Existing upgraders never run the wizard, so they never hit this true.</summary>
    internal static bool ShouldShowAfterWizard(JotSettings s) => s.FirstRunComplete && !s.FirstRunTipsDone;

    public QuickTourWindow()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsStore>();
        BuildCards();
    }

    // Render the declarative card list. A two-column grid (icon | text) bounds the text column so bodies wrap
    // instead of running off the fixed-width window.
    private void BuildCards()
    {
        foreach (TourCard card in Cards)
        {
            var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new SymbolIcon
            {
                Symbol = card.Icon,
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush"),
            };
            Grid.SetColumn(icon, 0);

            var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            text.Children.Add(new System.Windows.Controls.TextBlock { Text = card.Title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = card.Body(_settings.Current),
                TextWrapping = TextWrapping.Wrap, // the grid's star column bounds the width, so long bodies wrap
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
            });
            Grid.SetColumn(text, 1);

            grid.Children.Add(icon);
            grid.Children.Add(text);
            CardHost.Children.Add(grid);
        }
    }

    private void OnGotIt(object sender, RoutedEventArgs e) => Close();

    // Any close (Got it or the title-bar X) marks the tour done — truly one-time. Idempotent, so re-opening
    // from Recents/Help just re-saves the same flag.
    protected override void OnClosed(System.EventArgs e)
    {
        _settings.Current.FirstRunTipsDone = true;
        _settings.Save();
        base.OnClosed(e);
    }
}
