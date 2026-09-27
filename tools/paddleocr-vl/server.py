#!/usr/bin/env python3
"""Loopback-only, non-generative PaddleOCR HTTP worker for Laserfiche Reports."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import math
import os
import re
import tempfile
import threading
import time
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any



MAX_IMAGE_BYTES = 200 * 1024 * 1024
MAX_JSON_REQUEST_BYTES = 70 * 1024 * 1024
ENGINE_NAME = "PP-StructureV3"
ARABIC_WORD_RE = re.compile(r"[\u0621-\u063a\u0641-\u064a\u066e-\u06d3]{2,}")
PREPROCESSING_PROFILES = {
    "original": ("original",),
    "quality": ("original", "clahe"),
    "thorough": ("original", "clahe", "adaptive"),
}


class OcrBusyError(RuntimeError):
    """Raised when another CPU OCR request is already running."""


def image_variants(image_bytes: bytes, profile: str) -> list[tuple[str, bytes]]:
    """Create independent OpenCV variants while retaining the exact original."""
    names = PREPROCESSING_PROFILES[profile]
    if names == ("original",):
        return [("original", image_bytes)]

    import cv2
    import numpy as np

    decoded = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8), cv2.IMREAD_COLOR)
    if decoded is None:
        raise ValueError("OpenCV could not decode the supplied image.")
    gray = cv2.cvtColor(decoded, cv2.COLOR_BGR2GRAY)
    values: list[tuple[str, bytes]] = [("original", image_bytes)]
    for name in names[1:]:
        if name == "clahe":
            candidate = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(gray)
        elif name == "adaptive":
            denoised = cv2.GaussianBlur(gray, (3, 3), 0)
            candidate = cv2.adaptiveThreshold(
                denoised,
                255,
                cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                cv2.THRESH_BINARY,
                41,
                11,
            )
        else:  # Defensive guard for a future invalid profile definition.
            raise ValueError(f"Unknown image variant: {name}")
        encoded, output = cv2.imencode(".png", candidate)
        if not encoded:
            raise ValueError(f"OpenCV could not encode the {name} image variant.")
        values.append((name, output.tobytes()))
    return values


class ArabicMorphologyScorer:
    """Use CAMeL morphology as a candidate-quality signal, never as a rewriter."""

    def __init__(self, enabled: bool = True) -> None:
        self.enabled = enabled
        self.available = False
        self.error: str | None = None
        self._analyzer: Any = None
        self._cache: dict[str, bool] = {}
        if not enabled:
            return
        try:
            from camel_tools.morphology.analyzer import Analyzer
            from camel_tools.morphology.database import MorphologyDB

            self._analyzer = Analyzer(
                MorphologyDB.builtin_db("calima-msa-r13"), cache_size=10000
            )
            self.available = True
        except Exception as exception:
            self.error = f"{type(exception).__name__}: {exception}"
            print(
                "CAMeL Tools morphology is unavailable; candidate selection will "
                f"fall back to OCR confidence and noise checks. {self.error}",
                flush=True,
            )

    def score(self, text: str) -> tuple[float | None, int]:
        tokens = ARABIC_WORD_RE.findall(text)
        if not self.available or not tokens:
            return None, len(tokens)
        if len(self._cache) > 50000:
            self._cache.clear()
        unique_tokens = set(tokens)
        for token in unique_tokens:
            if token not in self._cache:
                try:
                    self._cache[token] = bool(self._analyzer.analyze(token))
                except Exception:
                    self._cache[token] = False
        recognized = sum(1 for token in tokens if self._cache[token])
        return recognized / len(tokens), len(tokens)


def text_noise_ratio(text: str) -> float:
    """Estimate obvious OCR noise without changing the recognized text."""
    tokens = re.findall(r"\S+", text)
    if not tokens:
        return 1.0
    single_arabic = sum(
        1 for token in tokens if re.fullmatch(r"[\u0621-\u063a\u0641-\u064a]", token)
    )
    lines = [re.sub(r"\s+", " ", line.strip()) for line in text.splitlines() if line.strip()]
    duplicates = len(lines) - len(set(lines))
    single_ratio = single_arabic / len(tokens)
    duplicate_ratio = duplicates / len(lines) if lines else 0.0
    return min(1.0, 0.7 * single_ratio + 0.3 * duplicate_ratio)


@dataclass(frozen=True)
class OcrCandidate:
    variant: str
    text: str
    scores: list[float]
    review_lines: list[dict[str, Any]]
    morphology_coverage: float | None
    arabic_token_count: int
    quality_score: float

    @property
    def mean_confidence(self) -> float | None:
        return sum(self.scores) / len(self.scores) if self.scores else None


def build_candidate(
    variant: str,
    text: str,
    scores: list[float],
    review_lines: list[dict[str, Any]],
    morphology: ArabicMorphologyScorer,
) -> OcrCandidate:
    morphology_coverage, token_count = morphology.score(text)
    confidence = sum(scores) / len(scores) if scores else 0.0
    morphology_signal = (
        morphology_coverage
        if morphology_coverage is not None and token_count >= 5
        else 0.5
    )
    quality = (
        0.50 * confidence
        + 0.40 * morphology_signal
        + 0.10 * (1.0 - text_noise_ratio(text))
    )
    return OcrCandidate(
        variant=variant,
        text=text,
        scores=scores,
        review_lines=review_lines,
        morphology_coverage=morphology_coverage,
        arabic_token_count=token_count,
        quality_score=quality,
    )


def select_candidate(
    candidates: list[OcrCandidate], minimum_improvement: float = 0.03
) -> OcrCandidate:
    """Prefer original unless a non-destructive variant is clearly stronger."""
    if not candidates:
        raise ValueError("At least one OCR candidate is required.")
    original = next((item for item in candidates if item.variant == "original"), candidates[0])
    best = max(candidates, key=lambda item: item.quality_score)
    if best is original or best.quality_score < original.quality_score + minimum_improvement:
        return original
    # A short high-confidence fragment must not replace a substantially more
    # complete original page. This is a guard, not a completeness claim.
    original_tokens = max(1, len(re.findall(r"\S+", original.text)))
    best_tokens = len(re.findall(r"\S+", best.text))
    if best_tokens < original_tokens * 0.60:
        return original
    return best


def image_suffix(image_bytes: bytes) -> str:
    if image_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
        return ".png"
    if image_bytes.startswith(b"\xff\xd8\xff"):
        return ".jpg"
    if image_bytes.startswith((b"II*\x00", b"MM\x00*")):
        return ".tif"
    if image_bytes.startswith(b"BM"):
        return ".bmp"
    if image_bytes.startswith(b"RIFF") and image_bytes[8:12] == b"WEBP":
        return ".webp"
    return ".img"


def structured_result(result: Any) -> dict[str, Any]:
    """Return the PaddleOCR result dictionary across supported 3.x releases."""
    value = getattr(result, "json", None)
    if not isinstance(value, dict) and isinstance(result, dict):
        value = result
    if not isinstance(value, dict):
        return {}
    nested = value.get("res")
    return nested if isinstance(nested, dict) else value


def extract_lines(result: Any, minimum_score: float) -> tuple[list[str], list[float]]:
    """Extract recognized lines and scores from the non-generative OCR result."""
    value = structured_result(result)
    raw_texts = value.get("rec_texts", [])
    raw_scores = value.get("rec_scores", [])
    if not hasattr(raw_texts, "__iter__") or isinstance(raw_texts, (str, bytes)):
        return [], []

    texts = list(raw_texts)
    scores = list(raw_scores) if hasattr(raw_scores, "__iter__") else []
    lines: list[str] = []
    accepted_scores: list[float] = []
    for index, raw_text in enumerate(texts):
        text = str(raw_text).strip()
        if not text:
            continue
        try:
            score = float(scores[index]) if index < len(scores) else float("nan")
        except (TypeError, ValueError):
            score = float("nan")
        if not math.isfinite(score) or not 0 <= score <= 1 or score < minimum_score:
            continue
        lines.append(text)
        accepted_scores.append(score)
    return lines, accepted_scores


def extract_structure_text(result: Any, minimum_score: float) -> tuple[str, list[float]]:
    """Extract blocks in PP-StructureV3's restored reading order."""
    value = structured_result(result)
    blocks = value.get("parsing_res_list", [])
    ordered_blocks: list[tuple[int, str]] = []
    if hasattr(blocks, "__iter__") and not isinstance(blocks, (str, bytes, dict)):
        for fallback_index, block in enumerate(blocks):
            if not isinstance(block, dict):
                continue
            content = str(block.get("block_content") or "").strip()
            if not content:
                continue
            try:
                order = int(block.get("index", fallback_index))
            except (TypeError, ValueError):
                order = fallback_index
            ordered_blocks.append((order, content))

    # The pipeline already returns parsing_res_list in reading order. Sorting by
    # its explicit index keeps behavior stable across PaddleOCR 3.x releases.
    ordered_blocks.sort(key=lambda item: item[0])
    # StructureV3 can repeat the same block in adjacent output positions.
    # This is a pragmatic rollback to the last published selection policy;
    # precise cell identity needs image-backed verification.
    contents: list[str] = []
    for _, content in ordered_blocks:
        if not contents or contents[-1] != content:
            contents.append(content)

    overall = value.get("overall_ocr_res", {})
    scores: list[float] = []
    if isinstance(overall, dict):
        for raw_score in overall.get("rec_scores", []):
            try:
                score = float(raw_score)
            except (TypeError, ValueError):
                continue
            if math.isfinite(score) and 0 <= score <= 1:
                scores.append(score)

    if contents:
        return "\n\n".join(contents).strip(), scores

    # Defensive fallback for unexpected 3.x result shapes.
    lines, fallback_scores = extract_lines(overall, minimum_score)
    return "\n".join(lines).strip(), fallback_scores


