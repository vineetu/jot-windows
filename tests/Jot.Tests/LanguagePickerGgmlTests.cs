using System.Linq;
using Jot.Transcription.Ggml;
using Jot.ViewModels;
using Xunit;

namespace Jot.Tests;

public class LanguagePickerGgmlTests
{
    [Fact]
    public void GgmlView_MarksAdaptationReadyUnavailable()
    {
        var view = LanguagePicker.BuildView(ggmlEngine: true);
        var options = view.Cast<LanguageOption>().ToList();
        Assert.Equal(41, options.Count);
        foreach (string code in GgmlLanguage.AdaptationReady)
        {
            LanguageOption row = options.Single(o => o.Code == code);
            Assert.False(row.IsAvailable);
            Assert.Equal("Not available on this engine", row.Group);
            Assert.Contains("not available", row.Label, System.StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(options.Single(o => o.Code == "en-US").IsAvailable);
        Assert.True(options.Single(o => o.Code == "nb-NO").IsAvailable);
    }

    [Fact]
    public void OnnxView_KeepsAdaptationReadyEnabled()
    {
        var view = LanguagePicker.BuildView(ggmlEngine: false);
        var options = view.Cast<LanguageOption>().ToList();
        LanguageOption greek = options.Single(o => o.Code == "el-GR");
        Assert.True(greek.IsAvailable);
        Assert.Equal("Basic support", greek.Group);
    }
}
