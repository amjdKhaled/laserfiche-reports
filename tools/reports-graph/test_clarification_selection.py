import unittest
from clarification_options import selected_option, clarification

class SelectionTests(unittest.TestCase):
    context = {'question': 'كم وثيقة حتى 2036؟', 'prompt': 'هل تقصد؟\n1. حقل «الأول»\n2. حقل «الثاني»'}

    def test_arabic_and_western_menu_numbers(self):
        for text in ('2', '٢', '۲', ' 2 '):
            self.assertEqual(selected_option(text, self.context), 'حقل «الثاني»')

    def test_full_question_is_not_replaced(self):
        for text in ('اعرض 2 وثيقة', '2036', 'الثاني حتى 2036', '0', '3'):
            self.assertIsNone(selected_option(text, self.context))

    def test_no_pending_menu_or_duplicate_number(self):
        self.assertIsNone(selected_option('2', None))
        self.assertIsNone(selected_option('2', {'prompt': '2. one\n2. two'}))

    def test_generated_choices_are_selectable(self):
        prompt = clarification('تاريخ العقد', {'fields': [{'name': 'تاريخ العقد'}]})
        self.assertEqual(selected_option('١', {'prompt': prompt}), 'حقل «تاريخ العقد»')
        self.assertIn('اكتب رقم الخيار', prompt)

if __name__ == '__main__':
    unittest.main()
