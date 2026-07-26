using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The ONE thing about the shipping spotter that needs the real checkpoint AND a real ONNX session:
/// term validation must give the same answer no matter what else the spotter is doing.
///
/// The defect this pins, observed 2026-07-25: typing <c>Wi-Fi</c> into the add form correctly warned
/// "Jot can't listen for this", while the saved <c>Wi-Fi</c> row directly above it showed no warning
/// at all — same term, same instant, two answers. <c>CheckTerm</c> took the SESSION lock with
/// <c>TryEnter</c> and returned <c>Unknown</c> (⇒ no warning, indistinguishable from "no model")
/// whenever it lost the race, and startup's <c>Warm</c> holds that lock for 2–3 s loading the
/// DirectML session — exactly while the page's view-model builds its rows.
///
/// Skipped honestly without the installed model. This is deliberately the model the shipping app
/// resolves, no env-var override: the point is what the owner's machine does.
/// </summary>
public class CtcVocabularySpotterValidationTests(ITestOutputHelper output)
{
    private static CtcVocabularySpotter Installed() =>
        new(new CtcModel(), new OnnxSessionFactory());

    /// <summary>Cold, before anything has loaded a session: validation must still answer, because it
    /// only ever needed the 262 KB of text artifacts.</summary>
    [ModelFact("installed-ctc")]
    public void CheckTerm_AnswersFromAColdSpotter_WithoutLoadingTheSession()
    {
        using CtcVocabularySpotter s = Installed();
        Assert.Equal(TermSpottability.Unsupported, s.CheckTerm("Wi-Fi"));
        Assert.Equal(TermSpottability.Ok, s.CheckTerm("Nemotron"));
        Assert.Equal(TermSpottability.TooShort, s.CheckTerm("a"));
    }

    /// <summary>
    /// Ordering A — validation racing a session load. Every answer taken across the whole of
    /// <c>Warm</c> must be the real verdict; a single <c>Unknown</c> is the bug (it renders as a row
    /// with no warning while the add form warns about the same string).
    /// </summary>
    [ModelFact("installed-ctc")]
    public void CheckTerm_IsNeverUnknownWhileWarmHoldsTheSessionLock()
    {
        using CtcVocabularySpotter s = Installed();
        var answers = new List<TermSpottability>();

        Task warm = Task.Run(s.Warm);
        // Capped so a fast/failed Warm can never spin this forever.
        for (int i = 0; i < 5_000 && !warm.IsCompleted; i++)
        {
            answers.Add(s.CheckTerm("Wi-Fi"));
            Thread.Sleep(1);
        }
        warm.GetAwaiter().GetResult();

        output.WriteLine($"answers sampled during Warm: {answers.Count}");
        Assert.True(answers.Count >= 5,
            $"Warm returned after only {answers.Count} samples — it did not load a session, so this " +
            "test proved nothing. Check the model is really installed.");
        Assert.DoesNotContain(TermSpottability.Unknown, answers);
        Assert.All(answers, a => Assert.Equal(TermSpottability.Unsupported, a));

        // Ordering B — after Warm, the same answers, plus the positive control.
        Assert.Equal(TermSpottability.Unsupported, s.CheckTerm("Wi-Fi"));
        Assert.Equal(TermSpottability.Ok, s.CheckTerm("Nemotron"));
    }

    /// <summary>The lock split introduced a second lock, so the order they nest in is now load-bearing.
    /// Hammer validation from several threads against Warm/Unload cycles: a deadlock hangs the UI
    /// thread, which is strictly worse than the bug being fixed.</summary>
    [ModelFact("installed-ctc")]
    public void CheckTerm_DoesNotDeadlockAgainstWarmAndUnload()
    {
        using CtcVocabularySpotter s = Installed();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task[] readers =
        [
            .. Enumerable.Range(0, 3).Select(i => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) { _ = s.CheckTerm("Wi-Fi"); _ = s.CheckTerm("Nemotron"); }
            })),
        ];
        Task churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) { s.Warm(); s.Unload(); }
        });

        Assert.True(Task.WaitAll([.. readers, churn], TimeSpan.FromSeconds(30)),
            "CheckTerm/Warm/Unload deadlocked — the _gate → _textGate lock order was violated");
        Assert.Equal(TermSpottability.Unsupported, s.CheckTerm("Wi-Fi"));
    }
}
