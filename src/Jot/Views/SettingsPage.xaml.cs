using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Jot.Controls;
using Jot.Services.Abstractions;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        DataContext = vm;

        // The API-key PasswordBox can't data-bind, so mirror the VM value into it — on load and
        // whenever it changes (e.g. switching providers loads that provider's saved key).
        SyncKeyBox(vm);
        vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(SettingsViewModel.AiApiKey)) SyncKeyBox(vm);
        };

        // Each time Settings opens, re-read the saved key from the store and push it into the box, so it
        // shows even when the singleton VM was seeded stale or the box missed the first sync (bug: key
        // was blank until a provider round-trip). Loaded fires on every navigation to the page.
        Loaded += (_, _) => { vm.RefreshApiKey(); SyncKeyBox(vm); };

        // Page height/scrolling is handled centrally by the shell (MainWindow applies FillHeight on
        // navigation), so no per-page scroll plumbing here.
    }

    // API key box (masked native PasswordBox + revealed TextBox + eye toggle)
    // These live inside SettingRow's own namescope, so they can't take an x:Name — each is captured
    // from its Loaded event. The native PasswordBox is used because Wpf.Ui.Controls.PasswordBox does
    // not render programmatic Password sets (broke prefill-on-launch and clear-on-provider-switch).
    private System.Windows.Controls.PasswordBox? _keyMasked;
    private Wpf.Ui.Controls.TextBox? _keyPlain;
    private Wpf.Ui.Controls.Button? _keyEye;
    private bool _keyRevealed;
    // Guards against the changed-event → VM → changed-event feedback loop while we push a value in.
    private bool _syncingKey;

    private void OnKeyMaskedLoaded(object sender, RoutedEventArgs e)
    {
        _keyMasked = sender as System.Windows.Controls.PasswordBox;
        if (DataContext is SettingsViewModel vm) SyncKeyBox(vm);
    }

    private void OnKeyPlainLoaded(object sender, RoutedEventArgs e)
    {
        _keyPlain = sender as Wpf.Ui.Controls.TextBox;
        if (DataContext is SettingsViewModel vm) SyncKeyBox(vm);
    }

    private void OnKeyEyeLoaded(object sender, RoutedEventArgs e)
    {
        _keyEye = sender as Wpf.Ui.Controls.Button;
        UpdateEye();
    }

    private void OnKeyMaskedChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKey) return;
        if (DataContext is SettingsViewModel vm && _keyMasked is not null)
            vm.AiApiKey = _keyMasked.Password;
    }

    private void OnKeyPlainChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingKey) return;
        if (DataContext is SettingsViewModel vm && _keyPlain is not null)
            vm.AiApiKey = _keyPlain.Text;
    }

    private void OnToggleKeyReveal(object sender, RoutedEventArgs e)
    {
        _keyRevealed = !_keyRevealed;
        if (_keyMasked is not null) _keyMasked.Visibility = _keyRevealed ? Visibility.Collapsed : Visibility.Visible;
        if (_keyPlain is not null) _keyPlain.Visibility = _keyRevealed ? Visibility.Visible : Visibility.Collapsed;
        if (DataContext is SettingsViewModel vm) SyncKeyBox(vm); // make sure the now-visible box holds the value
        UpdateEye();
    }

    private void UpdateEye()
    {
        if (_keyEye is null) return;
        // Icon set from the compile-checked enum (XAML symbol names are only validated at runtime).
        _keyEye.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = _keyRevealed ? Wpf.Ui.Controls.SymbolRegular.EyeOff24 : Wpf.Ui.Controls.SymbolRegular.Eye24,
        };
        _keyEye.ToolTip = _keyRevealed ? "Hide key" : "Show key";
    }

    /// <summary>Push the VM's key into both boxes (masked + plain) so whichever is visible shows it.</summary>
    private void SyncKeyBox(SettingsViewModel vm)
    {
        string value = vm.AiApiKey ?? "";
        _syncingKey = true;
        try
        {
            if (_keyMasked is not null && _keyMasked.Password != value) _keyMasked.Password = value;
            if (_keyPlain is not null && _keyPlain.Text != value) _keyPlain.Text = value;
        }
        finally { _syncingKey = false; }
    }

    // Filters the rows in-place: non-matching rows collapse, matching ones show, and a section
    // header is hidden when nothing under it matches. Advanced rows are searchable even when the
    // "Show advanced features" panel is collapsed, so a query can surface anything on the page.

    // Remembers the original Visibility binding (or none) of every element we override, so clearing
    // the search restores the element's own feature-gating binding instead of clobbering it.
    private readonly Dictionary<FrameworkElement, Binding?> _savedVisibility = new();

    // Ordered section headers + rows, captured once. The page's structure is static (only per-row
    // visibility changes at runtime), so re-walking each keystroke would be wasteful — and would trip
    // over rows we ourselves just collapsed. Cache it on first use instead.
    private List<FrameworkElement>? _searchIndex;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        string query = (SearchBox.Text ?? "").Trim();
        bool searching = query.Length > 0;

        // Reveal the advanced pane while searching so matching advanced rows can render; otherwise
        // hand its Visibility back to the AdvancedFeatures binding.
        if (searching) ForceVisible(AdvancedPanel); else RestoreVisibility(AdvancedPanel);
        // The AI intro banner is guidance, not a setting — hide it while filtering.
        AiInfoBar.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;

        if (_searchIndex is null)
        {
            _searchIndex = new List<FrameworkElement>();
            Collect(RootScroll, _searchIndex);
        }
        var flat = _searchIndex;

        // Group rows under the section header that precedes them (document order).
        FrameworkElement? header = null;
        var rows = new List<SettingRow>();
        void FlushSection()
        {
            if (header is null) return;
            bool headerMatch = searching && Contains(HeaderText(header), query);
            bool anyVisible = false;
            foreach (var row in rows)
            {
                bool show = !searching || headerMatch
                            || Contains(row.Title, query) || Contains(row.Description, query);
                if (show) RestoreVisibility(row); else Hide(row);
                if (row.Visibility == Visibility.Visible) anyVisible = true;
            }
            header.Visibility = (!searching || headerMatch || anyVisible)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var el in flat)
        {
            if ((el.Tag as string) == "section")
            {
                FlushSection();
                header = el;
                rows.Clear();
            }
            else if (el is SettingRow row)
            {
                rows.Add(row);
            }
        }
        FlushSection();
    }

    // Depth-first, document-order walk that stops at section headers and SettingRows (the two things
    // search cares about) and recurses through every other container. Subtrees that are collapsed by
    // design (a literal Visibility="Collapsed", no binding — e.g. the experimental Vocabulary block)
    // are skipped so their rows never leak into results; panes gated by a binding (the advanced pane,
    // the AI sub-panels) are still walked, since their gate can flip at runtime.
    private static void Collect(DependencyObject parent, List<FrameworkElement> flat)
    {
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is SettingRow row) { flat.Add(row); continue; }
            if (child is FrameworkElement fe)
            {
                if ((fe.Tag as string) == "section") { flat.Add(fe); continue; }
                if (fe.Visibility == Visibility.Collapsed
                    && fe.GetBindingExpression(VisibilityProperty) is null)
                    continue; // statically hidden by design — don't descend
            }
            Collect(child, flat);
        }
    }

    private static string HeaderText(FrameworkElement el) => el is TextBlock tb ? tb.Text : "";

    private static bool Contains(string? text, string query) =>
        !string.IsNullOrEmpty(text) && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void Hide(FrameworkElement el) => Override(el, Visibility.Collapsed);
    private void ForceVisible(FrameworkElement el) => Override(el, Visibility.Visible);

    private void Override(FrameworkElement el, Visibility value)
    {
        if (!_savedVisibility.ContainsKey(el))
            _savedVisibility[el] = el.GetBindingExpression(VisibilityProperty)?.ParentBinding;
        el.Visibility = value;
    }

    private void RestoreVisibility(FrameworkElement el)
    {
        if (!_savedVisibility.TryGetValue(el, out var binding)) return;
        if (binding is not null) el.SetBinding(VisibilityProperty, binding);
        else el.ClearValue(VisibilityProperty);
        _savedVisibility.Remove(el);
    }

    private void OnRunWizard(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Reset preferences and shortcut bindings to defaults? Your recordings and models are kept. Jot will restart.",
            "Reset settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        App.Services.GetRequiredService<Services.Abstractions.ISettingsStore>().Reset();
        Restart();
    }

    private void OnEraseData(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Erase all recordings, transcripts, the downloaded model, and settings? This can't be undone. Jot will restart.",
            "Erase all data", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        var settings = App.Services.GetRequiredService<Services.Abstractions.ISettingsStore>();
        App.Services.GetRequiredService<IRecordingStore>().Items.Clear(); // writes an empty library

        // Delete user data: recordings, library, prompts, model, encrypted key, and settings.
        var s = settings.Current;
        TryDeleteDir(Services.JotPaths.RecordingsDir(s));
        TryDeleteDir(Services.JotPaths.ModelsDir(s));
        TryDeleteFile(Services.JotPaths.LibraryFile(s));
        string appData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
        TryDeleteFile(System.IO.Path.Combine(appData, "prompts.json"));
        TryDeleteFile(System.IO.Path.Combine(Services.JotPaths.DataDir(s), "aikey.dat")); // now under the data folder
        TryDeleteFile(System.IO.Path.Combine(appData, "settings.json")); // last: back to first-run defaults
        Restart();
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch { }
    }

    // Releases the single-instance mutex before spawning the new process (see App.RestartApp) so the
    // relaunch after Reset / Erase isn't rejected as a duplicate.
    private static void Restart() => App.RestartApp();
}
