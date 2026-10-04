import io
import unittest
from http import HTTPStatus

from request_body import RequestBodyError, read_request_body


class RequestBodyTests(unittest.TestCase):
    def test_content_length_reads_utf8_json(self):
        body = '{"question":"ما الوثيقة؟"}'.encode("utf-8")
        self.assertEqual(read_request_body({"Content-Length": str(len(body))},
                                           io.BytesIO(body), 1000), body)

    def test_chunked_body_reads_utf8_json(self):
        body = '{"question":"ما الوثيقة؟"}'.encode("utf-8")
        chunks = body[:10], body[10:]
        wire = b"".join(f"{len(chunk):X}\r\n".encode() + chunk + b"\r\n"
                        for chunk in chunks) + b"0\r\n\r\n"
        self.assertEqual(read_request_body({"Transfer-Encoding": "chunked"},
                                           io.BytesIO(wire), 1000), body)

    def test_missing_length_is_not_reported_as_oversized(self):
        with self.assertRaises(RequestBodyError) as raised:
            read_request_body({}, io.BytesIO(b"data"), 1000)
        self.assertEqual(raised.exception.status, HTTPStatus.LENGTH_REQUIRED)

    def test_chunked_body_is_bounded(self):
        with self.assertRaises(RequestBodyError) as raised:
            read_request_body({"Transfer-Encoding": "chunked"},
                              io.BytesIO(b"A\r\n" + b"x" * 10 + b"\r\n0\r\n\r\n"), 5)
        self.assertEqual(raised.exception.status, HTTPStatus.REQUEST_ENTITY_TOO_LARGE)

    def test_invalid_framing_and_incomplete_data_are_rejected(self):
        for headers, wire in (
            ({"Content-Length": "5"}, b"ab"),
            ({"Content-Length": "-1"}, b""),
            ({"Transfer-Encoding": "chunked", "Content-Length": "2"}, b"ab"),
            ({"Transfer-Encoding": "chunked"}, b"-1\r\n"),
            ({"Transfer-Encoding": "chunked"}, b"+2\r\nab\r\n0\r\n\r\n"),
            ({"Transfer-Encoding": "chunked"}, b"2\r\na"),
            ({"Transfer-Encoding": "gzip"}, b"ab")):
            with self.subTest(headers=headers, wire=wire), self.assertRaises(RequestBodyError) as raised:
                read_request_body(headers, io.BytesIO(wire), 1000)
            self.assertEqual(raised.exception.status, HTTPStatus.BAD_REQUEST)


if __name__ == "__main__":
    unittest.main()
