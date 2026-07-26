using System.Text.Json.Serialization;

namespace Jot.Vocabulary;

// Port of `CorrectionProvenance.Record` from jot-shared (pinned commit 5326460). The value type the
// ask policy, the review surface and `CorrectionProvenance`'s persisted payload all read.
//
// Decode is STRICT (`required` + [JsonRequired]) on everything the Swift declares non-optional,
// because Swift's synthesized decoder throws on an absent key even where a default exists. Only
// Alternates and Shape are optional there, and they are the two fields added after payloads had
// already shipped. Leniency here would be worse than the throw: a record silently defaulting
// OriginalStart to 0 gets a colliding identity Key and adjudicates the wrong occurrence.

/// <summary>
/// One recorded gate decision for a single occurrence, kept so the review surface can let the
/// owner adjudicate it later.
/// </summary>
public sealed record CorrectionRecord
{
    public required string OriginalWord { get; init; }   // what the transcriber wrote ("Jamie")
    public required string Term { get; init; }           // the vocab term ("Jamy")
    public required string Decision { get; init; }       // "APPLY" | "BLOCK" | "OVERRIDE"
    public required string Outcome { get; init; }        // "applied" | "kept"
    [JsonRequired] public float Confidence { get; init; }
    [JsonRequired] public float Margin { get; init; }
    [JsonRequired] public bool Unsure { get; init; }
    [JsonRequired] public int OccurrenceIndex { get; init; }   // display-only
    [JsonRequired] public int OriginalStart { get; init; }
    [JsonRequired] public int OriginalLength { get; init; }

    /// <summary>LIVE anchor into the published text — mutated only by reconcile, which is why
    /// this one is settable while the rest are init-only.</summary>
    [JsonRequired] public int PublishedStart { get; set; }

    [JsonRequired] public int PublishedLength { get; init; }   // gate-time span length (display/diag only)
    public IReadOnlyList<VocabularyGate.Alternate>? Alternates { get; init; }
    public string? Shape { get; init; }

    /// <summary>Per-occurrence identity within one payload. Stays RAW-lowercased (not
    /// `CorrectionKey`-normalized) — inside a single payload raw and normalized are equally
    /// unique, and the shared implementation pins this shape.</summary>
    public string Key => $"{CorrectionKey.Lowercased(OriginalWord)}|{CorrectionKey.Lowercased(Term)}|{OriginalStart}";

    /// <summary>Mapping key shared by every occurrence of the same original→term. Uses the ONE
    /// shared normalization so the ledger, the store's nets, and the ask policy's prior lookups
    /// can never disagree about pair identity.</summary>
    public string MappingKey => $"{CorrectionKey.Normalize(OriginalWord)}|{CorrectionKey.Normalize(Term)}";

    /// <summary>Pre-normalization mapping-key shape — read-side fallback only, so older persisted
    /// entries keep reconciling.</summary>
    public string LegacyMappingKey => $"{CorrectionKey.Lowercased(OriginalWord)}|{CorrectionKey.Lowercased(Term)}";
}
