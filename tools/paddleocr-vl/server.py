#!/usr/bin/env python3
"""Loopback-only, non-generative PaddleOCR HTTP worker for Laserfiche Reports."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import os
import tempfile
import threading
import time
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

from paddleocr import PPStructureV3


MAX_IMAGE_BYTES = 200 * 1024 * 1024
MAX_JSON_REQUEST_BYTES = 70 * 1024 * 1024
ENGINE_NAME = "PP-StructureV3"


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
            score = float(scores[index]) if index < len(scores) else 1.0
        except (TypeError, ValueError):
            score = 0.0
        if score < minimum_score:
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
            if score >= minimum_score:
                scores.append(score)

    if contents:
        return "\n\n".join(contents).strip(), scores

    # Defensive fallback for unexpected 3.x result shapes.
    lines, fallback_scores = extract_lines(overall, minimum_score)
    return "\n".join(lines).strip(), fallback_scores


@dataclass(frozen=True)
class OcrResult:
    text: str
    line_count: int
    mean_confidence: float | None
    image_sha256: str
    elapsed_ms: int


class OcrRuntime:
    def __init__(
        self,
        ocr_version: str,
        language: str,
        recognition_model: str,
        device: str,
        minimum_score: float,
    ) -> None:
        self.ocr_version = ocr_version
        self.language = language
        self.recognition_model = recognition_model
        self.device = device
        self.minimum_score = minimum_score
        self._lock = threading.Lock()
        print(
            f"Loading {ENGINE_NAME} {ocr_version} ({recognition_model}, lang={language}) "
            f"on {device}. The first run may download model files.",
            flush=True,
        )
        # PaddleOCR-VL is intentionally not used here: as a generative model it
        # can produce fluent text that is absent from the input image.
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
        )
        print(f"{ENGINE_NAME} Arabic worker is ready.", flush=True)

    def recognize(self, image_bytes: bytes) -> OcrResult:
        digest = hashlib.sha256(image_bytes).hexdigest()
        started = time.perf_counter()
        temp_path = ""
        try:
            with tempfile.NamedTemporaryFile(
                suffix=image_suffix(image_bytes), delete=False
            ) as temp_file:
                temp_file.write(image_bytes)
                temp_path = temp_file.name

            print(
                f"OCR request image_sha256={digest} bytes={len(image_bytes)}",
                flush=True,
            )
            with self._lock:
                results = self._pipeline.predict(
                    temp_path,
                    use_doc_orientation_classify=True,
                    use_doc_unwarping=False,
                    use_textline_orientation=True,
                    text_rec_score_thresh=self.minimum_score,
                )
                texts: list[str] = []
                scores: list[float] = []
                for result in results:
                    result_text, result_scores = extract_structure_text(
                        result, self.minimum_score
                    )
                    if result_text:
                        texts.append(result_text)
                    scores.extend(result_scores)

            elapsed_ms = round((time.perf_counter() - started) * 1000)
            mean_confidence = sum(scores) / len(scores) if scores else None
            text = "\n\n".join(texts).strip()
            line_count = sum(1 for line in text.splitlines() if line.strip())
            print(
                f"OCR completed image_sha256={digest} lines={line_count} "
                f"mean_confidence={mean_confidence} elapsed_ms={elapsed_ms}",
                flush=True,
            )
            return OcrResult(
                text=text,
                line_count=line_count,
                mean_confidence=mean_confidence,
                image_sha256=digest,
                elapsed_ms=elapsed_ms,
            )
        finally:
            if temp_path:
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
                },
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
        self.end_headers()
        self.wfile.write(body)


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
    parser.add_argument("--minimum-score", type=float, default=0.35)
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "localhost", "::1"}:
        parser.error("--host must be a loopback address")
    if not 0 <= args.minimum_score <= 1:
        parser.error("--minimum-score must be between 0 and 1")

    OcrHandler.runtime = OcrRuntime(
        args.ocr_version,
        args.language,
        args.recognition_model,
        args.device,
        args.minimum_score,
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
