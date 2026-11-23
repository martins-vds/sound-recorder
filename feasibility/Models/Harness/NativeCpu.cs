using System.Runtime.InteropServices;
using LLama.Native;

namespace Models.Harness;

/// <summary>A model-free native load observation, not a model compatibility verdict.</summary>
public sealed record CpuProbeResult(Architecture ProcessArchitecture, Architecture OsArchitecture, bool GpuOffload);

/// <summary>Replaceable process-wide native initialization boundary.</summary>
public interface ICpuNativeApi
{
    void Configure(bool cuda, bool vulkan, bool fallback);
    void Initialize();
    bool SupportsGpuOffload();
}

/// <summary>Only the actual native library configuration and initialization.</summary>
public sealed class CpuNativeApi : ICpuNativeApi
{
    public void Configure(bool cuda, bool vulkan, bool fallback) =>
        NativeLibraryConfig.LLama.WithCuda(cuda).WithVulkan(vulkan).WithAutoFallback(fallback);
    public void Initialize() => NativeApi.llama_empty_call();
    public bool SupportsGpuOffload() => NativeApi.llama_supports_gpu_offload();
}

/// <summary>Configures the pinned CPU backend once before any other LLamaSharp API.</summary>
public static class NativeCpu
{
    private static readonly Lazy<bool> Loaded = new(() =>
    {
        return Probe(new CpuNativeApi()).GpuOffload;
    });

    /// <summary>Loads the native API in this process without any model file.</summary>
    public static CpuProbeResult Probe() =>
        new(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture, Loaded.Value);

    /// <summary>Configures CPU-only execution before initialization; no fallback is authorized.</summary>
    public static CpuProbeResult Probe(ICpuNativeApi api)
    {
        api.Configure(false, false, false);
        api.Initialize();
        return new(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture, api.SupportsGpuOffload());
    }
}
