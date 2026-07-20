using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services.Abstractions;
using Jot.Services.Ai;

namespace Jot.ViewModels;

public sealed record ChatMessage(string Text, bool IsUser);

/// <summary>
/// Ask Jot chat pane: a help assistant grounded in Jot's feature set. Answers via the configured AI
/// provider, else concise built-in fallbacks. Never throws out to the UI.
/// </summary>
public sealed partial class AskJotViewModel : ObservableObject
{
    private readonly IAiClient _ai;
    private readonly ISettingsStore _settings;
    private readonly AiCredentials _credentials;

    /// <summary>Toggle shortcut as a display label, so help copy never hardcodes the chord.</summary>
    private string HotkeyLabel => Jot.Recording.HotkeyChord.Display(_settings.Current.ToggleRecordingHotkey);

    private string Grounding =>
        "You are Jot's built-in help assistant. Jot is an on-device dictation app for Windows. Facts:\n" +
        $"- Press {HotkeyLabel} (rebindable in Settings → Shortcuts) in any app to start/stop dictation; the transcript is pasted at the cursor.\n" +
        "- Transcription runs 100% on-device (NVIDIA Nemotron 3.5, 33 languages) — nothing is sent to the cloud for transcription.\n" +
        "- Live captions show a running transcript in the floating pill while you speak; press Esc while recording to cancel.\n" +
        "- Rewrite: select text and press the Rewrite shortcut to transform it with a prompt; 'Rewrite with voice' lets you speak the instruction. Manage prompts in the Prompts tab; pin favourites and set a default.\n" +
        "- Optional AI rewrite uses a provider configured in Settings → AI (OpenAI, Anthropic, Gemini, or local Ollama). This is the only feature that may contact an external service, and only when enabled.\n" +
        "- Recordings and transcripts are saved locally; audio is auto-pruned per the 'Keep audio' setting while transcripts are kept forever.\n" +
        "Answer the user's question about using Jot concisely and accurately from these facts. If unsure, say so and point to the Help tab. Keep answers short and practical.";

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string[] Starters { get; } =
    [
        "How do I dictate into any app?",
        "How do I set up an AI provider?",
        "How does Rewrite work?",
    ];

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isThinking;

    public bool IsEmpty => Messages.Count == 0;

    public AskJotViewModel(IAiClient ai, ISettingsStore settings, AiCredentials credentials)
    {
        _ai = ai;
        _settings = settings;
        _credentials = credentials;
    }

    [RelayCommand]
    private async Task Send()
    {
        string text = Input.Trim();
        if (text.Length == 0 || IsThinking) return;

        Messages.Add(new ChatMessage(text, IsUser: true));
        Input = "";
        OnPropertyChanged(nameof(IsEmpty));

        JotSettings s = _settings.Current;
        if (s.AiProvider == "None")
        {
            Messages.Add(new ChatMessage(
                Answer(text) + "\n\n(Configure a provider in Settings → AI for full conversational answers.)",
                IsUser: false));
            return;
        }

        IsThinking = true;
        try
        {
            var config = new AiConfig(s.AiProvider,
                string.IsNullOrWhiteSpace(s.AiBaseUrl) ? null : s.AiBaseUrl,
                string.IsNullOrWhiteSpace(s.AiModel) ? null : s.AiModel,
                _credentials.GetKey(s.AiProvider));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string reply = (await _ai.AskAsync(Grounding, text, config, cts.Token)).Trim();
            Messages.Add(new ChatMessage(reply.Length > 0 ? reply : Answer(text), IsUser: false));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage($"{Answer(text)}\n\n(Couldn't reach {s.AiProvider}: {ex.Message})", IsUser: false));
        }
        finally
        {
            IsThinking = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void UseStarter(string? starter)
    {
        if (starter is null) return;
        Input = starter;
        SendCommand.Execute(null);
    }

    [RelayCommand]
    private void NewChat()
    {
        Messages.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    // Offline / no-provider fallback answers.
    private string Answer(string question)
    {
        string q = question.ToLowerInvariant();
        if (q.Contains("dictat") || q.Contains("type") || q.Contains("speak"))
            return $"Press {HotkeyLabel} in any app, speak, and press it again to stop. Jot transcribes on your PC and pastes the text at your cursor.";
        if (q.Contains("clean") || q.Contains("ai") || q.Contains("provider"))
            return "Open Settings → AI, pick a provider (OpenAI, Anthropic, Gemini, or local Ollama), then turn on \"Clean up transcript with AI.\" It runs after each dictation and falls back to the raw text on any error.";
        if (q.Contains("rewrite"))
            return "Select text anywhere, press the Rewrite shortcut, and pick a prompt (or use Rewrite with voice to speak an instruction like \"make this more formal\"). The result replaces your selection.";
        return "I'm grounded in Jot's built-in help. Check the Help tab for Basics, Advanced, and Troubleshooting.";
    }
}
