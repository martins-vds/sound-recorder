namespace SoundRecorder.Core;

internal sealed class AnalysisJobInferenceGate
{
    private readonly object sync = new();
    private TaskCompletionSource changed = NewSignal();
    private bool recording;
    private bool occupied;

    public bool Recording { get { lock (sync) return recording; } }

    public void SetRecording(bool value)
    {
        lock (sync)
        {
            recording = value;
            Pulse();
        }
    }

    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (!recording && !occupied)
                {
                    occupied = true;
                    return new Lease(this);
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    private void Release()
    {
        lock (sync)
        {
            occupied = false;
            Pulse();
        }
    }

    private void Pulse()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Lease(AnalysisJobInferenceGate owner) : IDisposable
    {
        private AnalysisJobInferenceGate? held = owner;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
