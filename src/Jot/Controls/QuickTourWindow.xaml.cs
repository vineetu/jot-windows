using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Jot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// Renders any named <see cref="Tour"/> (title + subhead + declarative card list) — the getting-started
/// essentials tour and every per-feature tour go through this one window. Dismissed with "Got it";
/// re-runnable from Recents/Help. Closing it (any way) marks that tour seen via <see cref="TourCatalog.MarkShown"/>
/// so it never nags again — getting-started via <see cref="JotSettings.FirstRunTipsDone"/>, the rest via
/// <see cref="JotSettings.ShownTours"/>.
///
/// The content lives in <see cref="TourCatalog"/>, not here: shipping a new tour is one entry there, with no
/// change to this window. The parameterless ctor shows getting-started (the long-standing first-run tour).
/// </summary>
public partial class QuickTourWindow : FluentWindow
{
    private readonly ISettingsStore _settings;
    private readonly Tour _tour;

    /// <summary>The getting-started cards, kept as a stable accessor for existing tests and callers.</summary>
    internal static IReadOnlyList<TourCard> Cards => TourCatalog.GettingStarted.Cards;

    /// <summary>The one place that decides whether the post-wizard tour fires: setup is complete and the
    /// tour hasn't been shown yet. Existing upgraders never run the wizard, so they never hit this true.</summary>
    internal static bool ShouldShowAfterWizard(JotSettings s) => s.FirstRunComplete && !s.FirstRunTipsDone;

    /// <summary>Shows the getting-started tour (the long-standing first-run / Recents / Help entry point).</summary>
    public QuickTourWindow() : this(TourCatalog.GettingStarted) { }

    internal QuickTourWindow(Tour tour)
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsStore>();
        _tour = tour;
        Title = tour.Title;
        WindowTitleBar.Title = tour.Title;
        HeadingText.Text = tour.Heading;
        SubheadText.Text = tour.Subhead;
        BuildCards();
    }

    // Render the declarative card list. A two-column grid (icon | text) bounds the text column so bodies wrap
    // instead of running off the fixed-width window.
    private void BuildCards()
    {
        foreach (TourCard card in _tour.Cards)
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

    // Any close (Got it or the title-bar X) marks THIS tour done — truly one-time. Idempotent, so re-opening
    // from Recents/Help/the Help hub just re-saves the same state.
    protected override void OnClosed(System.EventArgs e)
    {
        TourCatalog.MarkShown(_settings.Current, _tour.Id);
        _settings.Save();
        base.OnClosed(e);
    }
}
