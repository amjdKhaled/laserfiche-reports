# Arabic OCR accuracy: evidence before defaults

Baseline: `f2e0c4a` on main. This change corrects text-loss and unverified-rewrite
behaviour and adds measurement tools. It does **not** claim a measured improvement
on real documents. No reference images, ground-truth transcriptions, Paddle models,
or .NET SDK were available in the editing environment.

## Findings in the current implementation

- Recognition threshold 0.35 could discard uncertain source text before recovery.
  Recognition now retains available text; the threshold flags review instead.
- Identical neighbouring structure blocks were deleted by text equality alone.
  Distinct repeated labels/cells are now retained.
- Missing fallback scores previously became 1.0. Unknown scores now remain unknown;
  nonfinite/out-of-range values do not enter the mean.
- Mean confidence excluded low scores, producing an optimistic statistic. Valid
  low scores now contribute. Mean confidence is still not transcription accuracy.
- Ollama received text without the image. Length and numeric checks could not
  establish that names or words matched the document. Automatic rewriting is off;
  legacy opt-in configurations cannot replace text with an unverified suggestion.
- Numeric checks previously normalized digit scripts and sorted numbers, accepting
  some changes to printed glyphs/order. Checks now preserve both.
- The previous resize warning concerned a detector limit. It does not by itself
  prove that recognition crops lost the same resolution. Changing a CLI upper
  bound alone would not remove Paddle's internal `max_side_limit`.

Low-score retention may also retain noise. This is a deliberate tradeoff requiring
review, not proof of an improved CER. Detection can still miss text entirely; no
recognizer score detects an omitted region reliably. Layout, RTL, mixed-language
recognition and table structure remain subject to real-image evaluation.

## Corrected implementation requirements

1. Keep processing local. Download dependencies/models in preparation, then test
   startup/inference without networking. Do not replace the existing documents DB.
2. Freeze the baseline code, package versions, model identities, images and hashes.
3. Build manually verified references for real Arabic pages, starting with Entry
   618. Include names, dates, IDs, tables, mixed text and difficult scans. Entry 608
   (English) is a separate regression case, not the Arabic quality benchmark.
4. Separate tuning pages and held-out test pages. Never choose each test page's
   winning variant using its reference and advertise that as production accuracy.
5. Measure strict CER/WER, page exact match and numeric sequence preservation.
   Keep diacritics, hamzas, ya/maqsurah, ta marbuta and digit scripts. Report any
   relaxed metric separately. Names and cells need explicitly annotated references;
   document-level CER alone does not measure correct table relationships.
6. Evaluate independent image variants, then source-resolution line crops and
   overlapping region detection if a documented detector miss warrants it. Avoid
   cutting connected Arabic letters. Preserve page-coordinate transforms and RTL
   reading order when joining crop results. Do not reverse Unicode strings.
7. Compare models on the same inputs. Candidate models include the existing Arabic
   PP-OCRv5, Tesseract tessdata_best ara (OEM 1), and a locally runnable Arabic VLM
   such as Baseer/PaddleOCR-VL after checking version, model license and hardware.
   Model age or the word “server” is not evidence of better Arabic accuracy.
8. A character inventory documents coverage; adding it to a prompt does not train
   a recognizer. Do not apply it as a whitelist that deletes mixed English text.
9. Treat super-resolution as an experiment. Generated detail may alter dots;
   ordinary resizing also cannot recover evidence absent from the source.
10. Engine scores are not interchangeable probabilities. Repeated passes from one
    engine have correlated errors; agreement is not independent verification.
    Select a policy on tuning data and validate on held-out data.
11. Image-backed LLM corrections must retain original text, candidates, source crop
    and uncertainty. Text-only fluency is insufficient. Do not silently guess names
    or numbers; unresolved regions require review.
12. Promote a new default only after paired tests show improvement without material
    regressions in critical fields. Stop based on measured marginal benefit, not an
    unbounded promise of perfect OCR. Keep a tested rollback baseline.

## Local paired experiments

Create a private `ocr-evaluation` directory (git-ignored). Export single-page images
and create UTF-8 reference text files, using consistent reading order and, for table
output, the same explicit markup contract. References must come from human review
of the original image, not from the candidate model.

Example `ocr-evaluation/manifest.json`:

```json
[
  {"id":"618-page-1", "image":"618-page-1.png", "reference":"618-page-1.txt"}
]
```

Run the baseline worker from a separate checkout of `f2e0c4a`, without overwriting
local uncommitted changes. Run this benchmark script against it:

```powershell
.\tools\paddleocr-vl\.venv\Scripts\python.exe .\tools\paddleocr-vl\benchmark.py `
  .\ocr-evaluation\manifest.json --variants original `
  --output .\ocr-evaluation\baseline.json
```

Then start the changed worker and run:

```powershell
.\tools\paddleocr-vl\.venv\Scripts\python.exe .\tools\paddleocr-vl\benchmark.py `
  .\ocr-evaluation\manifest.json --variants original grayscale contrast scale2 `
  --output .\ocr-evaluation\candidate.json
```

Use the same reference/image hashes and package/model installation across the two
runs. Compare the `summary.original` CER/WER to isolate the code change. Variant
summaries are experiments, not an automatic ensemble. The tool retains each OCR
response, uncertainty, input/reference hashes and elapsed time, and refuses to
replace an existing report. Reports contain document text: keep them local.
Pillow is required (already a dependency of the worker installation).

The script calls the Python worker directly. It intentionally bypasses Laserfiche's
existing searchable text and ingestion cache; otherwise the test may not exercise
OCR at all. Test complete ingestion separately after selecting a policy.
A scale2 page may still be resized by the detector; it is not a fix for that limit.
Model comparisons can use separate local workers/ports with separate report files.

## Research consulted

- Tesseract image quality, segmentation and scaling:
  https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html
- Tesseract best/fast models and LSTM compatibility:
  https://tesseract-ocr.github.io/tessdoc/Data-Files.html
- Official PaddleOCR pipeline parameters and Arabic model listing:
  https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/pipeline_usage/OCR.en.md
- KITAB-Bench, Arabic-specific document evaluation:
  https://arxiv.org/abs/2502.14949
- Baseer, Arabic document OCR; reported results belong to its evaluation setup,
  not this repository: https://arxiv.org/abs/2509.18174
- Cross-dataset OCR/VLM study: no universally best approach; correction can regress
  with misleading OCR priors: https://arxiv.org/abs/2608.22366

## Verification status

Eight Python unit tests cover retained weak text, repeated blocks, unknown scores,
strict Arabic distinctions, edit metrics, local-only endpoints and image variants.
Python syntax and whitespace checks passed. These are software checks, not an OCR
accuracy benchmark. .NET tests and real Paddle inference remain to be run on the
installed environment; the Arabic image/reference set is still required.
