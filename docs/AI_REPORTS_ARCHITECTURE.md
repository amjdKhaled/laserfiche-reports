# Local document reporting: grounded pipeline v2

The reporting assistant now retrieves both keyword and vector candidates, selects
source excerpts, writes a concise answer and runs a separate semantic review.
It remains a read-only local reporting workflow. It cannot modify Laserfiche,
execute model-generated SQL, run commands or browse the internet at runtime.

## Research translated into implementation

| Primary source | Applied idea | Implementation |
| --- | --- | --- |
| [Ollama structured outputs](https://docs.ollama.com/capabilities/structured-outputs) | Pass a JSON Schema to the inference engine, then validate the returned data. | Pydantic schemas are bound to each extraction, composition and review call. |
| [Supabase hybrid search](https://supabase.com/docs/guides/ai/hybrid-search) | Combine independent lexical and semantic rankings using reciprocal rank fusion. | `HybridRetrieval.Sql` fuses full-text and vector ranks with constant 60; both queries use the same repository, document and project filters. |
| [LangGraph self-reflective RAG](https://www.langchain.com/blog/agentic-rag-with-langgraph) | Review evidence relevance and answer support in explicit graph stages. | Separate extraction, composition, review and rendering nodes; rejected prose is never shown as an answer. |
| [Anthropic contextual retrieval](https://www.anthropic.com/engineering/contextual-retrieval) | Losing context around a chunk can impair retrieval and understanding. | Source names/types accompany excerpts, and query-focused contiguous windows preserve relevant text later in a chunk. This is not Anthropic's indexing-time contextual embedding pipeline. |

The sources motivate design choices; their benchmark results are not measurements
of this project. No new external model or hosted service is required.

## Retrieval and access

- Reuses `public.documents` without altering its schema or existing data.
- Searches `laserfiche-reports` chunks only, constrained to the active repository
  and any explicitly requested document IDs, before ranking.
- Lexical search uses PostgreSQL `simple` tokenization and normalized Arabic search
  keys (hamza, diacritics, tatweel and digit forms). Original text is returned intact.
- RRF combines ranks, not incomparable raw similarity scores. It is not a probability
  or a confidence percentage. This implementation does not use BM25 or a neural reranker.
- Lexical matches can recover relevant names/numbers missed by vectors, including
  indexed chunks without an embedding. If the embedding service fails, keyword
  search continues with an explicit scope notice; cancellation still propagates.
- Every candidate document is checked against the current Laserfiche credential
  before its evidence reaches the model or the client.
- Live inventory/count/field-equality reports continue to use Laserfiche directly.
  Semantic retrieval never establishes repository-wide totals or absence.

Without a schema change, lexical search computes text vectors at query time. This
adds database work for large corpora. The candidate and command-timeout limits bound
the request; production scale requires measured latency before adding an optional
index through a separately reviewed database change. No new index is created here.

## Answer composition and review

1. **Prepare:** select a contiguous window near question terms. Record its start and
   truncation, preserving original characters. Bound the complete serialized context,
   including document headers and JSON escaping.
2. **Extract:** produce at most 16 quotations. Check each quotation against the exact
   visible source excerpt and validate its reference. One bounded repair attempt is
   allowed for invalid JSON or quotation output.
3. **Compose:** generate up to eight concise findings, each linked to quotation rows.
   Reject invalid row IDs, model-authored citation labels and unsupported numeric
   literals before semantic review. The server owns final source citations.
4. **Review:** a separate model call checks every quotation for relevance and faithful
   context, and every finding for support by its stated rows. It can reject text or
   reduce coverage; it cannot upgrade an insufficient extraction into a complete answer.
5. **Render:** show accepted findings, the quotation table, source identities, scope
   and items needing verification. HTML/Markdown exports preserve review status.

Normally this takes three model calls, at most four with extraction repair. A model
failure during composition/review falls back to source quotations with an explicit
review-status notice. A malformed review cannot release generated prose. The first
extraction call failing entirely is still a service error, not an empty successful answer.

The reviewer is another call to the same local model, not an independent ground-truth
authority. A misleading statement may fool both calls. Verbatim quotation checks
establish provenance only; OCR errors, stale indexed fields, incomplete retrieval
and semantic errors still require evaluation on real documents.

`/health` identifies `reports-grounded-v2.1`. Chat responses expose a `quality` object
with report status, quotation verification, semantic-review state and model-call
count. The UI displays understandable status labels rather than invented confidence
percentages. Old saved reports without this object remain readable.

## Verification and local model evaluation

Regression tests use scripted model replies to test rejection, scope, schema and
fallback behavior. PostgreSQL/pgvector CI exercises the actual hybrid SQL against
an isolated `reports_test` database, including cross-repository and project isolation.
It never connects to the user's database. UI tests cover exporting and persistence.

The 46-case synthetic corpus is a separate model-quality check. Run it on the model
used for reports after restarting LangGraph:

```powershell
.\tools\reports-graph\.venv\Scripts\python.exe .\tools\reports-graph\evaluate.py --model qwen2.5:7b --output .\quality-results.json
```

The saved evaluation contains per-case results, elapsed time, model and prompt
version, without raw source texts. Cases containing selected quotations require
completed semantic review to pass. Results on scripted replies do not count as
passing this corpus on Ollama. Record actual results before claiming an improvement
in answer quality, and add anonymized examples of failures from real documents.

Examples include negation, conditional payments, currency/unit mismatches, late
answers inside long chunks, absent comparison evidence, misleading instructions,
incomplete table headings, OCR uncertainty and causes not stated in the source.

## v2.1 output correctness follow-up

- Bind the schema in Ollama's `format` and include it in the trusted system
  prompt, following the official structured-output guidance.
- Give composition the actual source name, page and source type to distinguish
  comparison subjects. These labels do not establish document contents.
- Preserve signs, percentage markers and numeric separators in deterministic
  checks. A negative balance cannot be silently rewritten as positive, and a
  comma-separated list cannot be merged into a larger number. This is a lexical
  guard, not unit conversion or mathematical validation; faithful paraphrases
  may be conservatively rejected. Semantic review still checks units and meaning.
- Downgrade coverage when a finding is rejected and explain that unsupported
  prose was excluded, even when the model returns `sufficient`.
- Extend prompts and quality cases for blank versus zero, effective versus upload
  dates, partial questions, and recommendations versus approved obligations.
- Evaluation rejects an empty `answered` selection and can require an actual
  synthesis using `minimumFindings`, so a quotations-only response cannot silently
  pass cases specifically testing answer composition.

Regression tests use scripted model replies. Run the 46 cases on the installed
Ollama model and review the resulting answers against their synthetic sources
before drawing conclusions about real report quality. No benchmark score is
claimed from the research sources.
