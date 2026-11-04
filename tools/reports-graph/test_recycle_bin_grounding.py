import json
import unittest

from server import ground_recycle_bin, planner_request

CATALOG = {"recycleBin": {"status": "unavailable", "reason": "ربط سلة المحذوفات غير مهيأ."}}


def searched(count_only=True):
    # Seen 2026-10-29: «كم وثيقة في سلة المحذوفات» came back as a count of the whole repository.
    return planner_request(json.dumps({"reports": [{"operation": "search", "title": "عدد الوثائق في سلة المحذوفات",
                                                    "countOnly": count_only, "selection": {"requiresFilter": False}}]}), "كم وثيقة في سلة المحذوفات")


class RecycleBinGroundingTests(unittest.TestCase):
    def test_unavailable_bin_is_explained_not_searched(self):
        grounded = ground_recycle_bin(searched(), "كم وثيقة في سلة المحذوفات", CATALOG)
        self.assertEqual("clarify", grounded.reports[0].operation)
        self.assertEqual("ربط سلة المحذوفات غير مهيأ.", grounded.clarification)

    def test_available_bin_is_read(self):
        grounded = ground_recycle_bin(searched(), "how many documents are in the recycle bin?", {"recycleBin": {"status": "available"}})
        self.assertEqual(("recycle_bin", True), (grounded.reports[0].operation, grounded.reports[0].countOnly))

    def test_other_questions_are_unchanged(self):
        request = searched(False)
        self.assertIs(request, ground_recycle_bin(request, "الوثائق المحذوفة من الفهرس", CATALOG))


if __name__ == "__main__":
    unittest.main()
