import unittest
from benchmark import metrics, distance, variants, validate_endpoint
from server import (
    OcrCandidate,
    build_candidate,
    extract_lines,
    extract_review_lines,
    extract_structure_text,
    image_variants,
    select_candidate,
    text_noise_ratio,
)


class FakeMorphology:
    def __init__(self, coverage, token_count):
        self.coverage = coverage
        self.token_count = token_count

    def score(self, _text):
        return self.coverage, self.token_count


def candidate(name, quality, text='نص عربي صالح للاختبار'):
    return OcrCandidate(name, text, [0.8], [], 0.8, 5, quality)


class AccuracyTests(unittest.TestCase):
    def test_low_confidence_is_excluded_from_search_text(self):
        lines, scores = extract_lines({'rec_texts': ['محمد', '١٢', 'مجهول'],
                                      'rec_scores': [0.2, 0.99]}, 0.35)
        self.assertEqual(lines, ['١٢'])
        self.assertEqual(scores, [0.99])

    def test_adjacent_identical_blocks_are_coalesced(self):
        result = {'parsing_res_list': [{'index': 0, 'block_content': 'المبلغ'},
                                     {'index': 1, 'block_content': 'المبلغ'}],
                  'overall_ocr_res': {'rec_scores': [0.1, 0.9]}}
        text, scores = extract_structure_text(result, 0.35)
        self.assertEqual(text, 'المبلغ')
        self.assertAlmostEqual(sum(scores) / len(scores), 0.5)

    def test_nonfinite_missing_and_low_scores_require_review(self):
        data = {'overall_ocr_res': {'rec_texts': ['أ', 'ب', 'ج', 'د', 'هـ'],
                                   'rec_scores': [float('nan'), 0.2, 0.9, 3]}}
        review = extract_review_lines(data, 0.35, 0)
        self.assertEqual([x['lineIndex'] for x in review], [0, 1, 3, 4])
        self.assertIsNone(review[0]['score'])

    def test_true_cer_and_wer(self):
        result = metrics('الموظف ١٢', 'الموظق ١٢')
        self.assertEqual(result['characterEdits'], 1)
        self.assertEqual(result['wer'], 0.5)
        self.assertTrue(result['numberSequenceExact'])
        self.assertFalse(metrics('١٢', '12')['numberSequenceExact'])
        self.assertEqual(distance('abc', 'abxc'), 1)
        self.assertEqual(distance('abc', 'ac'), 1)

    def test_no_truth_no_fabricated_rate(self):
        self.assertIsNone(metrics('', 'نص')['cer'])

    def test_preserve_arabic_distinctions(self):
        for reference, prediction in [('أ', 'ا'), ('ة', 'ه'), ('ى', 'ي'), ('بَ', 'ب')]:
            self.assertGreater(metrics(reference, prediction)['cer'], 0)

    def test_endpoint_stays_local(self):
        self.assertEqual(validate_endpoint('http://127.0.0.1:8765/'), 'http://127.0.0.1:8765')
        for value in ['http://example.com', 'http://localhost.evil', 'http://a@localhost']:
            with self.assertRaises(ValueError):
                validate_endpoint(value)

    def test_original_pixels_and_variant_sizes(self):
        from PIL import Image
        import io
        source = io.BytesIO()
        Image.new('RGB', (20, 10), 'white').save(source, format='PNG')
        raw = source.getvalue()
        values = dict(variants(raw, ['original', 'grayscale', 'contrast', 'scale2']))
        self.assertEqual(values['original'], raw)
        with Image.open(io.BytesIO(values['scale2'])) as im:
            self.assertEqual(im.size, (40, 20))

    def test_original_opencv_profile_preserves_exact_bytes(self):
        raw = b'not-decoded-because-original-does-not-need-opencv'
        self.assertEqual(image_variants(raw, 'original'), [('original', raw)])

    def test_camel_signal_scores_but_does_not_rewrite_text(self):
        text = 'المنطقة الاقتصادية الخاصة بجازان'
        value = build_candidate('clahe', text, [0.9, 0.8], [], FakeMorphology(1.0, 5))
        self.assertEqual(value.text, text)
        self.assertEqual(value.morphology_coverage, 1.0)
        self.assertGreater(value.quality_score, 0.8)

    def test_candidate_requires_clear_improvement_over_original(self):
        original = candidate('original', 0.70)
        self.assertIs(select_candidate([original, candidate('clahe', 0.72)]), original)
        self.assertEqual(
            select_candidate([original, candidate('clahe', 0.75)]).variant,
            'clahe',
        )

    def test_short_fragment_cannot_replace_complete_original(self):
        original = candidate('original', 0.60, ' '.join(['نص'] * 20))
        short = candidate('clahe', 0.95, 'نص قصير')
        self.assertIs(select_candidate([original, short]), original)

    def test_noise_penalizes_isolated_arabic_characters_and_duplicates(self):
        clean = 'لائحة المنطقة الاقتصادية الخاصة بجازان'
        noisy = 'ل ل ي ي\nل ل ي ي\nل ل ي ي'
        self.assertLess(text_noise_ratio(clean), text_noise_ratio(noisy))


if __name__ == '__main__':
    unittest.main()