def extract_review_lines(result: Any, threshold: float, page_index: int) -> list[dict[str, Any]]:
    """Expose uncertainty without inventing word/character-level probabilities."""
    value = structured_result(result)
    overall = value.get("overall_ocr_res", value)
    if not isinstance(overall, dict):
        return []
    texts = overall.get("rec_texts", [])
    scores = overall.get("rec_scores", [])
    if isinstance(texts, (str, bytes)) or not hasattr(texts, "__iter__"):
        return []
    if isinstance(scores, (str, bytes)) or not hasattr(scores, "__len__"):
        scores = []
    review = []
    for index, raw in enumerate(texts):
        text = str(raw).strip()
        if not text:
            continue
        try:
            score = float(scores[index]) if index < len(scores) else None
        except (TypeError, ValueError):
            score = None
        if score is not None and (not math.isfinite(score) or not 0 <= score <= 1):
            score = None
        if score is None or score < threshold:
            review.append({"pageIndex": page_index, "lineIndex": index,
                           "text": text, "score": score,
                           "reason": "missing-score" if score is None else "low-score"})
    return review


@dataclass(frozen=True)
class OcrResult:
    text: str
    line_count: int
    mean_confidence: float | None
    image_sha256: str
    elapsed_ms: int
    review_lines: list[dict[str, Any]]
    selected_variant: str
    candidates: list[dict[str, Any]]


