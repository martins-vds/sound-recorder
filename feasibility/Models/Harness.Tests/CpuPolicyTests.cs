using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class CpuPolicyTests
{
    [Fact]
    public void Probe_CpuOnlyPolicy_ConfiguresBeforeLoadingAndReportsNativeCapability()
    {
        var api = new CpuDouble();
        var result = NativeCpu.Probe(api);
        Assert.False(result.GpuOffload);
        Assert.Equal(["configure", "initialize", "capability"], api.Calls);
    }

    [Fact]
    public void Probe_ConfigurationFailure_DoesNotInitializeOrClaimSuccess()
    {
        var cause = new InvalidOperationException("Unavailable CPU backend.");
        var api = new CpuDouble { Failure = cause };
        Assert.Same(cause, Assert.Throws<InvalidOperationException>(() => NativeCpu.Probe(api)));
        Assert.Equal(["configure"], api.Calls);
    }

    private sealed class CpuDouble : ICpuNativeApi
    {
        public List<string> Calls { get; } = [];
        public Exception? Failure { get; init; }
        public void Configure(bool cuda, bool vulkan, bool fallback)
        {
            Calls.Add("configure");
            if (Failure is not null) throw Failure;
            Assert.False(cuda || vulkan || fallback);
        }
        public void Initialize() => Calls.Add("initialize");
        public bool SupportsGpuOffload()
        {
            Calls.Add("capability");
            return false;
        }
    }
}
