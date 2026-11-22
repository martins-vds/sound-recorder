using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class WindowsWorkerLifetimeQualityTests
{
    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task Start_AssignmentFailure_WaitsForOwnedOutputDrainAndClosesTheExactProcessHandle()
    {
        using var unblock = new ManualResetEventSlim();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? started = null;
        SafeProcessHandle? handle = null;
        var failure = new Win32Exception(5);
        var attempt = Task.Run(() => Record.Exception(() => WindowsTranscriptionWorker.Start(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe"),
            () => new RejectingLifetime(_ => throw failure),
            info =>
            {
                started = Process.Start(info)!;
                handle = started.SafeHandle;
                started.OutputDataReceived += (_, line) =>
                {
                    if (line.Data != "{\"Started\":true}") return;
                    received.TrySetResult();
                    unblock.Wait(TimeSpan.FromSeconds(5));
                };
                started.BeginOutputReadLine();
                started.StandardInput.WriteLine("{\"ModelPath\":\"hang\"}");
                started.StandardInput.Close();
                received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                return started;
            })));
        try
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(attempt, await Task.WhenAny(attempt, Task.Delay(100)));
            unblock.Set();
            Assert.Same(failure, await attempt);
            Assert.True(handle!.IsClosed);
        }
        finally
        {
            unblock.Set();
            await attempt;
            if (started is not null && handle?.IsClosed == false)
            {
                await StopAsync(started);
                started.Dispose();
            }
        }
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task DisposeAsync_WaitsForPublicProcessOutputDrainBeforeClosingOwnedHandles()
    {
        using var unblock = new ManualResetEventSlim();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SafeProcessHandle? handle = null;
        var worker = WindowsTranscriptionWorker.Start(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe"), WindowsProcessJob.Create,
            info =>
            {
                var process = Process.Start(info)!;
                handle = process.SafeHandle;
                process.OutputDataReceived += (_, line) =>
                {
                    if (line.Data != "{\"Started\":true}") return;
                    received.TrySetResult();
                    unblock.Wait(TimeSpan.FromSeconds(5));
                };
                process.BeginOutputReadLine();
                return process;
            });
        try
        {
            await worker.Input.WriteLineAsync("{\"ModelPath\":\"hang\"}");
            worker.Input.Close();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = worker.DisposeAsync().AsTask();
            Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(100)));
            Assert.False(handle!.IsClosed);
            unblock.Set();
            await disposal;
            Assert.True(handle.IsClosed);
        }
        finally
        {
            unblock.Set();
            if (handle?.IsClosed != true) await worker.DisposeAsync();
        }
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task DisposeAsync_ReportsAnOwnedDiagnosticPipeFailureRatherThanLeavingAnUnobservedPump()
    {
        Process? observed = null;
        SafeProcessHandle? handle = null;
        var worker = WindowsTranscriptionWorker.Start(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe"), WindowsProcessJob.Create,
            info =>
            {
                observed = Process.Start(info)!;
                handle = observed.SafeHandle;
                return observed;
            });
        try
        {
            await worker.Input.WriteLineAsync("{\"ModelPath\":\"hang\"}");
            worker.Input.Close();
            Assert.Equal("{\"Started\":true}", await worker.Output.ReadLineAsync());
            var pipe = Assert.IsType<FileStream>(observed!.StandardError.BaseStream);
            Assert.True(CancelIoEx(pipe.SafeFileHandle, IntPtr.Zero), $"CancelIoEx failed: {Marshal.GetLastPInvokeError()}");
            var error = await Record.ExceptionAsync(() => worker.DisposeAsync().AsTask());
            Assert.True(error is IOException or ObjectDisposedException or OperationCanceledException,
                $"The owned diagnostic pipe fault was not propagated: {error}");
            Assert.True(handle!.IsClosed);
        }
        finally
        {
            if (handle?.IsClosed == false) await worker.DisposeAsync();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeHandle file, IntPtr overlapped);

    [Fact]
    public void Create_KernelCannotCreateJob_PreservesErrorAndClosesInvalidHandle()
    {
        var job = new WindowsProcessJob(IntPtr.Zero, _ => throw new InvalidOperationException());

        var error = Assert.Throws<Win32Exception>(() => WindowsProcessJob.Create(
            () =>
            {
                Marshal.SetLastPInvokeError(5);
                return job;
            },
            _ => throw new InvalidOperationException("An invalid handle must not be configured")));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.True(job.IsClosed);
    }

    [Fact]
    public void Create_KernelRejectsLimits_ClosesHandleWithoutOverwritingOriginalError()
    {
        var closes = 0;
        var job = new WindowsProcessJob(new IntPtr(12345), _ =>
        {
            closes++;
            Marshal.SetLastPInvokeError(87);
            return true;
        });

        var error = Assert.Throws<Win32Exception>(() => WindowsProcessJob.Create(
            () => job,
            _ =>
            {
                Marshal.SetLastPInvokeError(5);
                return false;
            }));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(1, closes);
        Assert.True(job.IsClosed);
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task Dispose_AssignedRunningProcess_EndsItWithoutAParentCallback()
    {
        using var child = StartFixture();
        using var job = WindowsProcessJob.Create();
        try
        {
            job.Assign(child);
            await child.StandardInput.WriteLineAsync("{\"ModelPath\":\"hang\"}");
            child.StandardInput.Close();
            Assert.Equal("{\"Started\":true}", await child.StandardOutput.ReadLineAsync());
            Assert.False(child.HasExited);

            job.Dispose();
            var exited = child.WaitForExitAsync();
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(child.HasExited, "Closing the owned job must terminate its running process.");
            await exited;
        }
        finally
        {
            await StopAsync(child);
        }
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task Assign_ExitedProcess_ReportsNativeFailure()
    {
        using var child = StartFixture();
        child.Kill();
        await child.WaitForExitAsync();
        using var job = WindowsProcessJob.Create();

        Assert.Throws<Win32Exception>(() => job.Assign(child));
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task Abort_WorkerHasDescendants_StopsTheWholeOwnedProcessTree()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe");
        await using var worker = WindowsTranscriptionWorker.Start(executable);
        await worker.Input.WriteLineAsync("{\"ModelPath\":\"tree\"}");
        worker.Input.Close();
        Assert.Equal("{\"Started\":true}", await worker.Output.ReadLineAsync());
        using var description = System.Text.Json.JsonDocument.Parse((await worker.Output.ReadLineAsync())!);
        using var grandchild = Process.GetProcessById(description.RootElement.GetProperty("Grandchild").GetInt32());
        Assert.False(grandchild.HasExited);

        worker.Abort();
        await worker.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var exited = grandchild.WaitForExitAsync();
        await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(grandchild.HasExited, "Aborting a worker must also stop its owned descendants.");
        await exited;
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task Start_LifetimeAssignmentFails_StopsUnownedChildAndReleasesLifetime()
    {
        Process? observed = null;
        var failure = new Win32Exception(5);
        var lifetime = new RejectingLifetime(_ => throw failure);
        try
        {
            var actual = Assert.Throws<Win32Exception>(() => WindowsTranscriptionWorker.Start(
                Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe"),
                () => lifetime,
                info =>
                {
                    var child = Process.Start(info)!;
                    observed = Process.GetProcessById(child.Id);
                    Assert.False(observed.HasExited);
                    return child;
                }));

            Assert.Same(failure, actual);
            Assert.True(lifetime.Disposed);
            Assert.NotNull(observed);
            Assert.True(observed.HasExited);
        }
        finally
        {
            if (observed is not null)
            {
                await StopAsync(observed);
                observed.Dispose();
            }
        }
    }

    [Fact]
    public void Start_ProcessCannotStart_KeepsItHiddenAndReleasesItsLifetime()
    {
        var lifetime = new RejectingLifetime(_ => throw new InvalidOperationException());

        var error = Assert.Throws<IOException>(() => WindowsTranscriptionWorker.Start(
            "worker.exe",
            () => lifetime,
            info =>
            {
                Assert.Equal("worker.exe", info.FileName);
                Assert.False(info.UseShellExecute);
                Assert.True(info.CreateNoWindow);
                Assert.True(info.RedirectStandardInput);
                Assert.True(info.RedirectStandardOutput);
                Assert.True(info.RedirectStandardError);
                Assert.Equal(new[] { "--transcribe-worker" }, info.ArgumentList);
                return null;
            }));

        Assert.Equal("The local transcription worker could not start.", error.Message);
        Assert.True(lifetime.Disposed);
    }

    private static Process StartFixture() =>
        Process.Start(new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "--transcribe-worker" },
        })!;

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        await process.WaitForExitAsync();
    }

    private sealed class RejectingLifetime(Action<Process> assign) : IWorkerLifetime
    {
        public bool Disposed { get; private set; }
        public void Assign(Process process) => assign(process);
        public void Dispose() => Disposed = true;
    }
}
