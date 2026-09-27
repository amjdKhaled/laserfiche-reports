import unittest
from benchmark import metrics, distance, variants, validate_endpoint
from server import extract_lines, extract_structure_text, extract_review_lines


class AccuracyTests(unittest.TestCase):
    def test_low_confidence_is_retained(self):
        lines, scores = extract_lines({'rec_texts': ['محمد', '١٢', 'مجهول'],
                                      'rec_scores': [0.2, 0.99]}, 0.35)
        self.assertEqual(lines, ['محمد', '١٢', 'مجهول'])
        self.assertEqual(scores, [0.2, 0.99])

    def test_repeated_distinct_blocks_are_retained(self):
        result = {'parsing_res_list': [{'index': 0, 'block_content': 'المبلغ'},
                                     {'index': 1, 'block_content': 'المبلغ'}],
                  'overall_ocr_res': {'rec_scores': [0.1, 0.9]}}
        text, scores = extract_structure_text(result, 0.35)
        self.assertEqual(text, 'المبلغ\n\nالمبلغ')
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


if __name__ == '__main__':
    unittest.main()
