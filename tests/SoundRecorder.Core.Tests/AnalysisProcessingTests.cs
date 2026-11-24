using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundRecorder.Analysis;
using SoundRecorder.Infrastructure.Analysis;
using SoundRecorder.Infrastructure.Analysis.Storage;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class AnalysisProcessingTests
{
    [Fact]
    public async Task AnalyzeAsync_GlobalQuestion_PersistsLastDecisionAndConflictingReportsWithExactEvidence()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Report: approved.", "Report: not approved.", "Topic: budget.", "Decision: defer.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var generation = new CandidateGeneration();
        ILocalTranscriptAnalysis service = new AnalysisProcessingService(store, revision, files.Directory,
            generation, new Vectors(), AnalysisProcessingMode.GlobalQuestion);

        var result = await service.AnalyzeAsync(request with { Question = "What was agreed?" }, new Progress<string>(), default);

        Assert.Equal("Completed", result.Status);
        var reopened = SqliteTranscriptAnalysisStore.Open(files.Database);
        var saved = await Collect(reopened.ReadOutputsAsync((await reopened.LoadAsync(revision.Identity, default)).Completed!, default));
        var claims = saved.SelectMany(output => output.Claims).ToArray();
        Assert.Equal(4, claims.Length);
        Assert.Contains(claims, claim => claim.Text.Contains("not approved"));
        var last = Assert.Single(claims, claim => claim.Text.Contains("defer"));
        Assert.Equal("[00:00:06.000] Decision: defer.", Assert.Single(last.Evidence).Text);
        Assert.Equal(6000, last.Evidence[0].StartMs);
        Assert.Contains("characters 0", last.Evidence[0].Reference);
        Assert.EndsWith("characters 0–31", last.Evidence[0].Reference);
        Assert.Contains("UNVERIFIED", result.Detail);
        Assert.Contains("whole-transcript", result.Detail);
    }

    [Fact]
    public async Task AnalyzeAsync_CompatibleLocalQuestion_ReusesDurableIndexWithoutExhaustiveOrAbsenceClaims()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First report.", "Second report.", "Third report.", "Last decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var vectors = new Vectors();
        await new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), vectors,
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);
        Assert.Equal(4, vectors.Calls);
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        var replacement = await store.BeginAsync(revision.Identity, default);
        var generation = new CandidateGeneration();

        var result = await new AnalysisProcessingService(store, replacement, files.Directory, generation, vectors,
            AnalysisProcessingMode.LocalQuestion).AnalyzeAsync(request with { Question = "Find a report." }, new Progress<string>(), default);

        Assert.Equal(5, vectors.Calls);
        Assert.Equal(3, result.Claims.Count);
        Assert.DoesNotContain(result.Claims, claim => claim.Text.Contains("Last decision"));
        Assert.Contains("not exhaustive", result.Detail);
        Assert.Contains("cannot establish absence", result.Detail);
        Assert.Equal(4, (await Collect(store.ReadChunksAsync(replacement, default))).Count);
        Assert.Equal(9, (await Collect(store.ReadOutputsAsync(replacement, default))).Count);
    }

    [Fact]
    public async Task AnalyzeAsync_FollowUp_UsesBoundedUserContextButNeverPreviousAssistantFactsOrAnotherAttempt()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana owns the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = files.Identity(request);
        var previous = await store.BeginAsync(identity, default);
        await store.CompleteAsync(previous, Stream(new AnalysisStoreOutput(0, "Discussion", "Who owns the draft?",
            "POISON: Marco owns everything.", [])), default);
        var revision = await store.BeginAsync(identity, default);
        var generation = new CandidateGeneration();
        var service = new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.GlobalQuestion, new TranscriptDiscussionContext(true, "the draft"));

        await service.AnalyzeAsync(request with { Question = "Who reviews it?" }, new Progress<string>(), default);

        var prompt = Assert.Single(generation.Prompts);
        Assert.Contains("Who owns the draft?", prompt);
        Assert.Contains("the draft", prompt);
        Assert.DoesNotContain("POISON", prompt);
        Assert.DoesNotContain("Marco", prompt);
        var other = request with { AttemptId = Guid.NewGuid() };
        var otherRevision = await store.BeginAsync(files.Identity(other), default);
        var unanswered = await new AnalysisProcessingService(store, otherRevision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.GlobalQuestion, new TranscriptDiscussionContext(true, "the draft"))
            .AnalyzeAsync(other with { Question = "Who reviews it?" }, new Progress<string>(), default);
        Assert.Empty(unanswered.Claims);
        Assert.Contains("antecedent", unanswered.Detail);
        Assert.Single(generation.Prompts);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordingIntentBoundary_HoldsEachNativeCallWithoutLosingTheCommittedResult()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana sends the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var boundary = new NativeBoundary();
        var generation = new CandidateGeneration { OnCall = () => Assert.Equal(1, boundary.Active) };
        var vectors = new Vectors { OnCall = () => Assert.Equal(1, boundary.Active) };
        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, vectors,
            AnalysisProcessingMode.Summary, enterInferenceAsync: boundary.Enter)
            .AnalyzeAsync(request, new Progress<string>(), default);
        Assert.Equal(2, boundary.Entries);
        Assert.Equal(0, boundary.Active);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(revision.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
    }

    [Fact]
    public async Task AnalyzeAsync_NativeProfileWithDifferentConfiguration_RejectsBeforeLoadingOrRetargetingModels()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana sends the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        ILocalTranscriptAnalysis service = new AnalysisProcessingNative(store, revision, "nonexistent-pinned-cache",
            files.Directory, AnalysisProcessingMode.Summary);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.AnalyzeAsync(request, new Progress<string>(), default));

        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
    }

    [Fact]
    public async Task AnalyzeAsync_PinnedNativePolicy_ValidatesBothRolesAndReleasesModelsAndRecordingLeases()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana sends the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(await AnalysisProcessingNative.PinAsync(request, default), default);
        var boundary = new NativeBoundary();
        var models = new List<NativeModel>();
        var verified = new List<string>();
        var phases = new List<string>();
        var service = new AnalysisProcessingNative(store, revision, files.Directory, files.Directory, AnalysisProcessingMode.Summary,
            enterInferenceAsync: boundary.Enter,
            loader: (settings, token) =>
            {
                Assert.Equal(1, boundary.Active);
                Assert.Equal(0, settings.GpuLayerCount);
                Assert.True(settings.NoKqvOffload);
                Assert.Equal(false, settings.OpOffload);
                var model = new NativeModel(settings.Embeddings);
                models.Add(model);
                return Task.FromResult<INativeModel>(model);
            },
            verifier: (directory, pinned, token) =>
            {
                Assert.Equal(files.Directory, directory);
                Assert.Equal(1, boundary.Active);
                var file = Assert.Single(pinned);
                var pinnedRole = file.Role == "generation" ? revision.Identity.Generation : revision.Identity.Embedding;
                Assert.Equal(pinnedRole.Id, file.Name);
                Assert.Equal(pinnedRole.Sha256, file.Sha256.ToUpperInvariant());
                verified.Add(file.Role);
                return Task.CompletedTask;
            });

        var result = await service.AnalyzeAsync(request, new CallbackProgress(phases.Add), default);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(new[] { "generation", "embedding" }, verified);
        Assert.Equal(2, models.Count);
        Assert.All(models, model => Assert.True(model.Disposed));
        Assert.Equal(4, boundary.Entries);
        Assert.Equal(0, boundary.Active);
        Assert.Equal(4, phases.Distinct().Count());
        Assert.All(phases, phase => Assert.False(string.IsNullOrWhiteSpace(phase)));
        Assert.Equal(revision.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
    }

    [Theory]
    [InlineData(GenerationFinish.TokenLimit, null, "TokenLimited")]
    [InlineData(GenerationFinish.Completed, "Conflicting reports could not be resolved.", "Partial")]
    public async Task AnalyzeAsync_IncompleteGeneration_PreservesPreviousCompletedEvidence(GenerationFinish finish,
        string? limitation, string expectedStatus)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana sends the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var previous = await store.BeginAsync(files.Identity(request), default);
        await new AnalysisProcessingService(store, previous, files.Directory, new CandidateGeneration(), new Vectors(),
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);
        var revision = await store.BeginAsync(previous.Identity, default);
        var generation = new CandidateGeneration { Finish = finish, Limitation = limitation };

        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.GlobalQuestion).AnalyzeAsync(request with { Question = "Was it approved?" }, new Progress<string>(), default);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Claims);
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        var restored = await store.LoadAsync(revision.Identity, default);
        Assert.Equal(previous.Id, restored.Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Failed, restored.Status);
        Assert.Equal("Ana sends the draft.", Assert.Single((await Collect(store.ReadOutputsAsync(previous, default))).SelectMany(output => output.Claims))
            .Evidence[0].Text[15..]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(store.ReadOutputsAsync(revision, default)));
    }

    [Fact]
    public async Task AnalyzeAsync_CancelledDuringIndex_ResumesExactCommittedPrefixWithoutReembeddingIt()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First.", "Second.", "Last.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var vectors = new Vectors { OnCall = () => { if (++calls == 2) cancellation.Cancel(); } };
        var service = new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), vectors,
            AnalysisProcessingMode.Summary);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(request, new Progress<string>(), cancellation.Token));
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        Assert.Equal(AnalysisStoreStatus.Cancelled, (await store.LoadAsync(revision.Identity, default)).Status);
        Assert.Equal(1, (await store.LoadAsync(revision.Identity, default)).Checkpoint!.NextOrdinal);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        await store.ResumeAsync(revision, default);

        var result = await service.AnalyzeAsync(request, new Progress<string>(), default);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(4, vectors.Calls);
        Assert.Equal(3, (await Collect(store.ReadChunksAsync(revision, default))).Count);
        Assert.False(System.IO.Directory.EnumerateFiles(files.Directory, "*.jsonl").Any());
    }

    [Theory]
    [InlineData("changed-text")]
    [InlineData("deleted-subtitles")]
    [InlineData("different-attempt")]
    public async Task AnalyzeAsync_StaleOrMissingSource_NeverRetargetsEvidence(string failure)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana sends the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        if (failure == "changed-text") await File.AppendAllTextAsync(request.Outputs.TextPath, "changed");
        if (failure == "deleted-subtitles") File.Delete(request.Outputs.SubtitlePath);
        if (failure == "different-attempt") request = request with { AttemptId = Guid.NewGuid() };
        var generation = new CandidateGeneration();

        await Assert.ThrowsAsync(failure == "deleted-subtitles" ? typeof(FileNotFoundException) : typeof(InvalidDataException),
            () => new AnalysisProcessingService(store, revision, files.Directory,
            generation, new Vectors(), AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));

        Assert.Empty(generation.Prompts);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
    }

    [Fact]
    public async Task AnalyzeAsync_TokenFittedPortugueseSource_PreservesEveryOriginalScalarAndCueOffset()
    {
        using var files = new ProcessingFiles();
        var original = string.Concat(Enumerable.Repeat("João 👋 mantém o prazo condicionado. ", 35));
        var request = (await files.Source(original)) with { Language = "Brazilian Portuguese" };
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var vectors = new Vectors { TokenDivisor = 1 };
        var generation = new CandidateGeneration { StatementLimit = 80 };

        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, vectors,
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);

        Assert.Equal("Completed", result.Status);
        var chunks = await Collect(store.ReadChunksAsync(revision, default));
        Assert.True(chunks.Count > 3);
        var exact = File.ReadAllText(request.Outputs.TextPath);
        Assert.Equal(exact, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.All(chunks, chunk => Assert.Equal(exact.Substring((int)chunk.Offset, chunk.Text.Length), chunk.Text));
        Assert.All(vectors.Inputs, input => Assert.True(input.Length <= 512));
        var saved = await Collect(store.ReadOutputsAsync(revision, default));
        Assert.All(saved.SelectMany(output => output.Claims).SelectMany(claim => claim.Evidence),
            evidence => Assert.Contains(evidence.Text, exact));
        Assert.Contains(saved.SelectMany(output => output.Claims).SelectMany(claim => claim.Evidence),
            evidence => evidence.Reference.Contains("characters 256"));
        Assert.All(generation.Prompts, prompt =>
        {
            Assert.Contains("pt-BR", prompt);
            Assert.True(generation.CountTokens(prompt) + 1024 < 4096);
        });
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedRevisionCannotRunAgain_PreservesActivatedEvidence()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("A retained decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var generation = new CandidateGeneration();
        var service = new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.Summary);
        await service.AnalyzeAsync(request, new Progress<string>(), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(request, new Progress<string>(), default));

        Assert.Equal(revision.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        Assert.Single(generation.Prompts);
    }

    [Theory]
    [InlineData("long-question")]
    [InlineData("empty-question")]
    [InlineData("summary-question")]
    [InlineData("question-without-question")]
    [InlineData("undefined-mode")]
    [InlineData("unsupported-language")]
    public async Task AnalyzeAsync_InvalidIntent_RejectsWithoutInferenceOrPublishing(string failure)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("A retained decision.");
        if (failure == "unsupported-language") request = request with { Language = "unknown" };
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var mode = failure switch
        {
            "long-question" or "empty-question" or "question-without-question" => AnalysisProcessingMode.GlobalQuestion,
            "undefined-mode" => (AnalysisProcessingMode)99,
            _ => AnalysisProcessingMode.Summary
        };
        var question = failure switch
        {
            "long-question" => new string('q', 513),
            "empty-question" => " ",
            "summary-question" => "What was agreed?",
            _ => null
        };
        var generation = new CandidateGeneration();
        await Assert.ThrowsAsync<ArgumentException>(() => new AnalysisProcessingService(store, revision, files.Directory,
            generation, new Vectors(), mode).AnalyzeAsync(request with { Question = question }, new Progress<string>(), default));
        Assert.Empty(generation.Prompts);
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
    }

    [Fact]
    public async Task AnalyzeAsync_LocalLookup_UsesNormalizedTaskVectorsAndReturnsOnlyRankedFreshEvidence()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First report.", "Second report.", "Third report.", "Last decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var vectors = new Vectors
        {
            GetVector = input =>
            {
                if (input.StartsWith("search_query: ")) return [1, 1];
                Assert.StartsWith("search_document: ", input);
                if (input.Contains("First")) return [9, 1];
                if (input.Contains("Second")) return [1, 8];
                if (input.Contains("Third")) return [8, 2];
                return [5, 6];
            }
        };
        var result = await new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), vectors,
            AnalysisProcessingMode.LocalQuestion).AnalyzeAsync(request with { Question = "Find reports." }, new Progress<string>(), default);

        Assert.Contains("Last decision", result.Claims[0].Text);
        Assert.Contains("Third report", result.Claims[1].Text);
        Assert.Contains("Second report", result.Claims[2].Text);
        Assert.DoesNotContain(result.Claims, claim => claim.Text.Contains("First report"));
        var indexed = await Collect(store.ReadChunksAsync(revision, default));
        Assert.Equal(9 / Math.Sqrt(82), indexed[0].Vector[0], 6);
        Assert.Equal(1 / Math.Sqrt(82), indexed[0].Vector[1], 6);
    }

    [Theory]
    [InlineData("dimensions")]
    [InlineData("nonfinite")]
    [InlineData("zero")]
    [InlineData("query-dimensions")]
    [InlineData("query-nonfinite")]
    [InlineData("query-zero")]
    public async Task AnalyzeAsync_IncompatibleEmbedding_LeavesNoCompletedOrInvalidIndex(string failure)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("An explicit decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var vectors = new Vectors
        {
            GetVector = input => failure.StartsWith("query-") && input.StartsWith("search_document: ") ? [1, 0]
                : failure switch { "dimensions" or "query-dimensions" => [1], "nonfinite" or "query-nonfinite" => [1, float.NaN], _ => [0, 0] }
        };
        var mode = failure.StartsWith("query-") ? AnalysisProcessingMode.LocalQuestion : AnalysisProcessingMode.Summary;
        var question = failure.StartsWith("query-") ? "Find the decision." : null;
        var service = new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), vectors, mode);
        if (failure.StartsWith("query-"))
            Assert.Equal("Failed", (await service.AnalyzeAsync(request with { Question = question }, new Progress<string>(), default)).Status);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => service.AnalyzeAsync(request, new Progress<string>(), default));
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        Assert.Equal(failure.StartsWith("query-") ? 1 : 0, (await Collect(store.ReadChunksAsync(revision, default))).Count);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("text")]
    [InlineData("offset")]
    [InlineData("time")]
    [InlineData("vector")]
    [InlineData("cursor")]
    public async Task AnalyzeAsync_IncompatibleCheckpoint_RejectsRatherThanReusingRetargetedEvidence(string failure)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("An explicit decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var chunk = new AnalysisStoreChunk(0, "cue-1 [00:00:00,000 --> 00:00:01,000]", 0, 0, 1000,
            "[00:00:00.000] An explicit decision.", [1, 0]);
        chunk = failure switch
        {
            "source" => chunk with { SourceId = "other-source" },
            "text" => chunk with { Text = "different text" },
            "offset" => chunk with { Offset = 1 },
            "time" => chunk with { StartMs = 1 },
            "vector" => chunk with { Vector = [] },
            _ => chunk
        };
        await store.SaveCheckpointAsync(revision, new(1, "Index", failure == "cursor" ? "obsolete" : "processing-v1"), Stream(chunk), default);
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Interrupted, default);
        await store.ResumeAsync(revision, default);
        var generation = new CandidateGeneration();
        await Assert.ThrowsAsync<InvalidDataException>(() => new AnalysisProcessingService(store, revision, files.Directory,
            generation, new Vectors(), AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));
        Assert.Empty(generation.Prompts);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
    }

    [Fact]
    public async Task AnalyzeAsync_NoGlobalCandidate_PersistsInsufficientEvidenceNotAnAbsenceAnswer()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("We discussed the weather.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var result = await new AnalysisProcessingService(store, revision, files.Directory,
            new CandidateGeneration { NoClaims = true, Limitation = "Insufficient evidence for deadlines in these excerpts." },
            new Vectors(), AnalysisProcessingMode.GlobalQuestion).AnalyzeAsync(request with { Question = "What deadlines were agreed?" }, new Progress<string>(), default);
        Assert.Empty(result.Claims);
        var outputs = await Collect(store.ReadOutputsAsync(revision, default));
        Assert.All(outputs, output => Assert.Equal("Discussion", output.Kind));
        Assert.All(outputs, output => Assert.Equal("What deadlines were agreed?", output.Question));
        Assert.Contains(outputs, output => output.Content.Contains("Insufficient evidence") && output.Content.Contains("does not prove"));
    }

    [Fact]
    public async Task AnalyzeAsync_Summary_PersistsStructuredQualificationsWithoutDestructiveProseReduction()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana sends the draft Friday, only if the budget is approved.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var generation = new CandidateGeneration
        {
            Response = """
                {"claims":[{"kind":"Action","statement":"Send the draft","owner":{"state":"Stated","value":"Ana"},
                "deadline":{"state":"Stated","value":"Friday"},"qualifiers":["only if budget approved"],"references":[0]}],"limitation":null}
                """
        };
        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);
        var claim = Assert.Single(result.Claims);
        Assert.Contains("Owner: Ana", claim.Text);
        Assert.Contains("Deadline: Friday", claim.Text);
        Assert.Contains("only if budget approved", claim.Text);
        Assert.Equal(File.ReadAllText(request.Outputs.TextPath), Assert.Single(claim.Evidence).Text);
        var saved = await Collect(SqliteTranscriptAnalysisStore.Open(files.Database).ReadOutputsAsync(revision, default));
        Assert.All(saved, output => Assert.Equal("Summary", output.Kind));
        Assert.All(saved, output => Assert.Null(output.Question));
        var structured = JsonSerializer.Deserialize<StructuredClaim>(Assert.Single(saved, output => output.Claims.Count > 0).Content)!;
        Assert.Equal("Action", structured.Kind);
        Assert.Equal("Send the draft", structured.Statement);
        Assert.Equal(new ClaimField("Stated", "Ana"), structured.Owner);
        Assert.Equal(new ClaimField("Stated", "Friday"), structured.Deadline);
        Assert.Equal(new[] { "only if budget approved" }, structured.Qualifiers);
        Assert.Single(generation.Prompts);
    }

    [Fact]
    public async Task AnalyzeAsync_MaximumFollowUpContext_RetainsOnlyFourRecentUserQuestionsAndFreshSource()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Fresh source: Ana reviews the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        var history = Enumerable.Range(0, 8).Select(index => new AnalysisStoreOutput(index, "Discussion",
            $"Question {index}: " + new string('q', 100), "POISON: generated earlier facts.", [])).ToArray();
        await store.CompleteAsync(prior, history.ToAsyncEnumerable(), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var generation = new CandidateGeneration();
        var requestQuestion = new string('Q', 512);

        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.GlobalQuestion, new TranscriptDiscussionContext(true, new string('a', 128)))
            .AnalyzeAsync(request with { Question = requestQuestion }, new Progress<string>(), default);

        Assert.Equal("Completed", result.Status);
        using var json = JsonDocument.Parse(Assert.Single(generation.Prompts));
        var question = json.RootElement.GetProperty("question").GetString()!;
        Assert.True(question.Length <= 1024);
        Assert.Contains(requestQuestion, question);
        Assert.DoesNotContain("Question 3", question);
        Assert.Contains("Question 4", question);
        Assert.Contains("Question 7", question);
        Assert.DoesNotContain("POISON", generation.Prompts[0]);
        Assert.Contains("Fresh source", generation.Prompts[0]);
        var saved = await Collect(store.ReadOutputsAsync(revision, default));
        Assert.Equal(history[0].Content, saved[0].Content);
        Assert.Equal(requestQuestion, saved[^1].Question);
    }

    [Fact]
    public async Task AnalyzeAsync_CancelledAtPublication_PreservesPreviousCompleteAndCompatibleFullIndex()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("A retained decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await new AnalysisProcessingService(store, prior, files.Directory, new CandidateGeneration(), new Vectors(),
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(phase => { if (phase.StartsWith("Committing")) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisProcessingService(store, revision,
            files.Directory, new CandidateGeneration(), new Vectors(), AnalysisProcessingMode.GlobalQuestion)
            .AnalyzeAsync(request with { Question = "What happened?" }, progress, cancellation.Token));
        var saved = await SqliteTranscriptAnalysisStore.Open(files.Database).LoadAsync(revision.Identity, default);
        Assert.Equal(prior.Id, saved.Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Cancelled, saved.Status);
        Assert.Equal("Indexed", saved.Checkpoint!.Stage);
        Assert.Equal(1, saved.Checkpoint.NextOrdinal);
        await store.ResumeAsync(revision, default);
        var reusedVectors = new Vectors();
        var resumed = await new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(),
            reusedVectors, AnalysisProcessingMode.GlobalQuestion)
            .AnalyzeAsync(request with { Question = "What happened?" }, new Progress<string>(), default);
        Assert.Equal("Completed", resumed.Status);
        Assert.Equal(0, reusedVectors.Calls);
        Assert.Equal(revision.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("subtitles")]
    [InlineData("artifacts")]
    public async Task AnalyzeAsync_NetworkBoundary_RejectsWithoutReadingOrCopyingPrivateContent(string destination)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Private source.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        const string network = @"\\not-contacted.invalid\share\private";
        var artifactDirectory = destination == "artifacts" ? network : files.Directory;
        request = destination switch
        {
            "text" => request with { Outputs = request.Outputs with { TextPath = network } },
            "subtitles" => request with { Outputs = request.Outputs with { SubtitlePath = network } },
            _ => request
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AnalysisProcessingService(store, revision, artifactDirectory,
            new CandidateGeneration(), new Vectors(), AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));
        if (destination != "artifacts")
            await Assert.ThrowsAsync<InvalidOperationException>(() => AnalysisProcessingNative.PinAsync(request, default));
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
    }

    [Fact]
    public async Task AnalyzeAsync_PinnedModelsDisappear_FailsExplicitlyWithoutDownloadOrModelSubstitution()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Private source.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(await AnalysisProcessingNative.PinAsync(request, default), default);
        var before = System.IO.Directory.GetFiles(files.Directory).Order().ToArray();
        await Assert.ThrowsAsync<FileNotFoundException>(() => new AnalysisProcessingNative(store, revision, files.Directory,
            files.Directory, AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));
        Assert.Equal(before, System.IO.Directory.GetFiles(files.Directory).Order().ToArray());
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
    }

    [Fact]
    public async Task AnalyzeAsync_NativeRunCancelledBeforeLoading_RetainsAnExplicitCancelledRevision()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Private source.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(await AnalysisProcessingNative.PinAsync(request, default), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisProcessingNative(store, revision, files.Directory,
            files.Directory, AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), cancellation.Token));
        Assert.Equal(AnalysisStoreStatus.Cancelled, (await store.LoadAsync(revision.Identity, default)).Status);
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
    }

    [Theory]
    [InlineData(498, true)]
    [InlineData(499, false)]
    public async Task AnalyzeAsync_EmbeddingBudgetIncludesTaskPrefix_NeverSilentlyTruncatesTheQuestion(int questionLength, bool fits)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source(new string('x', 480));
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var vectors = new Vectors { TokenDivisor = 1 };
        var question = new string('q', questionLength);
        var service = new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), vectors,
            AnalysisProcessingMode.LocalQuestion);
        if (fits)
        {
            var result = await service.AnalyzeAsync(request with { Question = question }, new Progress<string>(), default);
            Assert.Equal("Completed", result.Status);
            Assert.Equal(question, (await Collect(store.ReadOutputsAsync(revision, default)))[0].Question);
            Assert.Equal(2, vectors.Calls);
        }
        else
        {
            var result = await service.AnalyzeAsync(request with { Question = question }, new Progress<string>(), default);
            Assert.Equal("Failed", result.Status);
            Assert.Contains("budget", result.Detail);
            Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
            Assert.Equal(1, vectors.Calls);
        }
        Assert.Equal(512, vectors.Inputs[0].Length);
        Assert.Single(await Collect(store.ReadChunksAsync(revision, default)));
    }

    [Fact]
    public async Task AnalyzeAsync_MissingCommittedPrefix_NeverPublishesFalseGlobalCoverageFromRemainingChunks()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First report.", "Last decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisProcessingService(store, revision,
            files.Directory, new CandidateGeneration(), new Vectors(), AnalysisProcessingMode.Summary)
            .AnalyzeAsync(request, new CallbackProgress(phase => { if (phase.StartsWith("Generating")) cancellation.Cancel(); }), cancellation.Token));
        using (var corrupt = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + files.Database + ";Pooling=False"))
        {
            corrupt.Open();
            using var command = corrupt.CreateCommand();
            command.CommandText = "DELETE FROM chunks WHERE ordinal=1";
            command.ExecuteNonQuery();
        }
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        await store.ResumeAsync(revision, default);
        var generation = new CandidateGeneration();

        await Assert.ThrowsAsync<InvalidDataException>(() => new AnalysisProcessingService(store, revision, files.Directory,
            generation, new Vectors(), AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));

        Assert.Empty(generation.Prompts);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAsync_InactiveWriter_IsRejectedBeforeAnyNativeCallOrStateChange(bool newerWriter)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Private source.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, default);
        var latest = newerWriter ? await store.BeginAsync(revision.Identity, default) : revision;
        var vectors = new Vectors();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AnalysisProcessingService(store, revision, files.Directory,
            new CandidateGeneration(), vectors, AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));
        Assert.Equal(0, vectors.Calls);
        var saved = await store.LoadAsync(revision.Identity, default);
        Assert.Equal(latest.Id, saved.Unfinished!.Id);
        Assert.Equal(newerWriter ? AnalysisStoreStatus.Processing : AnalysisStoreStatus.Cancelled, saved.Status);
    }

    [Fact]
    public async Task AnalyzeAsync_UnicodeAtNativeRangeBoundaries_PreservesExactSourceAndBoundedContext()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source(new string('x', 1008) + "👋 fim.", new string('y', 496) + "👋 " + new string('z', 520));
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, new[]
        {
            new AnalysisStoreOutput(0, "Summary", "POISON-question", "Generated text is not evidence.", []),
            new AnalysisStoreOutput(1, "Discussion", null, "Generated text is not context.", []),
            new AnalysisStoreOutput(2, "Discussion", new string('q', 47) + "👋", "POISON-answer", [])
        }.ToAsyncEnumerable(), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var generation = new CandidateGeneration { StatementLimit = 80 };
        var vectors = new Vectors { TokenDivisor = 1 };
        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, vectors,
            AnalysisProcessingMode.GlobalQuestion, new TranscriptDiscussionContext(true, "the draft"))
            .AnalyzeAsync(request with { Question = "What about it?" }, new Progress<string>(), default);
        Assert.Equal("Completed", result.Status);
        var chunks = await Collect(store.ReadChunksAsync(revision, default));
        var expected = File.ReadAllText(request.Outputs.TextPath).Split(Environment.NewLine);
        Assert.Equal(expected[0], string.Concat(chunks.Where(chunk => chunk.SourceId.StartsWith("cue-1")).Select(chunk => chunk.Text)));
        Assert.Equal(expected[1], string.Concat(chunks.Where(chunk => chunk.SourceId.StartsWith("cue-2")).Select(chunk => chunk.Text)));
        using var prompt = JsonDocument.Parse(generation.Prompts[0]);
        var question = prompt.RootElement.GetProperty("question").GetString()!;
        Assert.DoesNotContain("POISON", question);
        Assert.DoesNotContain("👋", question);
        Assert.Contains(new string('q', 47), question);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("too-long")]
    public async Task AnalyzeAsync_FollowUpNeedsExplicitBoundedAntecedent_NeverInfersItFromAssistantHistory(string? antecedent)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana reviews the draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Discussion", "Who owns the draft?", "POISON-fact", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var generation = new CandidateGeneration();
        var service = new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.GlobalQuestion, new TranscriptDiscussionContext(true, antecedent == "too-long" ? new string('a', 129) : antecedent));
        if (antecedent == "too-long")
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(request with { Question = "Who reviews it?" }, new Progress<string>(), default));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.Equal(prior.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        }
        else
        {
            var result = await service.AnalyzeAsync(request with { Question = "Who reviews it?" }, new Progress<string>(), default);
            Assert.Empty(result.Claims);
            Assert.Contains("Clarification", result.Detail);
            var outputs = await Collect(store.ReadOutputsAsync(revision, default));
            Assert.Equal("POISON-fact", outputs[0].Content);
            Assert.Equal("Who reviews it?", outputs[1].Question);
            Assert.Contains("antecedent", outputs[1].Content);
        }
        Assert.Empty(generation.Prompts);
    }

    [Fact]
    public async Task AnalyzeAsync_GenerationFittedRanges_ResolveOriginalCueOffsetsRatherThanChunkRelativePositions()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source(new string('x', 1008) + "👋 fim.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var generation = new CandidateGeneration { TokenDivisor = 1, TokenOverhead = 768, StatementLimit = 80 };
        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);
        Assert.Equal("Completed", result.Status);
        var original = File.ReadAllText(request.Outputs.TextPath);
        var references = (await Collect(store.ReadOutputsAsync(revision, default))).SelectMany(output => output.Claims)
            .SelectMany(claim => claim.Evidence).ToArray();
        var starts = new List<int>();
        foreach (var evidence in references)
        {
            var range = System.Text.RegularExpressions.Regex.Match(evidence.Reference, @"characters (\d+)–(\d+)$");
            Assert.True(range.Success);
            var start = int.Parse(range.Groups[1].Value);
            var end = int.Parse(range.Groups[2].Value);
            starts.Add(start);
            Assert.Equal(original.Substring(start, end - start), evidence.Text);
            Assert.Equal(0, evidence.StartMs);
        }
        Assert.Contains(starts, start => start > 0);
        Assert.NotEmpty(result.Claims.SelectMany(claim => claim.Evidence));
        using var ledger = ClaimLedger.Open(result.LedgerPath);
        Assert.Equal(request.AttemptId.ToString("N"), ledger.ReadRun().Identity.SuccessfulTranscriptionAttemptId);
        Assert.Equal(revision.Identity.TextSha256, ledger.ReadRun().Identity.TranscriptSha256);
        Assert.Contains(ledger.ReadClaims().SelectMany(claim => ledger.ReadEvidence(claim.Id)), evidence => evidence.Offset > 0);
    }

    private static async IAsyncEnumerable<T> Stream<T>(T value)
    {
        yield return value;
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(AnalysisProcessingMode.GlobalQuestion, 0)]
    [InlineData(AnalysisProcessingMode.GlobalQuestion, 1)]
    [InlineData(AnalysisProcessingMode.LocalQuestion, 0)]
    [InlineData(AnalysisProcessingMode.LocalQuestion, 1)]
    public async Task AnalyzeAsync_IndexLosesCommittedRangeAfterIndexing_NeverActivatesIncompleteSourceCoverage(
        AnalysisProcessingMode mode, int missingOrdinal)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First report.", "Final decision: postpone the launch.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await new AnalysisProcessingService(store, prior, files.Directory, new CandidateGeneration(), new Vectors(),
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var damaged = false;
        var progress = new CallbackProgress(_ =>
        {
            if (damaged || store.LoadAsync(revision.Identity, default).GetAwaiter().GetResult().Checkpoint?.Stage != "Indexed") return;
            damaged = true;
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + files.Database + ";Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chunks WHERE revision=$revision AND ordinal=$ordinal";
            command.Parameters.AddWithValue("$revision", revision.Id.ToString());
            command.Parameters.AddWithValue("$ordinal", missingOrdinal);
            command.ExecuteNonQuery();
        });

        var result = await new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), new Vectors(), mode)
            .AnalyzeAsync(request with { Question = "What was agreed?" }, progress, default);

        Assert.True(damaged);
        Assert.Equal("Failed", result.Status);
        Assert.Empty(result.Claims);
        var restored = await SqliteTranscriptAnalysisStore.Open(files.Database).LoadAsync(revision.Identity, default);
        Assert.Equal(prior.Id, restored.Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Failed, restored.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(store.ReadOutputsAsync(revision, default)));
    }

    [Fact]
    public async Task AnalyzeAsync_RetainedIndexHasUnownedTrailingRange_RejectsReuseAndPreservesPriorActivation()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        var indexed = new[]
        {
            new AnalysisStoreChunk(0, "cue-1 [00:00:00,000 --> 00:00:01,000]", 0, 0, 1000,
                "[00:00:00.000] The selected transcript.", [1, 0]),
            new AnalysisStoreChunk(1, "unowned-cue", 0, 1000, 2000, "An unrelated decision.", [1, 0])
        };
        await store.SaveCheckpointAsync(prior, new(2, "Indexed", "processing-v1"), indexed.ToAsyncEnumerable(), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Summary", null, "Prior retained result.", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var generation = new CandidateGeneration();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new AnalysisProcessingService(store, revision,
            files.Directory, generation, new Vectors(), AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(generation.Prompts);
        Assert.Equal(prior.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnalyzeAsync_CancelledBeforeDeferredCaptureLeaseIsGranted_DoesNotEnterNativeBackend(int heldEntry)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Summary", null, "Prior retained result.", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;
        var releases = 0;
        Task<IDisposable> Enter(CancellationToken _)
        {
            if (++entries != heldEntry) return Task.FromResult<IDisposable>(new Lease(() => releases++));
            reached.SetResult();
            return grant.Task;
        }
        var vectors = new Vectors();
        var generation = new CandidateGeneration();
        var work = new AnalysisProcessingService(store, revision, files.Directory, generation, vectors,
            AnalysisProcessingMode.Summary, enterInferenceAsync: Enter).AnalyzeAsync(request, new Progress<string>(), cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(prior.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        cancellation.Cancel();
        grant.SetResult(new Lease(() => releases++));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

        Assert.Equal(heldEntry - 1, vectors.Calls);
        Assert.Empty(generation.Prompts);
        Assert.Equal(entries, releases);
        var restored = await SqliteTranscriptAnalysisStore.Open(files.Database).LoadAsync(revision.Identity, default);
        Assert.Equal(prior.Id, restored.Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Cancelled, restored.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnalyzeAsync_CancelledDuringModelLeaseWait_DoesNotLoadAnotherModel(int heldEntry)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(await AnalysisProcessingNative.PinAsync(request, default), default);
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;
        var releases = 0;
        Task<IDisposable> Enter(CancellationToken _)
        {
            if (++entries != heldEntry) return Task.FromResult<IDisposable>(new Lease(() => releases++));
            reached.SetResult();
            return grant.Task;
        }
        var models = new List<NativeModel>();
        var service = new AnalysisProcessingNative(store, revision, files.Directory, files.Directory, AnalysisProcessingMode.Summary,
            enterInferenceAsync: Enter,
            loader: (settings, _) =>
            {
                var model = new NativeModel(settings.Embeddings);
                models.Add(model);
                return Task.FromResult<INativeModel>(model);
            }, verifier: (_, _, _) => Task.CompletedTask);
        var work = service.AnalyzeAsync(request, new Progress<string>(), cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        grant.SetResult(new Lease(() => releases++));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

        Assert.Equal(heldEntry - 1, models.Count);
        Assert.All(models, model => Assert.True(model.Disposed));
        Assert.Equal(entries, releases);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        Assert.Equal(AnalysisStoreStatus.Cancelled, (await store.LoadAsync(revision.Identity, default)).Status);
    }

    [Fact]
    public async Task AnalyzeAsync_AsymmetricQuery_RanksCosineEvidenceRatherThanInverseVectorComponents()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First report.", "Second report.", "Third report.", "Last decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var vectors = new Vectors
        {
            GetVector = input =>
            {
                if (input.StartsWith("search_query: ")) return [1, 3];
                if (input.Contains("First")) return [9, 1];
                if (input.Contains("Second")) return [1, 8];
                if (input.Contains("Third")) return [8, 2];
                return [5, 6];
            }
        };

        var result = await new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), vectors,
            AnalysisProcessingMode.LocalQuestion).AnalyzeAsync(request with { Question = "Find the relevant reports." }, new Progress<string>(), default);

        Assert.Equal(new[] { "[00:00:02.000] Second report.", "[00:00:06.000] Last decision.", "[00:00:04.000] Third report." },
            result.Claims.Select(claim => Assert.Single(claim.Evidence).Text).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAsync_NativeDirectoryBoundary_UsesOnlyThePinnedLocalDirectoryWithoutImplicitLease(bool network)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(await AnalysisProcessingNative.PinAsync(request, default), default);
        var directory = network ? @"\\not-contacted.invalid\share\models" : files.Directory;
        var loaded = new List<NativeModel>();
        var service = new AnalysisProcessingNative(store, revision, directory, files.Directory, AnalysisProcessingMode.Summary,
            loader: (settings, _) =>
            {
                Assert.Equal(files.Directory, Path.GetDirectoryName(settings.ModelPath));
                var model = new NativeModel(settings.Embeddings);
                loaded.Add(model);
                return Task.FromResult<INativeModel>(model);
            }, verifier: (actual, _, _) =>
            {
                Assert.Equal(files.Directory, actual);
                return Task.CompletedTask;
            });
        if (network)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(request, new Progress<string>(), default));
            Assert.Empty(loaded);
            Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        }
        else
        {
            Assert.Equal("Completed", (await service.AnalyzeAsync(request, new Progress<string>(), default)).Status);
            Assert.Equal(2, loaded.Count);
            Assert.All(loaded, model => Assert.True(model.Disposed));
            Assert.Equal(revision.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        }
    }

    [Theory]
    [InlineData(AnalysisProcessingMode.GlobalQuestion)]
    [InlineData(AnalysisProcessingMode.LocalQuestion)]
    public async Task AnalyzeAsync_PriorAssistantClaimsAndCitationsArePoisoned_NewEvidenceStillComesOnlyFromSavedSource(AnalysisProcessingMode mode)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("Ana reviews the selected draft.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        var poison = new TranscriptAnalysisClaim("Marco has approved everything.",
            [new("invented prior evidence", null, "Assistant invented a different transcript.")]);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Discussion", "Who reviews the draft?", "Invented assistant facts.", [poison])), default);
        var revision = await store.BeginAsync(prior.Identity, default);

        var result = await new AnalysisProcessingService(store, revision, files.Directory, new CandidateGeneration(), new Vectors(),
            mode, new TranscriptDiscussionContext(true, "the draft"))
            .AnalyzeAsync(request with { Question = "Who reviews it?" }, new Progress<string>(), default);

        var fresh = Assert.Single(result.Claims);
        Assert.Equal("[00:00:00.000] Ana reviews the selected draft.", Assert.Single(fresh.Evidence).Text);
        var restored = await Collect(SqliteTranscriptAnalysisStore.Open(files.Database).ReadOutputsAsync(revision, default));
        Assert.Equal(poison.Text, Assert.Single(restored[0].Claims).Text);
        Assert.Equal(fresh.Evidence, Assert.Single(restored[^1].Claims).Evidence);
        Assert.Equal("Who reviews it?", restored[^1].Question);
    }

    [Fact]
    public async Task AnalyzeAsync_FinalSourceIsStillBeingIndexed_DoesNotStartGenerationOrReplacePreviousCompletion()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First report.", "Second report.", "Final decision: postpone the launch.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Summary", null, "Prior retained result.", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vectors = new Vectors
        {
            BeforeReturn = input =>
            {
                if (!input.Contains("Final decision")) return Task.CompletedTask;
                reached.SetResult();
                return finish.Task;
            }
        };
        var generation = new CandidateGeneration();
        var work = new AnalysisProcessingService(store, revision, files.Directory, generation, vectors,
            AnalysisProcessingMode.GlobalQuestion).AnalyzeAsync(request with { Question = "What was agreed?" }, new Progress<string>(), default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var unfinished = await store.LoadAsync(revision.Identity, default);
        Assert.Equal(prior.Id, unfinished.Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Processing, unfinished.Status);
        Assert.Equal(2, unfinished.Checkpoint!.NextOrdinal);
        Assert.Empty(generation.Prompts);
        finish.SetResult();

        var result = await work;

        Assert.Equal("Completed", result.Status);
        Assert.Equal(3, (await Collect(store.ReadChunksAsync(revision, default))).Count);
        Assert.Equal(revision.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        Assert.Contains(result.Claims.SelectMany(claim => claim.Evidence),
            evidence => evidence.Text == "[00:00:04.000] Final decision: postpone the launch.");
    }

    [Fact]
    public async Task AnalyzeAsync_CurrentGenerationFinishesAfterCancellation_DoesNotCommitItsCandidateClaims()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Summary", null, "Prior retained result.", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        using var cancellation = new CancellationTokenSource();
        var boundary = new NativeBoundary();
        var generation = new CandidateGeneration { OnCall = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisProcessingService(store, revision,
            files.Directory, generation, new Vectors(), AnalysisProcessingMode.GlobalQuestion, enterInferenceAsync: boundary.Enter)
            .AnalyzeAsync(request with { Question = "What was agreed?" }, new Progress<string>(), cancellation.Token));

        Assert.Single(generation.Prompts);
        Assert.Equal(0, boundary.Active);
        var restored = await SqliteTranscriptAnalysisStore.Open(files.Database).LoadAsync(revision.Identity, default);
        Assert.Equal(prior.Id, restored.Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Cancelled, restored.Status);
        Assert.Equal(1, restored.Checkpoint!.NextOrdinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(store.ReadOutputsAsync(revision, default)));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("embedding")]
    [InlineData("configuration")]
    [InlineData("attempt")]
    public async Task AnalyzeAsync_OnePinnedIdentityFieldIsDifferent_RejectsBeforeLeaseOrNativeLoading(string changed)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var pinned = await AnalysisProcessingNative.PinAsync(request, default);
        var configuration = """{"selectedBeforeQueueing":false}""";
        var identity = changed switch
        {
            "generation" => pinned with { Generation = pinned.Generation with { Sha256 = new('A', 64) } },
            "embedding" => pinned with { Embedding = pinned.Embedding with { Sha256 = new('B', 64) } },
            "configuration" => pinned with { Generation = pinned.Generation with
            {
                CanonicalConfiguration = configuration,
                ConfigurationSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)))
            } },
            _ => pinned with { AttemptId = Guid.NewGuid() }
        };
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(identity, default);
        var boundary = new NativeBoundary();
        var loads = 0;
        var service = new AnalysisProcessingNative(store, revision, files.Directory, files.Directory, AnalysisProcessingMode.Summary,
            enterInferenceAsync: boundary.Enter, loader: (settings, _) =>
            {
                loads++;
                return Task.FromResult<INativeModel>(new NativeModel(settings.Embeddings));
            }, verifier: (_, _, _) => Task.CompletedTask);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.AnalyzeAsync(request, new Progress<string>(), default));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(0, loads);
        Assert.Equal(0, boundary.Entries);
        Assert.Null((await store.LoadAsync(identity, default)).Completed);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(identity, default)).Status);
    }

    [Fact]
    public async Task AnalyzeAsync_NoOriginalScalarFitsEmbeddingBudget_FailsWithoutTruncatingOrEnteringTheBackend()
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Summary", null, "Prior retained result.", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var vectors = new Vectors { ConstantTokenCount = 513 };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new AnalysisProcessingService(store, revision, files.Directory,
            new CandidateGeneration(), vectors, AnalysisProcessingMode.Summary).AnalyzeAsync(request, new Progress<string>(), default));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(0, vectors.Calls);
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
        Assert.Equal(prior.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
    }

    [Theory]
    [InlineData(GenerationFinish.TokenLimit, null, "TokenLimited")]
    [InlineData(GenerationFinish.Completed, "Final ranges remain uncertain.", "Partial")]
    public async Task AnalyzeAsync_LaterGlobalBatchIsIncomplete_DoesNotActivateEarlierCandidateRecordsAsFullResult(
        GenerationFinish finish, string? limitation, string status)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("First.", "Second.", "Third.", "Fourth.", "Fifth.", "Sixth.", "Final decision.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var prior = await store.BeginAsync(files.Identity(request), default);
        await store.CompleteAsync(prior, Stream(new AnalysisStoreOutput(0, "Summary", null, "Prior retained result.", [])), default);
        var revision = await store.BeginAsync(prior.Identity, default);
        var generation = new CandidateGeneration
        {
            FinishForCall = call => call == 1 ? GenerationFinish.Completed : finish,
            LimitationForCall = call => call == 1 ? null : limitation
        };

        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.GlobalQuestion).AnalyzeAsync(request with { Question = "What was agreed across the transcript?" }, new Progress<string>(), default);

        Assert.Equal(status, result.Status);
        Assert.Empty(result.Claims);
        Assert.Equal(2, generation.Prompts.Count);
        using var ledger = ClaimLedger.Open(result.LedgerPath);
        Assert.True(ledger.ReadClaims().Any());
        Assert.Equal(prior.Id, (await store.LoadAsync(revision.Identity, default)).Completed!.Id);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
        Assert.Equal(7, (await Collect(store.ReadChunksAsync(revision, default))).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(store.ReadOutputsAsync(revision, default)));
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("algorithm")]
    public async Task AnalyzeAsync_IndexedCheckpointChangesBeforeInference_RejectsStaleProcessingState(string changed)
    {
        using var files = new ProcessingFiles();
        var request = await files.Source("The selected transcript.");
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(files.Identity(request), default);
        var changedOnce = false;
        var generation = new CandidateGeneration();
        var progress = new CallbackProgress(_ =>
        {
            var checkpoint = store.LoadAsync(revision.Identity, default).GetAwaiter().GetResult().Checkpoint;
            if (changedOnce || checkpoint?.Stage != "Indexed") return;
            changedOnce = true;
            var replacement = changed == "stage" ? checkpoint with { Stage = "Index" } : checkpoint with { Cursor = "obsolete" };
            store.SaveCheckpointAsync(revision, replacement, Array.Empty<AnalysisStoreChunk>().ToAsyncEnumerable(), default)
                .GetAwaiter().GetResult();
        });

        var result = await new AnalysisProcessingService(store, revision, files.Directory, generation, new Vectors(),
            AnalysisProcessingMode.Summary).AnalyzeAsync(request, progress, default);

        Assert.True(changedOnce);
        Assert.Equal("Failed", result.Status);
        Assert.Empty(generation.Prompts);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(revision.Identity, default)).Status);
    }

    [Fact]
    public async Task AnalyzeAsync_IntegratedCoordinatorResolver_RespectsRecordingIntentAndCommitsOnlyOwnedRevision()
    {
        using var files = new ProcessingFiles();
        var request = (await files.Source("First report.", "Final decision: postpone the launch.")) with { Question = "What was agreed?" };
        var identity = files.Identity(request);
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "queue.sqlite"));
        var vectors = new Vectors();
        var generation = new CandidateGeneration();
        var coordinator = new AnalysisJobCoordinator(repository, store, execution =>
        {
            Assert.Equal(identity, execution.Revision.Identity);
            Assert.Equal(files.Directory, execution.Request.ModelDirectory);
            Assert.Equal(AnalysisJobMode.GlobalQuestion, execution.Request.Mode);
            return new AnalysisProcessingService(store, execution.Revision, files.Directory, generation, vectors,
                AnalysisProcessingMode.GlobalQuestion, enterInferenceAsync: execution.EnterInferenceAsync);
        });
        await coordinator.RecoverAsync();
        await coordinator.SetRecordingIntentAsync(true);
        var job = await coordinator.EnqueueAsync(new(request, identity, files.Directory, AnalysisJobMode.GlobalQuestion));
        await coordinator.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Queued, Assert.Single(coordinator.Jobs).Status);
        Assert.Equal(0, vectors.Calls);
        Assert.Empty(generation.Prompts);
        await coordinator.SetRecordingIntentAsync(false);

        await coordinator.WaitForIdleAsync();

        var completed = Assert.Single(coordinator.Jobs);
        Assert.Equal(job.Id, completed.Id);
        Assert.Equal(AnalysisJobStatus.Complete, completed.Status);
        Assert.Equal(completed.Revision!.Id, (await store.LoadAsync(identity, default)).Completed!.Id);
        var outputs = await Collect(SqliteTranscriptAnalysisStore.Open(files.Database).ReadOutputsAsync(completed.Revision, default));
        Assert.Contains(outputs.SelectMany(output => output.Claims).SelectMany(claim => claim.Evidence),
            evidence => evidence.Text == "[00:00:02.000] Final decision: postpone the launch.");
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values) result.Add(value);
        return result;
    }

    private sealed class ProcessingFiles : IDisposable
    {
        public string Directory { get; } = Path.GetFullPath(Path.Combine(".test-artifacts", "processing-" + Guid.NewGuid().ToString("N")));
        public string Database => Path.Combine(Directory, "store.sqlite");

        public ProcessingFiles() => System.IO.Directory.CreateDirectory(Directory);

        public async Task<TranscriptAnalysisRequest> Source(params string[] cues)
        {
            var text = Path.Combine(Directory, "source.txt");
            var srt = Path.Combine(Directory, "source.srt");
            await File.WriteAllTextAsync(text, string.Join(Environment.NewLine, cues.Select((cue, index) =>
                $"[00:00:{index * 2:00}.000] {cue}")));
            await File.WriteAllTextAsync(srt, string.Join(Environment.NewLine, cues.Select((cue, index) =>
                $"{index + 1}\n00:00:{index * 2:00},000 --> 00:00:{index * 2 + 1:00},000\n{cue}\n")));
            return new(Guid.NewGuid(), new(text, srt), "English", null);
        }

        public AnalysisStoreIdentity Identity(TranscriptAnalysisRequest request) => new(request.AttemptId,
            Hash(File.ReadAllBytes(request.Outputs.TextPath)), Hash(File.ReadAllBytes(request.Outputs.SubtitlePath)),
            request.Language, Model("generation"), Model("embedding"), "processing-v1", 2);

        private static AnalysisStoreModel Model(string id) => new(id, new('A', 64), Hash(Encoding.UTF8.GetBytes("{}")), "{}");
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class CandidateGeneration : IGenerationBackend
    {
        public Action? OnCall { get; init; }
        public GenerationFinish Finish { get; init; } = GenerationFinish.Completed;
        public string? Limitation { get; init; }
        public int StatementLimit { get; init; } = 512;
        public int TokenDivisor { get; init; } = 4;
        public int TokenOverhead { get; init; }
        public bool NoClaims { get; init; }
        public string? Response { get; init; }
        public Func<int, GenerationFinish>? FinishForCall { get; init; }
        public Func<int, string?>? LimitationForCall { get; init; }
        public List<string> Prompts { get; } = [];
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt) => prompt.Length / TokenDivisor + TokenOverhead;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            OnCall?.Invoke();
            Prompts.Add(prompt);
            if (Response is not null) return Task.FromResult(new GenerationResult(Response, Finish, 50));
            using var json = JsonDocument.Parse(prompt);
            var excerpts = json.RootElement.GetProperty("excerpts").EnumerateArray().ToArray();
            var claims = (NoClaims ? [] : excerpts).Select((excerpt, index) => new
            {
                kind = "Report", statement = excerpt.GetProperty("Quote").GetString()![..Math.Min(StatementLimit, excerpt.GetProperty("Quote").GetString()!.Length)],
                owner = new { state = "Absent", value = (string?)null },
                deadline = new { state = "Absent", value = (string?)null },
                qualifiers = new[] { "unverified" }, references = new[] { index }
            });
            return Task.FromResult(new GenerationResult(JsonSerializer.Serialize(new { claims, limitation = LimitationForCall is null ? Limitation : LimitationForCall(Prompts.Count) }),
                FinishForCall?.Invoke(Prompts.Count) ?? Finish, 50));
        }
    }

    private sealed class Vectors : IEmbeddingBackend
    {
        public Action? OnCall { get; init; }
        public List<string> Inputs { get; } = [];
        public int TokenDivisor { get; init; } = 4;
        public int? ConstantTokenCount { get; init; }
        public Func<string, float[]>? GetVector { get; init; }
        public Func<string, Task>? BeforeReturn { get; init; }
        public int Calls { get; private set; }
        public int CountTokens(string input) => ConstantTokenCount ?? input.Length / TokenDivisor;
        public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
        {
            Calls++;
            Inputs.Add(input);
            OnCall?.Invoke();
            if (BeforeReturn is not null) await BeforeReturn(input);
            return GetVector?.Invoke(input) ?? [1, 0];
        }
    }

    private sealed class NativeBoundary
    {
        public int Entries { get; private set; }
        public int Active { get; private set; }
        public Task<IDisposable> Enter(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Entries++;
            Active++;
            return Task.FromResult<IDisposable>(new Lease(() => Active--));
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class NativeModel(bool embeddings) : INativeModel
    {
        public bool Disposed { get; private set; }
        public IReadOnlyDictionary<string, string> Metadata => new Dictionary<string, string>
            { ["general.architecture"] = embeddings ? "nomic-bert-moe" : "phi3" };
        public ulong SizeInBytes => 1;
        public int EmbeddingSize => 768;
        public int LastGeneratedTokens => 10;
        public int CountTokens(string text, bool addSpecial, bool parseSpecial, Encoding encoding) => text.Length / 4;
        public Task<GenerationResult> InferAsync(string prompt, NativeContextOptions context, NativeGenerationOptions inference,
            CancellationToken token)
        {
            Assert.Equal(4096u, context.ContextSize);
            Assert.Equal(1024, inference.MaxTokens);
            Assert.Equal(0, inference.Temperature);
            Assert.Equal(42u, inference.Seed);
            return new CandidateGeneration().GenerateAsync(prompt[(prompt.IndexOf("<|user|>", StringComparison.Ordinal) + 8)..prompt.LastIndexOf("<|end|>", StringComparison.Ordinal)],
                inference.MaxTokens, token);
        }
        public void InitializeEmbedding(NativeContextOptions context)
        {
            Assert.Equal(512u, context.ContextSize);
            Assert.Equal(LLama.Native.LLamaPoolingType.Mean, context.PoolingType);
        }
        public Task<IReadOnlyList<float[]>> EmbedAsync(string input, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<float[]>>([Enumerable.Repeat(1f, 768).ToArray()]);
        public void Dispose() => Disposed = true;
    }
}
