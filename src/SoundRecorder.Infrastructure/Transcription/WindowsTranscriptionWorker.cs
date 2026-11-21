using System.Diagnostics;

namespace SoundRecorder.Infrastructure.Transcription;

internal interface ITranscriptionWorker : IAsyncDisposable
{
    int Id { get; }
    TextWriter Input { get; }
    TextReader Output { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    void Abort();
}

internal sealed class WindowsTranscriptionWorker : ITranscriptionWorker
{
    private readonly Process _process;
    private readonly IWorkerLifetime _lifetime;
    private readonly Task _diagnostics;

    private WindowsTranscriptionWorker(Process process, IWorkerLifetime lifetime)
    {
        _process = process;
        _lifetime = lifetime;
        _diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }

    public int Id => _process.Id;
    public TextWriter Input => _process.StandardInput;
    public TextReader Output => _process.StandardOutput;

    public static ITranscriptionWorker Start(string executablePath) =>
        Start(executablePath, WindowsProcessJob.Create);

    internal static ITranscriptionWorker Start(
        string executablePath,
        Func<IWorkerLifetime> createLifetime,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        var lifetime = createLifetime();
        Process? process = null;
        try
        {
            process = (startProcess ?? Process.Start)(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--transcribe-worker" },
            }) ?? throw new IOException("The local transcription worker could not start.");
            lifetime.Assign(process);
            return new WindowsTranscriptionWorker(process, lifetime);
        }
        catch
        {
            lifetime.Dispose();
            if (process is not null)
            {
                Stop(process);
                process.WaitForExit();
                process.Dispose();
            }

            throw;
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        return _process.ExitCode;
    }

    public void Abort() => Stop(_process);

    public async ValueTask DisposeAsync()
    {
        using (_lifetime)
        using (_process)
        {
            Abort();
            await _process.WaitForExitAsync(CancellationToken.None);
            await _diagnostics;
        }
    }

    private static void Stop(Process process)
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
    }
}
