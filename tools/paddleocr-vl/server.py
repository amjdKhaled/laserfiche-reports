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


MAX_REQUEST_BYTES = 70 * 1024 * 1024


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
    if markdown is None:
        return ""

    texts = markdown.get("markdown_texts", "") if isinstance(markdown, dict) else ""
    if isinstance(texts, str):
        return texts.strip()
    if isinstance(texts, list):
        return "\n\n".join(str(value).strip() for value in texts if str(value).strip())
    return str(texts).strip() if texts else ""


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
        if length <= 0 or length > MAX_REQUEST_BYTES:
            self._json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "request_too_large"})
            return

        try:
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
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
