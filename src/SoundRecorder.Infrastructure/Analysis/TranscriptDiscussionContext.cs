using System.Runtime.CompilerServices;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis;

/// <summary>Follow-up intent and antecedent must be explicit; neither is guessed from natural language.</summary>
public sealed record TranscriptDiscussionContext(bool IsFollowUp, string? Antecedent);

public sealed partial class AnalysisProcessingService
{
    private async Task<string?> DiscussionQuestionAsync(AnalysisStoreRevision? previous, string? question, CancellationToken token)
    {
        if (question is null || discussion?.IsFollowUp != true) return question;
        if (string.IsNullOrWhiteSpace(discussion.Antecedent)) return null;
        if (discussion.Antecedent.Length > 128)
            throw new ArgumentException("Explicit discussion antecedents must be bounded to 128 characters.");
        var history = await UserHistoryAsync(previous, token);
        if (history.Count == 0) return null;
        return question + "\nExplicit antecedent (context only): " + discussion.Antecedent +
            "\nPrior user questions (context only, not evidence): " + string.Join(" | ", history);
    }

    private async Task<Queue<string>> UserHistoryAsync(AnalysisStoreRevision? previous, CancellationToken token)
    {
        var history = new Queue<string>();
        string? last = null;
        if (previous is not null)
        {
            await foreach (var output in store.ReadOutputsAsync(previous, token))
            {
                if (!IsNewUserQuestion(output, last)) continue;
                last = output.Question!;
                history.Enqueue(BoundedContext(last));
                if (history.Count > 4) history.Dequeue();
            }
        }
        return history;
    }

    private static bool IsNewUserQuestion(AnalysisStoreOutput output, string? last) =>
        output.Kind == "Discussion" && output.Question is not null && output.Question != last;

    private static string BoundedContext(string question)
    {
        var length = Math.Min(48, question.Length);
        if (char.IsHighSurrogate(question[length - 1])) length--;
        return question[..length];
    }

    private async IAsyncEnumerable<AnalysisStoreOutput> ClarificationAsync(AnalysisStoreRevision? previous,
        string question, [EnumeratorCancellation] CancellationToken token)
    {
        var ordinal = 0;
        if (previous is not null)
        {
            await foreach (var output in store.ReadOutputsAsync(previous, token))
                yield return output with { Ordinal = ordinal++ };
        }
        yield return new(ordinal, "Discussion", question,
            "Clarification required: supply an explicit antecedent and prior user context for this exact attempt. No answer was inferred.", []);
    }
}