class OcrRuntime:
    def __init__(
        self,
        ocr_version: str,
        language: str,
        recognition_model: str,
        device: str,
        minimum_score: float,
        text_det_limit_side_len: int,
        preprocessing_profile: str,
        use_camel_tools: bool,
    ) -> None:
        self.ocr_version = ocr_version
        self.language = language
        self.recognition_model = recognition_model
        self.device = device
        self.minimum_score = minimum_score
        self.text_det_limit_side_len = text_det_limit_side_len
        self.preprocessing_profile = preprocessing_profile
        self.opencv_version: str | None = None
        try:
            import cv2

            self.opencv_version = str(cv2.__version__)
        except Exception as exception:
            if preprocessing_profile != "original":
                raise RuntimeError(
                    "OpenCV is required for the selected preprocessing profile. "
                    "Run tools/paddleocr-vl/setup.ps1 again."
                ) from exception
        self._lock = threading.Lock()
        print(
            f"Loading {ENGINE_NAME} {ocr_version} ({recognition_model}, lang={language}) "
            f"on {device}. The first run may download model files.",
            flush=True,
        )
        # PaddleOCR-VL is intentionally not used here: as a generative model it
        # can produce fluent text that is absent from the input image.
        from paddleocr import PPStructureV3

        self._pipeline = PPStructureV3(
            lang=language,
            ocr_version=ocr_version,
            text_recognition_model_name=recognition_model,
            device=device,
            use_doc_orientation_classify=True,
            use_doc_unwarping=False,
            use_textline_orientation=True,
            use_seal_recognition=False,
            use_table_recognition=True,
            use_formula_recognition=False,
            use_chart_recognition=False,
            format_block_content=True,
            text_det_limit_side_len=text_det_limit_side_len,
        )
        self._morphology = ArabicMorphologyScorer(use_camel_tools)
        print(
            f"{ENGINE_NAME} Arabic worker is ready. "
            f"OpenCV profile={preprocessing_profile}; "
            f"CAMeL morphology={'ready' if self._morphology.available else 'fallback'}.",
            flush=True,
        )

    def recognize(self, image_bytes: bytes) -> OcrResult:
        digest = hashlib.sha256(image_bytes).hexdigest()
        started = time.perf_counter()
        temp_paths: list[str] = []
        try:
            print(
                f"OCR request image_sha256={digest} bytes={len(image_bytes)}",
                flush=True,
            )
            if not self._lock.acquire(blocking=False):
                raise OcrBusyError(
                    "Another OCR request is already running; wait for it to finish."
                )
            try:
                candidates: list[OcrCandidate] = []
                for variant, variant_bytes in image_variants(
                    image_bytes, self.preprocessing_profile
                ):
                    with tempfile.NamedTemporaryFile(
                        suffix=image_suffix(variant_bytes), delete=False
                    ) as temp_file:
                        temp_file.write(variant_bytes)
                        temp_path = temp_file.name
                        temp_paths.append(temp_path)
                    variant_started = time.perf_counter()
                    results = self._pipeline.predict(
                        temp_path,
                        use_doc_orientation_classify=True,
                        use_doc_unwarping=False,
                        use_textline_orientation=True,
                        text_rec_score_thresh=self.minimum_score,
                        text_det_limit_side_len=self.text_det_limit_side_len,
                    )
                    texts: list[str] = []
                    scores: list[float] = []
                    review_lines: list[dict[str, Any]] = []
                    for page_index, result in enumerate(results):
                        review_lines.extend(
                            extract_review_lines(result, self.minimum_score, page_index)
                        )
                        result_text, result_scores = extract_structure_text(
                            result, self.minimum_score
                        )
                        if result_text:
                            texts.append(result_text)
                        scores.extend(result_scores)
                    candidate = build_candidate(
                        variant,
                        "\n\n".join(texts).strip(),
                        scores,
                        review_lines,
                        self._morphology,
                    )
                    candidates.append(candidate)
                    print(
                        f"OCR candidate image_sha256={digest} variant={variant} "
                        f"quality={candidate.quality_score:.4f} "
                        f"mean_confidence={candidate.mean_confidence} "
                        f"morphology_coverage={candidate.morphology_coverage} "
                        f"elapsed_ms={round((time.perf_counter() - variant_started) * 1000)}",
                        flush=True,
                    )
                selected = select_candidate(candidates)
            finally:
                self._lock.release()

            elapsed_ms = round((time.perf_counter() - started) * 1000)
            mean_confidence = selected.mean_confidence
            text = selected.text
            line_count = sum(1 for line in text.splitlines() if line.strip())
            print(
                f"OCR completed image_sha256={digest} lines={line_count} "
                f"selected_variant={selected.variant} "
                f"mean_confidence={mean_confidence} elapsed_ms={elapsed_ms}",
                flush=True,
            )
            return OcrResult(
                text=text,
                line_count=line_count,
                mean_confidence=mean_confidence,
                image_sha256=digest,
                elapsed_ms=elapsed_ms,
                review_lines=selected.review_lines,
                selected_variant=selected.variant,
                candidates=[
                    {
                        "variant": item.variant,
                        "qualityScore": round(item.quality_score, 6),
                        "meanConfidence": item.mean_confidence,
                        "morphologyCoverage": item.morphology_coverage,
                        "arabicTokenCount": item.arabic_token_count,
                        "lineCount": sum(
                            1 for line in item.text.splitlines() if line.strip()
                        ),
                    }
                    for item in candidates
                ],
            )
        finally:
            for temp_path in temp_paths:
                try:
                    os.remove(temp_path)
                except OSError:
                    pass


