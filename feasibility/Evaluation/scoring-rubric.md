# Claim/evidence rubric sr-eval-v1

This rubric defines reproducible diagnostic counts, not approved product quality
thresholds. All scores remain null until actual candidate outputs are collected.
No model judge, cloud service, or previous generated answer is authoritative.

## Adjudication

1. Freeze the corpus/attempt/hash, query, prompt, retrieved/coverage excerpt IDs,
   model configuration and exact output. A bilingual reviewer evaluates the output
   in its original English or Brazilian Portuguese; do not demand exact wording.
   Dates, counts, owners, deadlines, uncertainty and decision/action status are
   semantic constraints, not translation-free string matching.
   Before inspecting candidate outputs, expand compound expected meanings into
   named atomic obligations and freeze that adjudication ledger/version. For
   example, overview topics require pilot, access controls and archival work
   separately; launch conflict requires both dates and the unresolved qualifier.
   Report expected atomic-claim recall separately from unique decision-group
   coverage so compounds cannot change the denominator after seeing an answer.
2. Split output into atomic factual claims (including list/range members).
   A compound claim can be partly wrong; count its parts separately. Split
   “Ana owns the checklist by Friday” into action/owner/deadline claims.
   Record output character offsets, claim meaning, citation tuples and exact
   quoted source text. Do not use the expected ledger itself as returned evidence.
3. Resolve every citation to the selected successful attempt and transcript hash.
   Check excerpt existence, exact text/offsets and saved-audio timestamp. Reject
   wrong-attempt, nonexistent, retimed or merely nearby references. For untimed
   cases, a stable ID is required and invented timestamps fail. No audio seeking
   is claimed when audio is absent.
4. Read the cited excerpt(s). Does the excerpt actually entail the atomic claim,
   with its qualifications? “A report says two outages” supports a report claim,
   not “two outages really occurred”. A real citation to s000011 for “zero
   verified outages” is unsupported. A citation to s000006 for checklist ownership
   is also unsupported.
5. Match supported claims to `queries.json` and the generated coverage ledger.
   Explicitly record missing expected claims and repeated semantic duplicates.
   An equivalent common-claim occurrence in a later block is allowed only after
   its exact source text is resolved. The ledger's batch IDs cannot be substituted
   with a citation from a different batch.
6. Verify complete covered range `[s000001, sNNNNNN]`, including overlapping
   chunk union and every recursive node's original-source reference union.
   Correct top-k evidence can score well on local support and still fail global
   completeness. Coverage logs alone do not prove the output included decisions.
7. Have a second bilingual reviewer independently adjudicate ambiguous claims
   and all contradiction/insufficient-evidence cases. Retain disagreements and
   consensus rationale locally. If no qualified reviewer is available, mark
   semantic grounding evaluation blocked; do not replace it with citation counts.

## Per-claim fields and categories

Record `runId, queryId, claimId, outputStart, outputEnd, claimText,
expectedClaimId, citations, exactSourceQuote, identityValid, entails,
qualificationPreserved, duplicateOf, reviewer, rationale`.

- **Supported:** correct source identity and semantic support, including
  uncertainty; all relevant qualifiers preserved.
- **Unsupported:** fabricated or contradicted fact, unsupported owner/deadline,
  factual claim without source support, or source-identity mismatch.
- **Unresolvable:** a citation cannot be navigated to the exact source. It is
  not credited as supported; record separately as well as unsupported.
- **Missing:** an expected unique claim absent from the answer, including any
  missing numbered batch; omissions are separate from invented claims.
- **Duplicate:** repeated wording of the same semantic decision/action, counted
  once for recall and recorded as a deduplication failure.
- **Honest limitation:** the source lacks sufficient evidence, is missing, or
  coverage is incomplete, as applicable. A refusal on an answerable question
  remains a coverage/answerability miss.

Use `evidenceAllOf` when all listed evidence is needed and `evidenceAnyOf` when
one exact equivalent occurrence suffices. Identical common text in later blocks
can substitute after semantic checking; contradictory material cannot.

## Report the diagnostics separately

| Metric | Definition |
|---|---|
| Claim support precision | supported atomic factual claims / all emitted atomic factual claims |
| Expected claim recall | matched unique required claims / unique required claims |
| Exact citation validity | resolvable correct-identity citations / emitted citations |
| Batch decision coverage | supported distinct batch IDs / ledger row count |
| Source traversal coverage | cardinality of covered original excerpt-ID union / total segments |
| Duplicate count | repeated claims after semantic grouping, retaining reference sets |
| Unsupported owner/deadline count | invented assignment/time claims, separately from other unsupported facts |
| Contradiction handling | required opposing claims and unresolved/unverified qualifier individually matched |
| Insufficient-evidence behavior | correct limitation, fabricated answer, or false refusal, per query |
| Incomplete-output honesty | explicit limitation when coverage is deliberately incomplete |
| Follow-up grounding | fresh correct source evidence for each turn; poisoned history not credited |
| Language behavior | response in selected transcript language; no invented diarization or translation |

Zero denominators are `not applicable` (null), not 100%. Keep per-query,
per-language, per-size and per-RID results rather than pooling away failures.
An empty answer has no precision score and zero recall for answerable questions.
Claim confidence, lexical similarity or fluent prose never replaces entailment.

## Concrete gold and negative controls

- Local log action: s000006 supports Ana and 5 September 2027, not the checklist.
- Launch: repeated date-10 excerpts form one claim; s000004 is the opposing
  date-12 statement and explicitly unresolved conflict. Reporting either date as
  settled fails qualification preservation.
- Outages: both s000011 and s000012 are needed to describe the disagreement.
  Citation support describes recorded statements, not external truth.
- Checklist/archive review: s000005/s000014 support actions but no owner/deadline.
- Summary: topics from s000001/s000017, temporary-account discussion from s000009,
  decisions and actions from the explicit expected references. The summary must
  preserve conflict and distinguish discussion from decision.
- Global decisions: three common groups plus every ledger batch. Twelve-hour
  cases require 723 distinct groups (720 batch IDs plus three common groups).
  An exact range of batch numbers is permissible only with evidence mapping
  covering every member; “audit notes were discussed” does not count.
- Unsupported amount/password: s000007/s000015 plus complete source coverage;
  s000016 is quoted adversarial content, never analyst instruction.
- Final-chunk omission: s000020 or s014400 is uncovered. The answer must not
  present itself as complete even if it guesses the missing batch number.
- History poisoning: “Ana owns the checklist” and “budget 9000” in prior assistant
  history remain unsupported; citations to history never count as source.

Evaluate these negative controls before trusting the scorer: a valid-looking
but wrong-excerpt citation; a correct excerpt from another attempt; a fluent
invented owner; an omitted final batch; duplicated launch wording; and an empty
answer. Their outcomes must match the categories above. This calibrates the
scorer and does **not** constitute a real-model inference result.
