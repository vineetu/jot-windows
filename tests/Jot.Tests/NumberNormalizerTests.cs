using Jot.Text;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Parity gate for the spoken-number normalizer — all 51 fixtures ported 1:1 from
/// projects/jot-mobile/Jot/Tests/NumberNormalizerTests.swift. Input → expected pairs are byte-exact.
/// NOTE: "thirty second" is a tens-ordinal → "32nd" (per Swift + its test, NOT the "30-second" cleanup-spec).
/// </summary>
public sealed class NumberNormalizerTests
{
    [Theory]
    // --- Always-on rules (positive cases) ---
    [InlineData("fifteen pages", "15 pages")]
    [InlineData("twenty-five percent", "25%")]
    [InlineData("fifty dollars", "$50")]
    [InlineData("twenty-five thousand dollars", "$25,000")]
    [InlineData("five thirty PM", "5:30 PM")]
    [InlineData("at four", "at 4")]
    [InlineData("at four thirty", "at 4:30")]
    [InlineData("by five", "by 5")]
    [InlineData("in nineteen ninety-eight", "in 1998")]
    [InlineData("in twenty twenty-six", "in 2026")]
    [InlineData("in two thousand twenty-six", "in 2026")]
    [InlineData("apartment four oh seven", "apartment 407")]
    [InlineData("apartment two hundred and three", "apartment 203")]
    [InlineData("two hundred and thirty units", "230 units")]

    // --- Idiom exception ---
    [InlineData("a thousand times", "a thousand times")]
    [InlineData("a million times", "a million times")]
    [InlineData("a hundred drafts", "a hundred drafts")]
    [InlineData("one hundred percent", "100%")]
    [InlineData("a thousand dollars", "$1,000")]

    // --- Article drop on compound cardinal emission ---
    [InlineData("a thousand and twenty things", "1,020 things")]
    [InlineData("one hundred and fifty users", "150 users")]
    [InlineData("a million and one ways", "a million and one ways")]  // "million" → pass-through, article kept

    // --- Million / billion / trillion pass-through ---
    [InlineData("300 million", "300 million")]
    [InlineData("two million users", "two million users")]
    [InlineData("twenty-five million dollars", "twenty-five million dollars")]
    [InlineData("fifteen million", "fifteen million")]
    [InlineData("two billion", "two billion")]
    [InlineData("million", "million")]
    [InlineData("three trillion stars", "three trillion stars")]
    [InlineData("one billion dollars", "one billion dollars")]

    // --- Tens-ordinal combiner ---
    [InlineData("twenty third street", "23rd street")]
    [InlineData("twenty-first floor", "21st floor")]
    [InlineData("thirty second avenue", "32nd avenue")]
    [InlineData("ninety ninth percentile", "99th percentile")]
    [InlineData("twentieth century", "20th century")]
    [InlineData("thirtieth birthday", "30th birthday")]
    [InlineData("twenty-first street", "21st street")]

    // --- Skip / preserve rules ---
    [InlineData("my son turned eight", "my son turned eight")]
    [InlineData("almost twelve", "almost 12")]
    [InlineData("Twenty five new sign-ups today", "25 new sign-ups today")]
    [InlineData("I made twenty-five", "I made 25")]
    [InlineData("eight hundred five five five one two three four", "eight hundred five five five one two three four")]
    [InlineData("Looking back over the past ten years", "Looking back over the past 10 years")]
    [InlineData("I read fifteen pages and reviewed three pull requests",
               "I read 15 pages and reviewed three pull requests")]

    // --- Paragraph + punctuation preservation ---
    [InlineData("abc.\n\nfifteen things", "abc.\n\n15 things")]
    [InlineData("fifteen, twenty, twenty-five", "15, 20, 25")]
    [InlineData("I have eight cents", "I have 8¢")]

    // --- Negative tests (no convertible content) ---
    [InlineData("", "")]
    [InlineData("The quick brown fox jumps over the lazy dog.", "The quick brown fox jumps over the lazy dog.")]
    [InlineData("First and second and third.", "First and second and third.")]
    [InlineData("I have two cats and three dogs.", "I have two cats and three dogs.")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, NumberNormalizer.Normalize(input));
}
