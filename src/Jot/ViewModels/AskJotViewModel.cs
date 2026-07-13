using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Jot.ViewModels;

public sealed record ChatMessage(string Text, bool IsUser);

/// <summary>
/// Ask Jot chat pane. Grounded, streaming answers arrive once an LLM path is wired; for now it
/// returns a canned, honest response so the whole surface (bubbles, starters, new chat) is real.
/// </summary>
public sealed partial class AskJotViewModel : ObservableObject
{
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string[] Starters { get; } =
    [
        "How do I dictate into any app?",
        "How do I set up AI cleanup?",
        "How does Rewrite work?",
    ];

    [ObservableProperty] private string _input = "";

    public bool IsEmpty => Messages.Count == 0;

    [RelayCommand]
    private void Send()
    {
        string text = Input.Trim();
        if (text.Length == 0) return;

        Messages.Add(new ChatMessage(text, IsUser: true));
        Input = "";
        Messages.Add(new ChatMessage(Answer(text), IsUser: false));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void UseStarter(string? starter)
    {
        if (starter is null) return;
        Input = starter;
        Send();
    }

    [RelayCommand]
    private void NewChat()
    {
        Messages.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Answer(string question)
    {
        string q = question.ToLowerInvariant();
        if (q.Contains("dictat") || q.Contains("type") || q.Contains("speak"))
            return "Press Alt + Space in any app, speak, and press it again to stop. Jot transcribes on your PC and pastes the text at your cursor.";
        if (q.Contains("clean") || q.Contains("ai") || q.Contains("provider"))
            return "Open Settings → AI, pick a provider (OpenAI, Anthropic, Gemini, or local Ollama), then turn on \"Clean up transcript with AI.\" It runs after each dictation and falls back to the raw text on any error.";
        if (q.Contains("rewrite"))
            return "Select text anywhere, press the Rewrite shortcut, and speak an instruction like \"make this more formal.\" The result replaces your selection.";
        return "I'm grounded in Jot's built-in help. Full conversational answers arrive once an AI provider is wired — for now, check the Help tab for Basics, Advanced, and Troubleshooting.";
    }
}
