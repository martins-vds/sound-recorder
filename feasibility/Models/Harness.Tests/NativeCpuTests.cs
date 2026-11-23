using System.Diagnostics;
using System.Runtime.InteropServices;
using SoundRecorder.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Models.Harness.Tests;

[Trait("type", "NativeProbe")]
public sealed class NativeCpuTests(ITestOutputHelper output)
{
    [Fact]
    public void Probe_NativeCpu_LoadsInProcessWithoutModelOrGpu()
    {
        var result = NativeCpu.Probe();
        Assert.Equal(RuntimeInformation.OSArchitecture, result.ProcessArchitecture);
        Assert.False(result.GpuOffload);
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().Where(m =>
            m.ModuleName is "llama.dll" or "ggml.dll" or "ggml-base.dll" or "ggml-cpu.dll").ToArray();
        Assert.Contains(modules, m => m.ModuleName == "llama.dll");
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        foreach (var module in modules)
        {
            output.WriteLine($"{module.ModuleName}: {module.FileName}");
        }
    }
}
