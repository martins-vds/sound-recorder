namespace SoundRecorder.Core;

internal static class AnalysisJobValidation
{
    public static void Request(AnalysisJobRequest request)
    {
        Transcript(request);
        Resources(request);
        Coverage(request);
        Discussion(request.Discussion);
    }

    private static void Transcript(AnalysisJobRequest request)
    {
        if (request.Identity.AttemptId == Guid.Empty || request.Transcript.AttemptId != request.Identity.AttemptId)
            throw new ArgumentException("Select the exact successful transcript attempt.");
        if (request.Transcript.Language != request.Identity.Language ||
            request.Identity.Language is not ("English" or "Brazilian Portuguese"))
            throw new ArgumentException("Use the immutable successful transcript language.");
    }

    private static void Resources(AnalysisJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelDirectory))
            throw new ArgumentException("Pin the explicitly verified local model directory.");
        if (string.IsNullOrWhiteSpace(request.Transcript.Outputs.TextPath) ||
            string.IsNullOrWhiteSpace(request.Transcript.Outputs.SubtitlePath))
            throw new ArgumentException("Pin both successful transcript output paths.");
    }

    private static void Coverage(AnalysisJobRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
            throw new ArgumentException("Select an explicit analysis coverage mode.");
        var question = request.Transcript.Question;
        if (InvalidQuestion(request.Mode, question))
            throw new ArgumentException("Select summary or an explicit bounded discussion question.");
    }

    private static bool InvalidQuestion(AnalysisJobMode mode, string? question) =>
        mode == AnalysisJobMode.Summary ? question is not null :
            string.IsNullOrWhiteSpace(question) || question.Length > 512;

    private static void Discussion(AnalysisJobDiscussionContext? context)
    {
        if (context?.Antecedent is { Length: > 128 } ||
            context is { IsFollowUp: true } && string.IsNullOrWhiteSpace(context.Antecedent))
            throw new ArgumentException("Pin an explicit bounded follow-up antecedent.");
    }
}
