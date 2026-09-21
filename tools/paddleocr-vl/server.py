#!/usr/bin/env python3
"""Loopback-only PaddleOCR-VL HTTP worker for Laserfiche Reports."""

from __future__ import annotations

import argparse
import base64
import binascii
import json
import os
import tempfile
import threading
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

from paddleocr import PaddleOCRVL


# The .NET client sends the page as application/octet-stream.  Keeping the
# legacy JSON limit separate avoids the ~33% Base64 expansion that caused
# otherwise valid page images to be rejected with HTTP 413.
MAX_IMAGE_BYTES = 200 * 1024 * 1024
MAX_JSON_REQUEST_BYTES = 70 * 1024 * 1024


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


def extract_markdown(result: Any) -> str:
    """Read markdown text from PaddleOCR result objects across 3.x releases."""
    markdown = getattr(result, "markdown", None)
    if markdown is None and isinstance(result, dict):
        markdown = result.get("markdown")

    if isinstance(markdown, str):
        return markdown.strip()

    texts = markdown.get("markdown_texts", "") if isinstance(markdown, dict) else ""
    if isinstance(texts, str):
        text = texts.strip()
        if text:
            return text
    if isinstance(texts, list):
        text = "\n\n".join(str(value).strip() for value in texts if str(value).strip())
        if text:
            return text

    # PaddleOCR releases expose the structured result through either a `json`
    # attribute or the result dictionary itself. Falling back to block_content
    # prevents a successful OCR request from being reported as empty merely
    # because the installed PaddleOCR version shaped `markdown` differently.
    structured = getattr(result, "json", None)
    if not isinstance(structured, dict) and isinstance(result, dict):
        structured = result
    if isinstance(structured, dict):
        nested = structured.get("res")
        if isinstance(nested, dict):
            structured = nested

        nested_markdown = structured.get("markdown")
        if isinstance(nested_markdown, str) and nested_markdown.strip():
            return nested_markdown.strip()
        if isinstance(nested_markdown, dict):
            nested_texts = nested_markdown.get("markdown_texts")
            if isinstance(nested_texts, str) and nested_texts.strip():
                return nested_texts.strip()

        blocks = structured.get("parsing_res_list")
        if isinstance(blocks, list):
            block_texts = []
            for block in blocks:
                if not isinstance(block, dict):
                    continue
                content = block.get("block_content")
                if isinstance(content, str) and content.strip():
                    block_texts.append(content.strip())
            if block_texts:
                return "\n\n".join(block_texts)

    return ""


class OcrRuntime:
    def __init__(self, pipeline_version: str, device: str) -> None:
        self.pipeline_version = pipeline_version
        self.device = device
        self._lock = threading.Lock()
        print(
            f"Loading PaddleOCR-VL {pipeline_version} on {device}. "
            "The first run may download model files.",
            flush=True,
        )
        self._pipeline = PaddleOCRVL(
            pipeline_version=pipeline_version,
            device=device,
            use_doc_orientation_classify=True,
            use_doc_unwarping=True,
            format_block_content=True,
        )
        print("PaddleOCR-VL worker is ready.", flush=True)

    def recognize(self, image_bytes: bytes) -> str:
        temp_path = ""
        try:
            with tempfile.NamedTemporaryFile(suffix=image_suffix(image_bytes), delete=False) as temp_file:
                temp_file.write(image_bytes)
                temp_path = temp_file.name

            with self._lock:
                results = self._pipeline.predict(temp_path)
                pages = [extract_markdown(result) for result in results]
            return "\n\n".join(page for page in pages if page).strip()
        finally:
            if temp_path:
                try:
                    os.remove(temp_path)
                except OSError:
                    pass


class OcrHandler(BaseHTTPRequestHandler):
    runtime: OcrRuntime
    server_version = "LaserfichePaddleOCR/1.0"

    def do_GET(self) -> None:  # noqa: N802
        if self.path.rstrip("/") != "/health":
            self._json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return
        self._json(
            HTTPStatus.OK,
            {
                "status": "ready",
                "engine": "PaddleOCR-VL",
                "model": self.runtime.pipeline_version,
                "device": self.runtime.device,
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
            text = self.runtime.recognize(image_bytes)
            self._json(
                HTTPStatus.OK,
                {
                    "text": text,
                    "engine": "PaddleOCR-VL",
                    "model": self.runtime.pipeline_version,
                },
            )
        except Exception as exception:  # Paddle raises several backend-specific types.
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
    parser = argparse.ArgumentParser(description="Run the local PaddleOCR-VL worker.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--pipeline-version", default="v1.6")
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "localhost", "::1"}:
        parser.error("--host must be a loopback address")

    OcrHandler.runtime = OcrRuntime(args.pipeline_version, args.device)
    server = ThreadingHTTPServer((args.host, args.port), OcrHandler)
    print(f"Listening on http://{args.host}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Stopping PaddleOCR-VL worker.", flush=True)
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