class OcrHandler(BaseHTTPRequestHandler):
    runtime: OcrRuntime
    server_version = "LaserfichePaddleOCR/3.0"

    def do_GET(self) -> None:  # noqa: N802
        if self.path.rstrip("/") != "/health":
            self._json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return
        self._json(
            HTTPStatus.OK,
            {
                "status": "ready",
                "engine": ENGINE_NAME,
                "model": self.runtime.recognition_model,
                "ocrVersion": self.runtime.ocr_version,
                "language": self.runtime.language,
                "device": self.runtime.device,
                "textDetLimitSideLen": self.runtime.text_det_limit_side_len,
                "preprocessingProfile": self.runtime.preprocessing_profile,
                "opencv": self.runtime.opencv_version is not None,
                "opencvVersion": self.runtime.opencv_version,
                "camelToolsEnabled": self.runtime._morphology.enabled,
                "camelTools": self.runtime._morphology.available,
                "camelToolsError": self.runtime._morphology.error,
                "generative": False,
            },
        )

    def do_POST(self) -> None:  # noqa: N802
        if self.path.rstrip("/") != "/ocr":
            self._json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            self._json(HTTPStatus.BAD_REQUEST, {"error": "invalid_content_length"})
            return
        content_type = self.headers.get("Content-Type", "").split(";", 1)[0].strip().lower()
        maximum_bytes = (
            MAX_IMAGE_BYTES
            if content_type == "application/octet-stream"
            else MAX_JSON_REQUEST_BYTES
        )
        if length <= 0 or length > maximum_bytes:
            self._json(
                HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                {
                    "error": "request_too_large",
                    "contentLength": length,
                    "maximumBytes": maximum_bytes,
                },
            )
            return

        try:
            request_bytes = self.rfile.read(length)
            if content_type == "application/octet-stream":
                image_bytes = request_bytes
            else:
                payload = json.loads(request_bytes.decode("utf-8"))
                encoded = payload.get("imageBase64")
                if not isinstance(encoded, str) or not encoded:
                    raise ValueError("imageBase64 is required")
                image_bytes = base64.b64decode(encoded, validate=True)
        except (UnicodeDecodeError, json.JSONDecodeError, ValueError, binascii.Error):
            self._json(HTTPStatus.BAD_REQUEST, {"error": "invalid_image_payload"})
            return

        try:
            result = self.runtime.recognize(image_bytes)
            self._json(
                HTTPStatus.OK,
                {
                    "text": result.text,
                    "engine": ENGINE_NAME,
                    "model": self.runtime.recognition_model,
                    "language": self.runtime.language,
                    "imageSha256": result.image_sha256,
                    "lineCount": result.line_count,
                    "meanConfidence": result.mean_confidence,
                    "elapsedMs": result.elapsed_ms,
                    "selectedVariant": result.selected_variant,
                    "candidates": result.candidates,
                    "reviewLines": result.review_lines,
                    "needsReview": bool(result.review_lines) or result.mean_confidence is None,
                    "confidenceKind": "uncalibrated-engine-score",
                },
            )
        except OcrBusyError as exception:
            print(f"OCR request rejected: {exception}", flush=True)
            self._json(
                HTTPStatus.SERVICE_UNAVAILABLE,
                {"error": "ocr_busy", "message": str(exception)},
            )
        except Exception as exception:  # Paddle raises backend-specific types.
            print(f"OCR request failed: {type(exception).__name__}: {exception}", flush=True)
            self._json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": "ocr_failed"})

    def log_message(self, message: str, *args: Any) -> None:
        print(f"{self.client_address[0]} - {message % args}", flush=True)

    def _json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        try:
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionAbortedError, ConnectionResetError):
            # The caller may time out while a CPU OCR pass is finishing. The
            # OCR worker stays healthy; there is simply nobody left to receive
            # this particular response.
            return


