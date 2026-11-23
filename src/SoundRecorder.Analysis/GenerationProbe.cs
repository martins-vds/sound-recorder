using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace SoundRecorder.Analysis;

/// <summary>In-process model seam; token counts include the serialized chat template.</summary>
public interface IGenerationBackend
{
    /// <summary>Serializes exact model chat delimiters without conversation history.</summary>
    string RenderPrompt(string instruction, string input);
    /// <summary>Counts the same special-token/BOS convention that inference uses.</summary>
    int CountTokens(string prompt);
    /// <summary>Returns native completion versus length exhaustion; failures/cancellation throw at safe boundaries.</summary>
    Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken);
}

/// <summary>Native end-of-generation and output-cap exhaustion are distinct outcomes.</summary>
public enum GenerationFinish { Completed, TokenLimit }

/// <summary>Exact native sampled-token observation, not retokenized output.</summary>
public sealed record GenerationResult(string Text, GenerationFinish Finish, int GeneratedTokens);

/// <summary>An exact reference to an immutable transcript.</summary>
public sealed record TranscriptIdentity(string SuccessfulTranscriptionAttemptId, string TranscriptSha256);

/// <summary>Original small-fixture excerpt text and saved-audio timestamps, used only by retrieval diagnostics.</summary>
public sealed record Excerpt(string Id, long? StartMs, long? EndMs, string Text);

/// <summary>Bounded structured extraction, exact disk merge and independently timed diagnostics.</summary>
public sealed partial class GenerationProbe(IGenerationBackend backend, int contextTokens = 4096,
    int outputTokens = 1024, int answerTokens = 512)
{
    private static readonly JsonElement OutputSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type":"object","required":["claims","limitation"],"additionalProperties":false,
          "$defs":{"presence":{"oneOf":[
            {"type":"object","required":["state","value"],"additionalProperties":false,
             "properties":{"state":{"const":"Absent"},"value":{"type":"null"}}},
            {"type":"object","required":["state","value"],"additionalProperties":false,
             "properties":{"state":{"const":"Stated"},"value":{"type":"string","minLength":1,"maxLength":128}}}
          ]}},
          "properties":{
            "claims":{"type":"array","maxItems":16,"items":{
              "type":"object","required":["kind","statement","owner","deadline","qualifiers","references"],"additionalProperties":false,
              "properties":{
                "kind":{"enum":["Topic","Decision","Action","Report"]},
                "statement":{"type":"string","minLength":1,"maxLength":512},
                "owner":{"$ref":"#/$defs/presence"},"deadline":{"$ref":"#/$defs/presence"},
                "qualifiers":{"type":"array","maxItems":8,"items":{"type":"string","minLength":1,"maxLength":128}},
                "references":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"integer","minimum":0,"maximum":3}}
              }
            }},
            "limitation":{"type":["string","null"],"minLength":1,"maxLength":512}
          }
        }
        """);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
}
