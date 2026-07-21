using Jot.Text;
using Xunit;

namespace Jot.Tests;

public sealed class FillerWordCleanerTests
{
    // English — ported 1:1 from projects/jot-mobile/Jot/Tests/FillerWordCleanerTests.swift.
    [Theory]
    [InlineData("Um, I think", "I think ")]
    [InlineData("I, uh, mean", "I mean ")]
    [InlineData("umbrella", "umbrella ")]
    [InlineData("Ummmm yes", "Yes ")]
    [InlineData("Um. Uh.", "")]
    [InlineData("", "")]
    [InlineData("Hello world.", "Hello world. ")]
    [InlineData("Hello.\n\num New paragraph.", "Hello.\n\nNew paragraph. ")]
    [InlineData("Hello.\n\num world.", "Hello.\n\nWorld. ")]
    [InlineData("yeah uh okay", "Yeah okay ")]
    [InlineData("hello um world", "Hello world ")]
    [InlineData("this is uh really fast", "This is really fast ")]
    [InlineData("yeah uh um okay", "Yeah okay ")]
    public void English(string input, string expected) =>
        Assert.Equal(expected, FillerWordCleaner.Clean(input, "en"));

    [Fact]
    public void English_nonempty_output_ends_in_exactly_one_space()
    {
        string outp = FillerWordCleaner.Clean("Hello world.", "en");
        Assert.EndsWith(" ", outp);
        Assert.False(outp.EndsWith("  "));
        Assert.Equal("Hello world.", outp[..^1]);
    }

    // Multilingual — spec anchors from docs/plans/offline-cleanup-windows.md.
    [Theory]
    [InlineData("de", "Ähm, hallo", "Hallo ")]
    [InlineData("de", "das ist ähnlich", "das ist ähnlich ")]
    [InlineData("de", "er kommt um drei Uhr", "er kommt um drei Uhr ")]
    [InlineData("de", "ich ähm weiß", "ich weiß ")]
    [InlineData("fr", "euh bonjour", "Bonjour ")]
    [InlineData("fr", "je euh pense", "je pense ")]
    [InlineData("fr", "eh bien", "eh bien ")]
    [InlineData("it", "ehm sì", "Sì ")]
    [InlineData("it", "due mm tre", "due mm tre ")]
    [InlineData("it", "ehm, mmm", "")]
    [InlineData("es", "em hola", "Hola ")]
    [InlineData("es", "hola eh mundo", "hola mundo ")]
    [InlineData("es", "¿Verdad, eh?", "¿Verdad? ")]
    [InlineData("es", "¡eh!", "")]
    [InlineData("es", "pues bien", "pues bien ")]
    [InlineData("pt", "hum certo", "Certo ")]
    [InlineData("pt", "tipo assim", "tipo assim ")]
    public void Multilingual(string iso, string input, string expected) =>
        Assert.Equal(expected, FillerWordCleaner.Clean(input, iso));

    // Any other language = strict byte-for-byte no-op (fillers are not stripped, no trailing space added).
    [Theory]
    [InlineData("ar", "مرحبا بالعالم")]
    [InlineData("ja", "こんにちは um 世界")]   // even an English "um" is left alone — the cleaner never runs
    [InlineData("ko", "안녕하세요")]
    [InlineData("", "um hello world")]         // unmapped language name → "" iso → no-op
    public void Unsupported_language_is_byte_identical(string iso, string input) =>
        Assert.Equal(input, FillerWordCleaner.Clean(input, iso));
}
