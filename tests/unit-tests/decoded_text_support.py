"""Value-free verification of decoded-text protection against the frozen parity baseline (WR-019), names only.

Shared by test_decoded_text_feeds.py and tests/run_sample_verification.py. The recognizer file defines passenger names
only, so only the baseline's PERSON values are checked; every other baseline value is counted as out of scope and never
fails a check. Nothing here prints or returns a sample value; failures name the sample, location and check only.
"""
from pathlib import Path
import json

PROJECT = Path(__file__).resolve().parents[2]
FIXTURES = PROJECT / "tests" / "fixtures"
# The negative samples that must be dead-lettered, with their reason. None: PNR_LINKING_03 sends a date of birth as a JSON
# number, which dead-lettered while DOB was a mapped key; the file now maps passenger names only, so it publishes with the
# number unchanged. A number or boolean under a mapped name key still dead-letters (WR-016); FeedPipelineChecks covers it.
EXPECTED_REJECTIONS = {}
# The one baseline entity in scope: the recognizer file defines passenger names only.
SCOPE_ENTITY = "PERSON"


def payload(message):
    """Return the payload bytes of an IBM MQ export (X rows) or a raw payload."""
    lines = message.decode("utf-8", errors="strict").splitlines() if message[:2] == b"A " else None
    if lines is None:
        return message
    return bytes.fromhex("".join(line[2:].strip() for line in lines if line.startswith("X ")))


def header(message):
    """Return the MQ metadata rows of an export."""
    return [line for line in message.decode("utf-8").splitlines() if line.startswith("A ")]


def in_scope(document):
    """Return the baseline values in the names-only scope."""
    return [entry for entry in document["protected"] if entry["entity"] == SCOPE_ENTITY]


def baseline():
    """Return the frozen baseline and the approved exceptions as (sample, location) keys."""
    document = json.loads((FIXTURES / "parity-baseline.json").read_text(encoding="utf-8"))
    exceptions = {(e["sample"], e["location"]) for e in json.loads((FIXTURES / "parity-exceptions.json").read_text(encoding="utf-8"))}
    return document, exceptions


def json_scalars(node, path=""):
    """Yield (path, value) for every JSON scalar, with paths in the baseline's /0/Customers/1/FirstName form."""
    if isinstance(node, dict):
        for key, value in node.items():
            yield from json_scalars(value, path + "/" + key.replace("~", "~0").replace("/", "~1"))
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from json_scalars(value, f"{path}/{index}")
    else:
        yield path, node


def verify_published(feed, name, original, output, document=None, exceptions=None):
    """Check one published sample; return value-free counts, raise AssertionError on a failed check. Baseline values
    outside the names-only scope are counted as out_of_scope and not checked."""
    if document is None:
        document, exceptions = baseline()
    before, after = payload(original), payload(output)
    checks = {"protected": 0, "approved_exceptions": 0, "out_of_scope": 0}
    if original[:2] == b"A ":
        if header(original) != header(output):
            raise AssertionError(f"{name}: MQ metadata changed")
        if original.endswith(b"\n") != output.endswith(b"\n"):
            raise AssertionError(f"{name}: final newline changed")
        rows = [len(line) for line in output.splitlines() if line.startswith(b"X ")]
        width = len(next(line for line in original.splitlines() if line.startswith(b"X ")))
        # The export keeps its hex row width; only the last row may be shorter (WR-004).
        if any(row != width for row in rows[:-1]) or rows[-1] > width:
            raise AssertionError(f"{name}: MQ row framing changed")
    left, right = dict(json_scalars(json.loads(before))), dict(json_scalars(json.loads(after)))
    if list(left) != list(right):
        raise AssertionError(f"{name}: JSON structure changed")
    for path, value in left.items():
        if not isinstance(value, str):
            if type(right[path]) is not type(value) or right[path] != value:
                raise AssertionError(f"{name}: a number, boolean or null at {path} changed")
            continue
        published = right[path]
        if not isinstance(published, str) or len(published) != len(value):
            raise AssertionError(f"{name}: the string at {path} changed its width")
        # Every action's result is fitted printable ASCII (WR-015, WR-016), whatever operator the recognizer file chooses.
        if any(a != b and not " " <= b <= "~" for a, b in zip(value, published)):
            raise AssertionError(f"{name}: the string at {path} gained a character that is not printable ASCII")
    if len(before) != len(after) and before.isascii():
        raise AssertionError(f"{name}: an ASCII payload changed its length")
    for entry in document["protected"]:
        if entry["sample"] != name:
            continue
        if entry["entity"] != SCOPE_ENTITY:
            checks["out_of_scope"] += 1
            continue
        value, published = left[entry["location"]], right[entry["location"]]
        caught = all(a == "*" or a != b for a, b in zip(value, published))
        if caught:
            checks["protected"] += 1
        elif (name, entry["location"]) in exceptions:
            checks["approved_exceptions"] += 1
        else:
            raise AssertionError(f"{name}: baseline {entry['entity']} at {entry['location']} was published unprotected")
    return checks
