using Microsoft.Extensions.Logging;

namespace SoundRecorder.Infrastructure.Analysis.Models;

public sealed partial class AnalysisModelSetup
{
    [LoggerMessage(6100, LogLevel.Information, "Explicit analysis model setup started.")]
    private static partial void SetupStarted(ILogger logger);

    [LoggerMessage(6101, LogLevel.Information, "Analysis model profile activated after verification.")]
    private static partial void SetupCompleted(ILogger logger);

    [LoggerMessage(6102, LogLevel.Warning, "Analysis model setup failed ({FailureKind}); previous selection retained.")]
    private static partial void SetupFailed(ILogger logger, string failureKind);
}
