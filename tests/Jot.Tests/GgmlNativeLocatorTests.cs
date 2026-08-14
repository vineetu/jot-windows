using System.IO;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

public class GgmlNativeLocatorTests
{
    [Fact]
    public void HandyInstall_IsForbidden()
    {
        string handy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Handy");
        Assert.True(GgmlNativeLocator.IsForbidden(handy));
        Assert.True(GgmlNativeLocator.IsForbidden(Path.Combine(handy, "extra")));
        Assert.True(GgmlNativeLocator.IsForbidden(handy + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void UnrelatedFolder_IsNotForbidden()
    {
        Assert.False(GgmlNativeLocator.IsForbidden(@"C:\natives"));
        Assert.False(GgmlNativeLocator.IsForbidden(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot")));
        Assert.False(GgmlNativeLocator.IsForbidden(null));
        Assert.False(GgmlNativeLocator.IsForbidden(""));
    }

    [Fact]
    public void TryResolve_SkipsHandyEvenIfEnvPointsThere()
    {
        string handy = GgmlNativeLocator.HandyDir;
        string? resolved = GgmlNativeLocator.TryResolve(name =>
            name == GgmlNativeLocator.EnvVar ? handy : null);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_PrefersEnvWhenTheFolderHasTheDll()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "jot-ggml-natives-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "transcribe.dll"), [0]);
            string? resolved = GgmlNativeLocator.TryResolve(name =>
                name == GgmlNativeLocator.EnvVar ? tmp : null);
            Assert.Equal(Path.GetFullPath(tmp), resolved);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryResolve_IgnoresEnvFolderWithoutDll()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "jot-ggml-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string? resolved = GgmlNativeLocator.TryResolve(name =>
                name == GgmlNativeLocator.EnvVar ? tmp : null);
            // May still find natives next to the test host; must NOT be the empty env dir.
            if (resolved is not null)
                Assert.NotEqual(Path.GetFullPath(tmp), resolved);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }
}
