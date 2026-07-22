using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jot.Models;
using Jot.Services;
using Jot.ViewModels;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The prompt-augment step: needs-input prompts (Translate → which language) collect a detail before running.
/// Covers the model helpers, that the bundled library flags Translate, and the picker's pick → augment → run flow.
/// </summary>
public sealed class PromptAugmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jot-augtest-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    [Fact]
    public void NeedsInput_TrueOnlyWhenAugmentLabelSet()
    {
        Assert.True(new PromptItem { AugmentLabel = "Which language?" }.NeedsInput);
        Assert.False(new PromptItem { AugmentLabel = null }.NeedsInput);
        Assert.False(new PromptItem { AugmentLabel = "   " }.NeedsInput);
    }

    [Fact]
    public void BuildInstruction_AppendsDetail_OrRunsPlainBodyWhenEmpty()
    {
        var p = new PromptItem { Body = "Translate the text into the language in the instruction." };
        Assert.Equal(p.Body, p.BuildInstruction(null));
        Assert.Equal(p.Body, p.BuildInstruction("   "));
        Assert.Equal(p.Body + "\n\nJapanese", p.BuildInstruction("  Japanese  "));
    }

    [Fact]
    public void BundledLibrary_FlagsTranslateAsNeedsInput_AndNotThePlainPrompts()
    {
        var catalog = new PromptCatalog(_dir);
        PromptItem translate = catalog.Prompts.First(p => p.Slug == "translate");
        Assert.True(translate.NeedsInput);
        Assert.Equal("Translate to which language?", translate.AugmentLabel);

        // A plain rewrite prompt must not trigger the input step.
        Assert.False(catalog.Prompts.First(p => p.Slug == "rewrite").NeedsInput);
        Assert.False(catalog.Prompts.First(p => p.Slug == "summarize").NeedsInput);
    }

    [Fact]
    public void Picker_PlainPrompt_RunsImmediately_WithNullDetail()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var vm = new PromptPickerViewModel(catalog);
            PromptItem? gotItem = null;
            string? gotDetail = "unset";
            vm.Picked += (item, detail) => { gotItem = item; gotDetail = detail; };

            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "rewrite"));

            Assert.False(vm.IsAugmenting);
            Assert.Equal("rewrite", gotItem!.Slug);
            Assert.Null(gotDetail);
        });
    }

    [Fact]
    public void Picker_NeedsInputPrompt_EntersAugmentStep_ThenRunsWithDetail()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var vm = new PromptPickerViewModel(catalog);
            var raised = new List<(string Slug, string? Detail)>();
            vm.Picked += (item, detail) => raised.Add((item.Slug, detail));

            // Picking Translate must NOT run yet — it opens the augment step.
            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));
            Assert.True(vm.IsAugmenting);
            Assert.Equal("Translate to which language?", vm.AugmentLabel);
            Assert.Empty(raised);

            // Supply the detail and confirm — now it runs, carrying the detail.
            vm.AugmentText = "Japanese";
            vm.ConfirmAugmentCommand.Execute(null);

            Assert.Single(raised);
            Assert.Equal("translate", raised[0].Slug);
            Assert.Equal("Japanese", raised[0].Detail);
        });
    }

    [Fact]
    public void Picker_CancelAugment_ReturnsToList_WithoutRunning()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var vm = new PromptPickerViewModel(catalog);
            bool ran = false;
            vm.Picked += (_, _) => ran = true;

            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));
            Assert.True(vm.IsAugmenting);

            vm.CancelAugmentCommand.Execute(null);

            Assert.False(vm.IsAugmenting);
            Assert.False(ran);
        });
    }

    [Fact]
    public void Picker_NeedsInputPrompt_AutoStartsTheMic()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var mic = new FakePhrase();
            var vm = new PromptPickerViewModel(catalog, mic);

            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));

            Assert.True(vm.IsAugmenting);
            Assert.True(vm.IsListening);          // mic armed without a button press
            Assert.Equal(1, mic.StartCount);
        });
    }

    [Fact]
    public void Picker_LiveCaption_StreamsIntoTheField()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var mic = new FakePhrase();
            var vm = new PromptPickerViewModel(catalog, mic);
            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));

            mic.RaisePartial("Japan");
            Assert.Equal("Japan", vm.AugmentText);
        });
    }

    [Fact]
    public void Picker_TypingWhileListening_TakesOver_AndKeepsTypedText()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var mic = new FakePhrase { StopText = "SHOULD-NOT-OVERWRITE" };
            var vm = new PromptPickerViewModel(catalog, mic);
            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));
            Assert.True(vm.IsListening);

            vm.AugmentText = "Spanish";   // simulate the user typing over the caption

            Assert.False(vm.IsListening);            // mic handed off
            Assert.Equal(1, mic.StopCount);
            Assert.Equal("Spanish", vm.AugmentText); // typed text preserved, NOT replaced by the transcription
        });
    }

    [Fact]
    public void Picker_Enter_AdoptsTheSpokenTranscription_AndRuns()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var mic = new FakePhrase { StopText = "Japanese" };
            var vm = new PromptPickerViewModel(catalog, mic);
            (string Slug, string? Detail)? raised = null;
            vm.Picked += (item, detail) => raised = (item.Slug, detail);

            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));
            Assert.True(vm.IsListening);

            vm.ConfirmAugmentCommand.Execute(null);   // Enter mid-speech

            Assert.False(vm.IsListening);
            Assert.NotNull(raised);
            Assert.Equal("translate", raised!.Value.Slug);
            Assert.Equal("Japanese", raised.Value.Detail); // adopted from the mic
        });
    }

    [Fact]
    public void Picker_EscStop_StopsMic_ButStaysInAugmentStep()
    {
        RunSta(() =>
        {
            var catalog = new PromptCatalog(_dir);
            var mic = new FakePhrase();
            var vm = new PromptPickerViewModel(catalog, mic);
            bool ran = false;
            vm.Picked += (_, _) => ran = true;
            vm.PickCommand.Execute(catalog.Prompts.First(p => p.Slug == "translate"));
            Assert.True(vm.IsListening);

            vm.StopSpeakingCommand.Execute(null);

            Assert.False(vm.IsListening);   // mic stopped
            Assert.True(vm.IsAugmenting);   // ...but still collecting input
            Assert.False(ran);              // nothing ran yet
        });
    }

    // Stand-in for the real recorder+engine so the auto-mic/takeover state machine is testable headlessly.
    private sealed class FakePhrase : IPhraseDictation
    {
        public event Action<string>? Partial;
        public bool StartResult = true;
        public int StartCount;
        public int StopCount;
        public string StopText = "";
        public bool Start() { StartCount++; return StartResult; }
        public Task<string> StopAsync() { StopCount++; return Task.FromResult(StopText); }
        public void RaisePartial(string text) => Partial?.Invoke(text);
    }

    // CollectionViewSource (built in the picker VM ctor) has WPF thread affinity — run on a single STA thread.
    private static void RunSta(Action f)
    {
        Exception? error = null;
        var t = new Thread(() => { try { f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }
}
