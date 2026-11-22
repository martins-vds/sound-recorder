using System.Runtime.InteropServices;
using LLama.Native;

namespace Models.Harness;

/// <summary>A model-free native load observation, not a model compatibility verdict.</summary>
public sealed record CpuProbeResult(Architecture ProcessArchitecture, Architecture OsArchitecture, bool GpuOffload);

/// <summary>Configures the pinned CPU backend once before any other LLamaSharp API.</summary>
public static class NativeCpu
{
    private static readonly Lazy<bool> Loaded = new(() =>
    {
        NativeLibraryConfig.LLama.WithCuda(false).WithVulkan(false).WithAutoFallback(false);
        NativeApi.llama_empty_call();
        return NativeApi.llama_supports_gpu_offload();
    });

    /// <summary>Loads the native API in this process without any model file.</summary>
    public static CpuProbeResult Probe() =>
        new(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture, Loaded.Value);
}
