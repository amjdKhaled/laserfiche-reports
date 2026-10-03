"""Read bounded HTTP request bodies with explicit or chunked framing."""

from http import HTTPStatus


class RequestBodyError(ValueError):
    def __init__(self, status: HTTPStatus, error: str, request_bytes: int | None = None):
        super().__init__(error)
        self.status = status
        self.error = error
        self.request_bytes = request_bytes


def read_request_body(headers, stream, max_bytes: int) -> bytes:
    transfer = headers.get("Transfer-Encoding", "").lower().strip()
    length_header = headers.get("Content-Length")
    if transfer:
        if transfer != "chunked" or length_header is not None:
            raise RequestBodyError(HTTPStatus.BAD_REQUEST, "unsupported_request_framing")
        chunks = []
        total = 0
        while True:
            line = stream.readline(128)
            if not line.endswith(b"\r\n"):
                raise RequestBodyError(HTTPStatus.BAD_REQUEST, "invalid_chunk_size")
            try:
                token = line[:-2].split(b";", 1)[0]
                if not token or any(c not in b"0123456789abcdefABCDEF" for c in token):
                    raise ValueError("Invalid chunk size.")
                size = int(token, 16)
            except ValueError as error:
                raise RequestBodyError(HTTPStatus.BAD_REQUEST, "invalid_chunk_size") from error
            if size == 0:
                for _ in range(32):
                    trailer = stream.readline(8192)
                    if trailer == b"\r\n":
                        return b"".join(chunks)
                    if not trailer.endswith(b"\r\n"):
                        break
                raise RequestBodyError(HTTPStatus.BAD_REQUEST, "invalid_chunk_trailer")
            total += size
            if total > max_bytes:
                raise RequestBodyError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                                       "request_too_large", total)
            chunk = stream.read(size)
            if len(chunk) != size or stream.read(2) != b"\r\n":
                raise RequestBodyError(HTTPStatus.BAD_REQUEST, "incomplete_chunk")
            chunks.append(chunk)
    if length_header is None:
        raise RequestBodyError(HTTPStatus.LENGTH_REQUIRED, "content_length_required")
    try:
        length = int(length_header)
    except ValueError as error:
        raise RequestBodyError(HTTPStatus.BAD_REQUEST, "invalid_content_length") from error
    if length > max_bytes:
        raise RequestBodyError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large", length)
    if length < 1:
        raise RequestBodyError(HTTPStatus.BAD_REQUEST, "empty_request")
    body = stream.read(length)
    if len(body) != length:
        raise RequestBodyError(HTTPStatus.BAD_REQUEST, "incomplete_request")
    return body
