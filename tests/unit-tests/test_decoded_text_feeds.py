"""The PNR-Linking feed through the real workers, the JSON parser and local Presidio (V-07, V-08, V-05 through the harness)."""
import json
import unittest

from decoded_text_support import EXPECTED_REJECTIONS, PROJECT, baseline, json_scalars, payload, verify_published
from pipeline_harness import run
from test_feed_logs import blocks, leaked_values

SAMPLES = PROJECT / "docs" / "sample-docs"
LINKING = SAMPLES / "PNR-Linking" / "PNR_LINKING.txt"


def compact(document):
    """Serialize a JSON document the way a producer without whitespace would."""
    return json.dumps(document, separators=(",", ":")).encode("utf-8")


class DecodedTextFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        inputs = {f"{p.parent.name}/{p.name}": p.read_bytes() for p in sorted(SAMPLES.rglob("*.txt"))}
        # A template change: the producer adds a key the recognizer file does not know, holding an undetected value.
        cls.added_text = "NEWFIELD9"
        added = json.loads(payload(LINKING.read_bytes()))
        added[0]["NewReferenceNote"] = cls.added_text
        inputs["PNR-Linking/template-added-key.bin"] = compact(added)
        # Template changes (AC-007): a key removed, and every key order reversed.
        removed = json.loads(payload(LINKING.read_bytes()))
        del removed[0]["ChangeReason"]
        inputs["PNR-Linking/template-removed-key.bin"] = compact(removed)
        reordered = json.loads(payload(LINKING.read_bytes()))
        reordered[0] = dict(reversed(list(reordered[0].items())))
        reordered[0]["Customers"] = [dict(reversed(list(c.items()))) for c in reordered[0]["Customers"]]
        inputs["PNR-Linking/template-reordered-keys.bin"] = compact(reordered)
        cls.inputs = inputs
        cls.harness = run(inputs, extra_args=["--detection-log", "on"], expect=2 if EXPECTED_REJECTIONS else 0, prefix="decoded-text-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_two_samples_published_and_the_numeric_date_of_birth_rejected_with_its_reason(self):
        items = [i for i in self.harness.report["Items"] if "template-" not in i["Source"]]
        published = [i for i in items if i["Status"] == "Published"]
        rejected = {i["Source"].replace("\\", "/").rsplit("/", 1)[-1]: i["Reason"] for i in items if i["Status"] == "Rejected"}
        self.assertEqual(len(published), 2)
        self.assertEqual(set(rejected), set(EXPECTED_REJECTIONS))
        for name, reason in EXPECTED_REJECTIONS.items():
            self.assertIn(reason, rejected[name])

    def test_published_samples_keep_framing_and_protect_every_baseline_value(self):
        document, exceptions = baseline()
        totals = {"protected": 0, "approved_exceptions": 0}
        for key, original in self.inputs.items():
            feed, name = key.split("/")
            if "template-" in name or name in EXPECTED_REJECTIONS:
                continue
            counts = verify_published(feed, name, original, self.harness.published(feed, name), document, exceptions)
            for k in totals:
                totals[k] += counts[k]
        self.assertEqual(totals["protected"] + totals["approved_exceptions"], len(document["protected"]))
        self.assertEqual(totals["approved_exceptions"], len(exceptions))

    def test_template_changes_still_publish_and_leave_an_undetected_new_value_unchanged(self):
        # WR-014: a string in which nothing is detected is published unchanged (and listed for review in the log).
        linking = json.loads(self.harness.published("PNR-Linking", "template-added-key.bin"))
        self.assertEqual(linking[0]["NewReferenceNote"], self.added_text, "the added, undetected string is published unchanged")

    def test_removed_and_reordered_template_keys_still_publish_protected(self):
        document, exceptions = baseline()
        original = dict(json_scalars(json.loads(payload(LINKING.read_bytes()))))
        for name in ("template-removed-key.bin", "template-reordered-keys.bin"):
            output = dict(json_scalars(json.loads(self.harness.published("PNR-Linking", name))))
            for e in document["protected"]:
                if e["sample"] == "PNR_LINKING.txt" and ("PNR_LINKING.txt", e["location"]) not in exceptions:
                    self.assertNotEqual(output[e["location"]], original[e["location"]],
                                        f"{name}: {e['entity']} at {e['location']} was published unprotected")
            for e in document["pnrs"]:
                if e["sample"] == "PNR_LINKING.txt":
                    self.assertEqual(output[e["location"]], original[e["location"]], f"{name}: the record locator at {e['location']} changed")

    def test_mapped_names_are_masked_to_their_width(self):
        linking = json.loads(payload(self.harness.published("PNR-Linking", "PNR_LINKING.txt")))
        names = [c["FirstName"] for c in linking[0]["Customers"] if c.get("FirstName")]
        self.assertTrue(names)
        self.assertTrue(all(set(name) == {"*"} for name in names))

    def test_detection_log_through_the_harness_is_value_free(self):
        found = blocks(self.harness.console)
        self.assertGreater(sum(len(b["entries"]) for b in found), 200)
        leaked = leaked_values("\n".join("\n".join(b["lines"][1:]) for b in found))
        self.assertEqual(leaked, [])


if __name__ == "__main__":
    unittest.main()
