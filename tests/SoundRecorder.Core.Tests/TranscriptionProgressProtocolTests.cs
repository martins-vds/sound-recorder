using System.Diagnostics;
using System.Text;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class TranscriptionProgressProtocolTests
{
    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task Real_owned_worker_progress_is_correlated_and_not_a_final_result()
    {
        var jobId = Guid.NewGuid();
        var script = """
            $r=[Console]::ReadLine()|ConvertFrom-Json
            @{Kind='Started';JobId=$r.JobId;Started=$true}|ConvertTo-Json -Compress|%{[Console]::WriteLine($_)}
            foreach($p in 0,37,84){
              @{Kind='Progress';JobId=$r.JobId;Progress=@{Phase=1;Percent=$p}}|ConvertTo-Json -Compress|%{[Console]::WriteLine($_)}
            }
            @{Kind='Progress';JobId=[Guid]::NewGuid().ToString();Progress=@{Phase=1;Percent=99}}|ConvertTo-Json -Compress|%{[Console]::WriteLine($_)}
            @{Kind='Result';JobId=[Guid]::NewGuid().ToString();Segments=@(@{Start='00:00:00';End='00:00:01';Text='stale result'})}|ConvertTo-Json -Depth 5 -Compress|%{[Console]::WriteLine($_)}
            Start-Sleep -Milliseconds 500
            @{Kind='Result';JobId=$r.JobId;Segments=@()}|ConvertTo-Json -Compress|%{[Console]::WriteLine($_)}
            """;
        var transcriber = new WhisperProcessTranscriber("controlled-worker", new TranscriptionJobManagementTests.Models(),
            _ => WindowsTranscriptionWorker.Start("controlled-worker", WindowsProcessJob.Create, start =>
            {
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                start.ArgumentList.Clear();
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                return Process.Start(start);
            }));
        var seen = new List<int?>();
        var progress = new Reporter(value => seen.Add(value.Percent));
        var task = transcriber.TranscribeAsync(new(jobId, TranscriptionJobManagementTests.Recording(),
            "base-original", "pinned.bin"), progress, default);
        await TranscriptionJobManagementTests.WaitUntilAsync(() => seen.Count >= 3 || task.IsCompleted);
        Assert.Equal(new int?[] { 0, 37, 84 }, seen);
        Assert.False(task.IsCompleted);
        var document = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(document.Segments);
    }

    internal sealed class Reporter(Action<TranscriptionProgress> report) : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) => report(value);
    }
}