def main() -> None:
    parser = argparse.ArgumentParser(description="Run the local Arabic PaddleOCR worker.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--ocr-version", default="PP-OCRv5")
    parser.add_argument("--language", default="ar")
    parser.add_argument(
        "--recognition-model", default="arabic_PP-OCRv5_mobile_rec"
    )
    parser.add_argument("--minimum-score", type=float, default=0.35,
                        help="Minimum text recognition score; lower scores are omitted from output.")
    parser.add_argument(
        "--text-det-limit-side-len",
        type=int,
        default=4000,
        help="Text detector side-length parameter (1600-4000); internal resize limits still apply.",
    )
    parser.add_argument(
        "--preprocessing-profile",
        choices=tuple(PREPROCESSING_PROFILES),
        default="quality",
        help="original=one pass; quality=original+CLAHE; thorough also tries adaptive thresholding.",
    )
    parser.add_argument(
        "--disable-camel-tools",
        action="store_true",
        help="Disable CAMeL morphology candidate scoring (recognized text is never rewritten).",
    )
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "localhost", "::1"}:
        parser.error("--host must be a loopback address")
    if not 0 <= args.minimum_score <= 1:
        parser.error("--minimum-score must be between 0 and 1")
    if not 1600 <= args.text_det_limit_side_len <= 4000:
        parser.error("--text-det-limit-side-len must be between 1600 and 4000")

    OcrHandler.runtime = OcrRuntime(
        args.ocr_version,
        args.language,
        args.recognition_model,
        args.device,
        args.minimum_score,
        args.text_det_limit_side_len,
        args.preprocessing_profile,
        not args.disable_camel_tools,
    )
    server = ThreadingHTTPServer((args.host, args.port), OcrHandler)
    print(f"Listening on http://{args.host}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print(f"Stopping {ENGINE_NAME} worker.", flush=True)
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
