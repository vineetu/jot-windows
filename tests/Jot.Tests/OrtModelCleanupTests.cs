using System;
using System.IO;
using Jot.Services;
using Xunit;

namespace Jot.Tests;

public class OrtModelCleanupTests : IDisposable
{
    private readonly string _root;

    public OrtModelCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jot-ortclean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Run_DeletesOnnxFolders_WhenGgufIntact()
    {
        string config = Path.Combine(_root, "config");
        string gguf = Path.Combine(_root, "model.gguf");
        string int4 = Path.Combine(_root, "int4");
        string fp16 = Path.Combine(_root, "fp16");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(int4);
        Directory.CreateDirectory(fp16);
        File.WriteAllText(Path.Combine(int4, "encoder.onnx"), "x");
        File.WriteAllText(Path.Combine(fp16, "encoder.onnx"), "y");
        File.WriteAllBytes(gguf, new byte[32]);

        var marker = new OrtModelCleanup.Marker(gguf, 32, int4, fp16);
        OrtModelCleanup.Run(config, marker);

        Assert.False(Directory.Exists(int4));
        Assert.False(Directory.Exists(fp16));
        Assert.True(File.Exists(gguf));
        Assert.False(OrtModelCleanup.HasPending(config));
    }

    [Fact]
    public void Run_LeavesOnnx_WhenGgufMissing()
    {
        string config = Path.Combine(_root, "config");
        string gguf = Path.Combine(_root, "missing.gguf");
        string int4 = Path.Combine(_root, "int4");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(int4);
        File.WriteAllText(Path.Combine(int4, "encoder.onnx"), "x");

        var marker = new OrtModelCleanup.Marker(gguf, 32, int4, @"C:\nope\fp16");
        OrtModelCleanup.Run(config, marker);

        Assert.True(Directory.Exists(int4));
    }

    [Fact]
    public void Run_LeavesOnnx_WhenGgufSizeChanged()
    {
        string config = Path.Combine(_root, "config");
        string gguf = Path.Combine(_root, "model.gguf");
        string int4 = Path.Combine(_root, "int4");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(int4);
        File.WriteAllText(Path.Combine(int4, "encoder.onnx"), "x");
        File.WriteAllBytes(gguf, new byte[8]);

        var marker = new OrtModelCleanup.Marker(gguf, 32, int4, @"C:\nope\fp16");
        OrtModelCleanup.Run(config, marker);

        Assert.True(Directory.Exists(int4));
    }
}
