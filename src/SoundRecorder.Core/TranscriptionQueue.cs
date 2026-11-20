namespace SoundRecorder.Core;

public sealed class TranscriptionQueue : ITranscriptionQueue
{
    private readonly ILocalTranscriber _transcriber;
    private readonly ITranscriptWriter _writer;
    private readonly ITranscriptionJobRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TranscriptionJob> _jobs = [];
    private CancellationTokenSource? _runningJobCancellation;
    private Task? _worker;

    public TranscriptionQueue(
        ILocalTranscriber transcriber,
        ITranscriptWriter writer,
        ITranscriptionJobRepository repository)
    {
        _transcriber = transcriber;
        _writer = writer;
        _repository = repository;
    }

    public IReadOnlyList<TranscriptionJob> Jobs
    {
        get
        {
            lock (_jobs)
            {
                return _jobs.ToArray();
            }
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var persisted = await _repository.LoadAsync(cancellationToken);
        lock (_jobs)
        {
            foreach (var job in persisted.OrderBy(job => job.CreatedAt))
            {
                if (job.Status == TranscriptionJobStatus.Running)
                {
                    job.Status = TranscriptionJobStatus.Interrupted;
                }

                _jobs.Add(job);
            }
        }

        StartWorker();
        await SaveAsync(cancellationToken);
    }

    public async Task EnqueueAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        var job = new TranscriptionJob(Guid.NewGuid(), recording);
        lock (_jobs)
        {
            _jobs.Add(job);
        }

        await SaveAsync(cancellationToken);
        StartWorker();
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        TranscriptionJob? canceledJob;
        lock (_jobs)
        {
            canceledJob = _jobs.FirstOrDefault(job => job.Id == jobId);
            if (canceledJob?.Status == TranscriptionJobStatus.Queued)
            {
                canceledJob.Status = TranscriptionJobStatus.Canceled;
            }
        }

        if (canceledJob?.Status == TranscriptionJobStatus.Running)
        {
            await _runningJobCancellation!.CancelAsync();
        }

        await SaveAsync(cancellationToken);
    }

    public async Task<TranscriptionJob> RetryAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        var job = new TranscriptionJob(Guid.NewGuid(), recording);
        lock (_jobs)
        {
            _jobs.Add(job);
        }

        await SaveAsync(cancellationToken);
        StartWorker();
        return job;
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (_worker is { } worker && !worker.IsCompleted)
        {
            await worker.WaitAsync(cancellationToken);
        }
    }

    private void StartWorker()
    {
        // Stryker disable once all: removing this guard only creates a redundant, gated worker task.
        if (_worker is { IsCompleted: false })
        {
            return;
        }

        _worker = RunWorkerAsync();
    }

    private async Task RunWorkerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            while (NextQueuedJob() is { } job)
            {
                await RunAsync(job);
            }
        }
        finally
        {
            _gate.Release();
            _worker = null;
            // Stryker disable once all: this race guard is observable only if enqueue and worker teardown interleave.
            if (NextQueuedJob() is not null)
            {
                StartWorker();
            }
        }
    }

    private TranscriptionJob? NextQueuedJob()
    {
        lock (_jobs)
        {
            return _jobs
                .Where(job => job.Status == TranscriptionJobStatus.Queued)
                .OrderBy(job => job.CreatedAt)
                .FirstOrDefault();
        }
    }

    private async Task RunAsync(TranscriptionJob job)
    {
        _runningJobCancellation = new CancellationTokenSource();
        job.Status = TranscriptionJobStatus.Running;
        await SaveAsync(_runningJobCancellation.Token);

        try
        {
            var document = await _transcriber.TranscribeAsync(
                job.Recording,
                _runningJobCancellation.Token);
            job.Outputs = await _writer.WriteAsync(document, _runningJobCancellation.Token);
            job.Status = TranscriptionJobStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            job.Status = TranscriptionJobStatus.Canceled;
        }
        catch (Exception exception)
        {
            job.Status = TranscriptionJobStatus.Failed;
            job.Error = exception.Message;
        }
        finally
        {
            await SaveAsync(CancellationToken.None);
            // Stryker disable once Statement: disposal is resource lifecycle, not result-state behavior.
            _runningJobCancellation.Dispose();
            _runningJobCancellation = null;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _repository.SaveAsync(Jobs, cancellationToken);
    }
}
