# Additive Arabic semantic expansion

Base: `722e218` on `fix/typed-semantic-repository-plans` (2026-11-02).
PR #43 was reviewed as a closed, unmerged permissions change and is not reapplied.
The current permission bridge from merged PR #44 remains unchanged.

## Behavior

| Area | Existing behavior preserved | Added behavior |
| --- | --- | --- |
| Business concepts | Owner-maintained `glossary.json`, archived-by-field and decisions-by-template rules | Applicable glossary rules reach the local planner and reviewer; no business definition is replaced |
| Templates | Existing cues, catalog name matching, missing-template logic | تيمبليت، تيكبليت، تمبلييت، قالب الفهرسة، كرت فهرستها، metadata template; Gulf association and negation |
| Dates | Existing normalization, calendar arithmetic, English and formal Arabic periods | هالشهر، الشهر ذا، البارح، هالأسبوع، هالسنة and creation/modification verbs; negated verbs cannot acquire a missing positive period |
| Files and folders | Standalone ملف keeps its document reading; existing folder backend resolves names under current account | Explicit containment such as داخل ملف الوارد supplies a separate folder-grounding view; no invented ID |
| Archive | Generic مؤرشفة retains its old field condition | Explicit current location inside مركز الوثائق والمحفوظات is a folder scope, not a historical archive date |
| Status | Existing live-value and field grounding | Actual list choices are now provided to the model; approval synonyms are candidates, not invented stored values |
| Deletion | Current bin and audit remain separate existing tools | Additional unambiguous deletion phrases reuse the bin; unsupported deletion dates/constraints ask for clarification instead of becoming unfiltered inventory |
| Conversation | Existing history, option selection and semantic review | Original question survives spelling suggestions; additional whole-utterance, negation, mixed-language and follow-up guidance |
| Deployment | Existing local model and startup architecture | Windows source install, transfer package, Docker and frozen MSI planner include the old glossary, previously missing from these paths |

`semantic_expansion.py` produces advisory language evidence. It does not execute a query,
choose arbitrary stored values, access privileged credentials, or replace the semantic
planner. The planner still uses the complete live catalog, the existing validator and
repository executor. No additional LLM calls or cloud services are introduced.
All old glossary entries and tests are retained. Optional `REPORTS_GLOSSARY` overrides
retain their existing behavior. Frozen builds bundle the default next to Python modules.

## Validation

The isolated, unmodified baseline ran **267 tests, OK, one skipped** using the same
installed Python dependencies as the updated code. The skip requires an external
llama.cpp schema converter (`LLAMA_SCHEMA_CONVERTER`).

Run regression and new contract tests:

```powershell
python -m compileall -q tools/reports-graph
python -m unittest discover -s tools/reports-graph -p 'test_*.py'
```

New tests check calendar boundaries, non-destructive language evidence for the twenty
requested colloquial examples, template grounding, actual list choices, original text,
negation, archive distinctions, bin constraints and deployment resource inclusion.
Fake-model tests validate the pipeline contract; they are **not real model accuracy tests**.
The original small-payload regression is retained and passes without relaxing its limit.

Twenty additional authored acceptance cases can be run with the existing real-model
runner and a synthetic catalog. This exercises the local LLM, not repository data:

```powershell
python tools/reports-graph/evaluate_real_questions.py --cases tools/reports-graph/semantic_acceptance.json --catalog tools/reports-graph/semantic_catalog.example.json --model qwen2.5:14b --timeout 180 --output semantic-results.json
```

Use the model actually configured on the target machine; a model upgrade is not required
by these changes. The reference date in the acceptance corpus is fixed for reproducibility;
production still receives the real Asia/Riyadh date from the backend. The evaluator now
accepts optional per-case conversation history while preserving old case behavior.

## Limits and target-device checks

- No live Laserfiche repository, Ollama model, .NET SDK, PowerShell or Windows MSI build
  environment was available for end-to-end verification here. Real model accuracy,
  repository result accuracy, latency, GPU memory and installer execution remain unverified.
- The new prompt adds context tokens; no inference speed or accuracy improvement is
  asserted without the target-device benchmark. No model calls were added.
- Recycle-bin date filtering/sorting and arbitrary metadata predicates are not exposed by
  the current tool contract. These requests clarify; creation/modified dates cannot stand
  in for deletion time. Permanently purged content cannot be retrieved by this feature.
- Template-to-field definitions, historical workflow/scan/archive events, empty-folder
  detection and arbitrary Laserfiche knowledge answers still require authoritative tools
  or local reference content where the existing contract lacks them. Language hints do not
  implement these missing APIs. Ambiguous «مسح» or «أرشفة» still needs context.
- Contextual folder names use the existing current-account backend lookup. A name in the
  request does not prove existence, uniqueness or permission. No hidden IDs are enumerated.
- This is an additive implementation of the language and grounding improvements, not a
  claim that every acceptance criterion in the much broader requested specification has
  been proven on the real system.

Before publishing a release, build with the existing Windows `build/publish.ps1`, install
on a test device, and run the new real-model corpus plus live tests with authorized and
restricted users. Confirm the same query under both source and installed planners, offline.
The committed source/package changes do not rebuild or replace old checked-in installers.
