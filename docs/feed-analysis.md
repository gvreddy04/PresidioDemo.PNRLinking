# PNR-Linking feed sample review and structure

> **Superseded design.** The removed design protected this feed with an inline field policy per JSON path in `feed-policies.xml`, and later with key-based formats, keeps and PNR anchors in the earlier recognizer format. Both are removed. Protection now uses the shared JSON parser and the Presidio-style recognizer file `pnr-linking.xml`; see [Decoded-text detection](../README.md#decoded-text-detection). The sample review, the structure findings and the field meanings below stay valid as analysis. The protection results of the removed design are frozen, value-free, in [tests/fixtures/parity-baseline.json](../tests/fixtures/parity-baseline.json).
>
> This worker keeps the PNR-Linking feed only. The review of the other feeds is in the repository history at commit `dffbdad`.

The three files in `docs/sample-docs/PNR-Linking/` were reviewed without changing source bytes. Each is an MQ text export: `A` rows carry metadata and `X` rows carry hexadecimal payload bytes. The payload is UTF-8 JSON (CCSID 1208) with an array root. Two of the files end in `_Negative_Testing`. Both parse correctly; they test what happens after parsing.

## JSON representation

`PnrLinkingParser` in `src/PresidioDemo.Core/Decoder` sets the route and the required array root and inherits `JsonFeedParser`. The shared class reads the payload with `System.Text.Json.Utf8JsonReader` and records each scalar's exact byte span. Every JSON scalar becomes a field:

- its instance path is a JSON Pointer such as `/0/Customers/1/FirstName`;
- its key path uses `*` for array indexes, such as `/*/Customers/*/FirstName`.

Every JSON string is a detection candidate. Its property names (`Customers`, `FirstName`) are the detection context. Numbers, booleans and nulls are never candidates and never change.

Duplicate or ambiguous property names, a wrong root, trailing data, truncated JSON and an MQ export with a CCSID other than 1208 reject the complete message. A rebuilt message keeps the original bytes of every unchanged scalar, property name, separator, whitespace and empty container. Inside a changed string, every unchanged character keeps its original spelling, escapes included. Validation re-parses the output and compares every byte gap, so a change outside the protected strings is refused.

## PNR-Linking

PNR_LINKING.txt (2,217 payload bytes) links two customers of one booking. It has 76 scalars, 56 of them strings:

- the link record: link identifiers, link type and version, reasons and change type, the owning record locator `PNROwner`, and timestamps;
- the flight-leg references;
- a `Customers` array. Each customer carries a passenger ID, an order ID, first and last names, a booked-as name (`SURNAME/GIVEN`), a record locator `UaRecordLocator`, a date of birth, loyalty account numbers, a status and nested `FlightSegment` and `FlightLeg` objects.

Many optional properties are `null`. `OperatingFlightNumber` is a JSON number.

The two negative samples differ from PNR_LINKING.txt in one place each:

- PNR_LINKING_02_Negative_Testing.txt (2,236 bytes) adds a `MiddleName` to the first customer. The removed design rejected it as a field without a configured policy.
- PNR_LINKING_03_Negative_Testing.txt (2,213 bytes) sends the first customer's `DOB` as a JSON number instead of a string.

The samples do not establish a general producer contract. Array position never establishes passenger identity.

## Protection with `pnr-linking.xml`

Each string is decided on its own, in this order:

| Step | Recognizer file element | Example keys | Result |
| --- | --- | --- | --- |
| 1 | `<entity_mapping>` | `FirstName`, `LastName`, `MiddleName`, `BookedAs`, `DOB`, `PaxId`, `PaxLinkId`, `PaxFlightLegLinkId`, `OrderId`, `UserId`, the loyalty account numbers, `transitionLinkSegments` | The whole string is the mapped entity and gets that entity's anonymizer. All of these mask with `*` at the same width. |
| 2 | `<keys_to_skip>` | `PNROwner`, `UaRecordLocator`, link type and version, flight-leg IDs, airports, timestamps, status | The string is published unchanged without analysis. The record locators link the bookings, so they stay unchanged. |
| 3 | `<allow_list>` | none: the item is a timestamp shape | A string that matches an item end to end is published unchanged. |
| 4 | Presidio | the reason keys `ChangeReason`, `LinkReasonAtCreation` and `ReasonForLeaving`, and any key the file does not name | Local Presidio analyzes the string. The property names of its key path are passed as Presidio context words, as Presidio's own `analyze_dict` passes a key. Each span at or above the threshold gets its entity's anonymizer; a string with nothing detected is published unchanged and listed for review. |

A key is matched ignoring case, anywhere on the key path. The innermost mapped name wins. A number or boolean under a mapped key cannot be protected in its JSON type, so the message is dead-lettered (WR-016).

## Reviewed samples and results

| Sample | Payload bytes | Result | Changed strings | Reason |
| --- | ---: | --- | ---: | --- |
| PNR_LINKING.txt | 2,217 | Published | 14 | |
| PNR_LINKING_02_Negative_Testing.txt | 2,236 | Published | 15 | The extra `MiddleName` is masked by its key |
| PNR_LINKING_03_Negative_Testing.txt | 2,213 | Dead-lettered | | A personal-data property holds a number |

These are the outcomes the monorepo reported for these samples at `dffbdad`. The parity baseline lists 14 protected values of PNR_LINKING.txt and its 3 record locators. All 14 values are protected, so there are no approved parity exceptions. The record locators, the numbers, booleans and nulls, the MQ headers, the row framing and the newline style are unchanged. The two reason strings are listed for review because nothing is detected in them.

## Reproduce verification

```powershell
dotnet build PresidioDemo.slnx
dotnet run --project tests/GenericCodecChecks --no-build
dotnet run --project tests/FeedPipelineChecks --no-build
dotnet run --project tests/DetectionParityChecks --no-build -- spike
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p "test_*.py" -v
src/.venv/Scripts/python.exe tests/run_sample_verification.py
```

The sample verification runs the three samples through `tests/FeedPipelineHarness`. It keeps the outputs, transaction logs and a value-free `verification.json` in a fresh `.test-runs/e2e-*` folder. The harness exits with code 2 because PNR_LINKING_03 is dead-lettered by design.
