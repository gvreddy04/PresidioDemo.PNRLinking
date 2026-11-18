"""The PNR-Linking feed through the real workers, the JSON parser and local Presidio (V-07, V-08, V-05 through the harness)."""
import json
import re
import unittest

from decoded_text_support import EXPECTED_REJECTIONS, PROJECT, baseline, in_scope, json_scalars, payload, verify_published
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

    def test_every_sample_publishes_including_the_numeric_date_of_birth(self):
        # The names-only file does not map DOB, so the date of birth sent as a JSON number publishes unchanged.
        items = [i for i in self.harness.report["Items"] if "template-" not in i["Source"]]
        published = [i for i in items if i["Status"] == "Published"]
        rejected = {i["Source"].replace("\\", "/").rsplit("/", 1)[-1]: i["Reason"] for i in items if i["Status"] == "Rejected"}
        self.assertEqual(len(published), 3)
        self.assertEqual(set(rejected), set(EXPECTED_REJECTIONS))
        for name, reason in EXPECTED_REJECTIONS.items():
            self.assertIn(reason, rejected[name])
        numeric = json.loads(payload(self.harness.published("PNR-Linking", "PNR_LINKING_03_Negative_Testing.txt")))
        original = json.loads(payload(self.inputs["PNR-Linking/PNR_LINKING_03_Negative_Testing.txt"]))
        self.assertEqual(numeric[0]["Customers"][0]["DOB"], original[0]["Customers"][0]["DOB"])
        self.assertTrue(all(set(c["FirstName"]) == {"*"} for c in numeric[0]["Customers"] if c.get("FirstName")))

    def test_published_samples_keep_framing_and_protect_every_name_of_the_baseline(self):
        document, exceptions = baseline()
        totals = {"protected": 0, "approved_exceptions": 0, "out_of_scope": 0}
        for key, original in self.inputs.items():
            feed, name = key.split("/")
            if "template-" in name or name in EXPECTED_REJECTIONS:
                continue
            counts = verify_published(feed, name, original, self.harness.published(feed, name), document, exceptions)
            for k in totals:
                totals[k] += counts[k]
        self.assertEqual(totals["protected"] + totals["approved_exceptions"], len(in_scope(document)))
        self.assertEqual(totals["out_of_scope"], len(document["protected"]) - len(in_scope(document)), "every other baseline value is out of scope")
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
            for e in in_scope(document):
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


class DefaultDetectionHarnessTests(unittest.TestCase):
    """Presidio:DefaultDetection through the real workers (--default-detection): off acts on the file's definitions only and
    says so in the run summary, with no predefined or spaCy decision in the detection log; on adds Presidio's own."""

    MESSAGE = b'[{"FirstName":"JORDAN","Note":"mail jordan.miller@example.com"}]'  # made up

    @classmethod
    def setUpClass(cls):
        inputs = {"PNR-Linking/flag.bin": cls.MESSAGE}
        cls.off = run(inputs, extra_args=["--detection-log", "on"], expect=0, prefix="default-detection-off-")
        cls.addClassCleanup(cls.off.cleanup)
        cls.on = run(inputs, extra_args=["--detection-log", "on", "--default-detection", "on"], expect=0, prefix="default-detection-on-")
        cls.addClassCleanup(cls.on.cleanup)

    def test_off_masks_the_mapped_name_only(self):
        self.assertEqual(self.off.published("PNR-Linking", "flag.bin"), b'[{"FirstName":"******","Note":"mail jordan.miller@example.com"}]')

    def test_on_masks_the_email_address_too(self):
        self.assertEqual(self.on.published("PNR-Linking", "flag.bin"), b'[{"FirstName":"******","Note":"mail *************************"}]')

    def test_the_run_shows_its_mode_and_off_logs_no_predefined_decision(self):
        self.assertIn("HARNESS Default detection off:", self.off.console)
        self.assertIn("HARNESS Default detection on:", self.on.console)
        self.assertIn("Default detection off:", (self.off.output_root / "detection-summary.txt").read_text(encoding="utf-8"))
        entries = [line for b in blocks(self.off.console) for _, line in b["entries"]]
        self.assertEqual(len(entries), 2)
        self.assertTrue(all(re.search(r" recognizer=(entity_mapping|none) ", line) for line in entries), "only the mapped key decided")
        self.assertNotIn("spacy-guess", self.off.console)
        self.assertIn("recognizer=EmailRecognizer", self.on.console)


if __name__ == "__main__":
    unittest.main()
