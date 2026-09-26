#!/usr/bin/env python3
"""Local, paired OCR experiments. Never select production output using test truth."""
from __future__ import annotations
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import time
import urllib.request
from urllib.parse import urlparse


def distance(a, b):
    """Levenshtein distance with O(len(b)) memory; no Arabic normalization."""
    previous = list(range(len(b) + 1))
    for i, left in enumerate(a, 1):
        current = [i]
        for j, right in enumerate(b, 1):
            current.append(min(current[-1] + 1, previous[j] + 1,
                               previous[j - 1] + (left != right)))
        previous = current
    return previous[-1]


def metrics(reference, hypothesis):
    # Only normalize transport line endings. Diacritics, digits and hamzas stay.
    reference = reference.replace('\r\n', '\n').replace('\r', '\n')
    hypothesis = hypothesis.replace('\r\n', '\n').replace('\r', '\n')
    words = reference.split()
    edits = distance(reference, hypothesis)
    word_edits = distance(words, hypothesis.split())
    numbers = re.findall(r'\d+(?:[./:\-٬٫]\d+)*', reference)
    predicted_numbers = re.findall(r'\d+(?:[./:\-٬٫]\d+)*', hypothesis)
    return {'characterEdits': edits, 'referenceCharacters': len(reference),
            'wordEdits': word_edits, 'referenceWords': len(words),
            'cer': edits / len(reference) if reference else None,
            'wer': word_edits / len(words) if words else None,
            'exactMatch': reference == hypothesis,
            'numberSequenceExact': numbers == predicted_numbers,
            'hasReferenceNumbers': bool(numbers),
            'numberTokenEdits': distance(numbers, predicted_numbers)}


def variants(image_bytes, names):
    """Original remains untouched; changes are independent, never chained."""
    from PIL import Image, ImageOps
    with Image.open(io.BytesIO(image_bytes)) as source:
        if getattr(source, 'n_frames', 1) != 1:
            raise ValueError('Export multi-page input to one image per page first.')
        for name in names:
            if name == 'original':
                yield name, image_bytes
                continue
            rgb = source.convert('RGB')
            if name == 'grayscale':
                candidate = ImageOps.grayscale(rgb)
            elif name == 'contrast':
                candidate = ImageOps.autocontrast(rgb, cutoff=0)
            elif name == 'scale2':
                candidate = rgb.resize((rgb.width * 2, rgb.height * 2), Image.Resampling.LANCZOS)
            else:
                raise ValueError(f'Unknown variant: {name}')
            output = io.BytesIO()
            candidate.save(output, format='PNG')
            yield name, output.getvalue()


def validate_endpoint(endpoint):
    url = urlparse(endpoint)
    if (url.scheme != 'http' or url.hostname not in {'127.0.0.1', 'localhost', '::1'}
            or url.username or url.password or url.query or url.fragment):
        raise ValueError('Use an HTTP loopback worker URL without credentials/query/fragment.')
    return endpoint.rstrip('/')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('OCR redirects are disabled; document must stay local.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', type=Path, help='JSON list: id, image, reference (UTF-8 file)')
    parser.add_argument('--endpoint', default='http://127.0.0.1:8765')
    parser.add_argument('--variants', nargs='+', default=['original', 'grayscale', 'contrast', 'scale2'],
                        choices=['original', 'grayscale', 'contrast', 'scale2'])
    parser.add_argument('--output', type=Path, required=True, help='New local report path; contains document text')
    parser.add_argument('--timeout', type=int, default=1800)
    args = parser.parse_args()
    endpoint = validate_endpoint(args.endpoint)
    if args.output.exists():
        parser.error('Output already exists; choose a new report name to preserve the baseline.')
    samples = json.loads(args.manifest.read_text(encoding='utf-8-sig'))
    if not isinstance(samples, list) or not samples:
        parser.error('Manifest must be a non-empty list.')
    ids = [str(sample['id']) for sample in samples]
    if len(set(ids)) != len(ids):
        parser.error('Sample IDs must be unique.')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(endpoint + '/health', timeout=10) as response:
        health = json.load(response)
    rows = []
    for sample in samples:
        image_bytes = (args.manifest.parent / sample['image']).read_bytes()
        truth = (args.manifest.parent / sample['reference']).read_text(encoding='utf-8-sig')
        if not truth:
            parser.error(f"Empty reference for {sample['id']}")
        for name, data in variants(image_bytes, args.variants):
            started = time.perf_counter()
            request = urllib.request.Request(endpoint + '/ocr', data=data,
                        headers={'Content-Type': 'application/octet-stream'})
            with opener.open(request, timeout=args.timeout) as response:
                result = json.load(response)
            digest = hashlib.sha256(data).hexdigest()
            if result.get('imageSha256') != digest:
                raise ValueError('Worker returned a different image identity.')
            text = result['text']
            rows.append({'id': str(sample['id']), 'variant': name,
                         'originalImageSha256': hashlib.sha256(image_bytes).hexdigest(),
                         'imageSha256': digest,
                         'referenceSha256': hashlib.sha256(truth.encode('utf-8')).hexdigest(),
                         'elapsedSeconds': time.perf_counter() - started,
                         'metrics': metrics(truth, text), 'response': result})
            print(f"{sample['id']} {name}: CER={rows[-1]['metrics']['cer']:.4f}", flush=True)
    summary = {}
    for name in args.variants:
        selected = [row['metrics'] for row in rows if row['variant'] == name]
        chars = sum(x['referenceCharacters'] for x in selected)
        words = sum(x['referenceWords'] for x in selected)
        summary[name] = {'cer': sum(x['characterEdits'] for x in selected) / chars if chars else None,
                         'wer': sum(x['wordEdits'] for x in selected) / words if words else None,
                         'exactPages': sum(x['exactMatch'] for x in selected),
                         'pages': len(selected)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('x', encoding='utf-8') as output:
        json.dump({'worker': health, 'summary': summary, 'rows': rows}, output,
                  ensure_ascii=False, indent=2, allow_nan=False)


if __name__ == '__main__':
    main()
