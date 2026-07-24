using System.ComponentModel;
using System.Windows;
using System.Windows.Shapes;
using Jot.Controls;
using Jot.Services.Abstractions;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Jot.Views;

public partial class SetupWizardWindow : FluentWindow
{
    private readonly WizardViewModel _vm;
    private readonly MicMeter _meter = new();

    public SetupWizardWindow()
    {
        InitializeComponent();
        _vm = new WizardViewModel(
            App.Services.GetRequiredService<ISettingsStore>(),
            App.Services.GetRequiredService<Jot.Services.ModelDownload>());
        DataContext = _vm;
        _vm.CloseRequested += Close;
        _vm.PropertyChanged += OnVmPropertyChanged;

        BuildDots();
        UpdateDots();

        _meter.Level += lvl => Dispatcher.BeginInvoke(() => MeterBar.Value = lvl);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WizardViewModel.StepIndex)) return;
        UpdateDots();
        if (_vm.StepIndex == 4) _meter.Start();   // step 4 = "Choose your microphone"
        else _meter.Stop();
    }

    private void BuildDots()
    {
        for (int i = 0; i < WizardViewModel.StepCount; i++)
            Dots.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(4, 0, 4, 0) });
    }

    private void UpdateDots()
    {
        var active = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.Gray;
        var inactive = TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.DimGray;
        for (int i = 0; i < Dots.Children.Count; i++)
            ((Ellipse)Dots.Children[i]).Fill = i <= _vm.StepIndex ? active : inactive;
    }

    protected override void OnClosed(EventArgs e)
    {
        _meter.Dispose();
        _vm.Detach();   // drop the wizard's subscription to the singleton download so it can be collected

        // Closing via the title-bar X counts as completing first-run ONLY if the model actually made it
        // down. Without a model the app can't dictate, so we must NOT mark setup done — App startup re-shows
        // this wizard whenever the model is missing (see OnStartup's modelMissing check). With the model
        // present (the normal path), closing still counts as done so it doesn't nag again.
        var store = App.Services.GetRequiredService<ISettingsStore>();
        var transcriber = App.Services.GetRequiredService<Jot.Transcription.ITranscriber>();
        if (!store.Current.FirstRunComplete && transcriber.IsModelInstalled)
        {
            store.Current.FirstRunComplete = true;
            store.Save();
        }
        base.OnClosed(e);
    }
}
