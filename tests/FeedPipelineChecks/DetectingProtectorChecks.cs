using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;

/// <summary>
/// Checks the decoded-text protector (WR-014, WR-015): every JSON string is decided on its own, a mapped key is its entity
/// across the whole string, a skipped key and an allow-list match stay unchanged, detected spans get their entity's action
/// fitted to width, a string in which nothing is detected is published unchanged and listed for review, numbers, booleans
/// and nulls never change, detection failures dead-letter, and every production sample rebuilds and validates.
/// </summary>
internal static class DetectingProtectorChecks
{

    #region Methods

    /// <summary>
    /// Runs the checks. Python must be started; the protector is configured here, after the failure-injection check.
    /// </summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <returns>A task that completes when every check passed.</returns>
    public static async Task RunAsync(string projectRoot)
    {
        string recognizers = Path.Combine(projectRoot, "src", "PresidioDemo.Core", "Configuration", "recognizers");
        var files = RecognizerFileReader.ReadAll(recognizers);
        var shippedFile = files[FeedType.PnrLinking];
        string samples = Path.Combine(projectRoot, "docs", "sample-docs");
        string linkingSample = Path.Combine(samples, "PNR-Linking", "PNR_LINKING.txt");
        var protector = new DetectingFeedProtector(files);

        // Detection failure: the bridge has no registry for an unconfigured feed name, so the call fails and dead-letters.
        var unconfigured = new DetectingFeedProtector(new Dictionary<FeedType, RecognizerFile> { [FeedType.PnrLinking] = shippedFile });
        await RejectAsync(() => unconfigured.ProtectAsync(FeedType.PnrLinking, Parse(linkingSample), CancellationToken.None),
            "Local Presidio detection failed", "a Presidio failure before configuration dead-letters");
        protector.Configure();

        int published = 0;
        foreach (string path in Directory.EnumerateFiles(samples, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Contains("Negative_Testing", StringComparison.Ordinal))
            {
                continue;
            }
            string name = Path.GetFileName(path);
            var feed = FeedFileRouting.FromPath(path);
            var codec = Codec(feed);
            var parsed = codec.Parse(Message(path, feed));
            var strings = JsonFeedParser.StringFields(parsed);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            byte[] output = codec.Serialize(parsed, outcome.Values);
            codec.Validate(parsed, output);
            Check(strings.All(f => outcome.Values[f.Id].Length == f.Value.Length), name + ": every protected string keeps its width");
            var stringIds = strings.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            Check(parsed.Fields.Where(f => !stringIds.Contains(f.Id)).All(f => outcome.Values[f.Id] == f.Value), name + ": numbers, booleans and nulls never change");
            Check(outcome.Events.Count == strings.Count, name + ": every JSON string is one candidate");
            var decoded = strings.Select(f => f.Value).Where(v => v.Any(char.IsAsciiLetterOrDigit) && DetectingFeedProtector.Shape(v) != v).ToHashSet(StringComparer.Ordinal);
            Check(outcome.Events.All(e => !decoded.Contains(e.Shape) && !decoded.Contains(e.Recognizer) && !decoded.Contains(e.Entity)),
                name + ": no detection event field carries a decoded value");
            // WR-020: the decoded values travel beside the events, one per slot, for the values flag only.
            Check(outcome.CandidateValues is { } carried && carried.Count == outcome.Events.Count && carried.SequenceEqual(strings.Select(f => f.Value)),
                name + ": the candidate values match the decoded strings by slot");
            // WR-018: a JSON string is located by its pointer; the value-free pointer keeps declared names and array indexes only.
            Check(outcome.Events.Zip(strings).All(pair => pair.First.Pointer == pair.Second.Id && pair.First.Pattern == pair.Second.Name),
                name + ": each event carries its string's JSON Pointer and key path");
            Check(outcome.Events.All(e => e.SafePointer.Split('/').Skip(1).All(segment => segment == "?" || segment.All(char.IsAsciiDigit) || shippedFile.Declares(JsonFeedParser.Unescape(segment)))),
                name + ": a value-free pointer holds only declared names, array indexes and ?");
            Check(strings.Where(f => JsonFeedParser.PropertyNames(f.Name).LastOrDefault() is "PNROwner" or "UaRecordLocator").All(f => outcome.Values[f.Id] == f.Value),
                name + ": every record locator stays unchanged");
            published++;
        }
        Console.WriteLine($"PASS {published} production samples protected, rebuilt and validated with the recognizer files");

        // The negative samples as the monorepo expected them: an extra name field is protected by its key and published;
        // a date of birth sent as a JSON number cannot be protected in its type and dead-letters (WR-016).
        string extraField = Path.Combine(samples, "PNR-Linking", "PNR_LINKING_02_Negative_Testing.txt");
        var extraParsed = Parse(extraField);
        var extraOutcome = await protector.ProtectAsync(FeedType.PnrLinking, extraParsed, CancellationToken.None);
        var middle = extraParsed.Fields.Single(f => f.Id.EndsWith("/MiddleName", StringComparison.Ordinal));
        Check(extraOutcome.Values[middle.Id] == new string('*', middle.Value.Length), "PNR_LINKING_02: the extra MiddleName is masked by its key and the message publishes");
        await RejectAsync(() => protector.ProtectAsync(FeedType.PnrLinking, Parse(Path.Combine(samples, "PNR-Linking", "PNR_LINKING_03_Negative_Testing.txt")), CancellationToken.None),
            "number or boolean", "PNR_LINKING_03: a numeric date of birth dead-letters");

        // WR-018, WR-020: one block per message; every string in exactly one group; review lines carry a reason and a fix.
        DetectionEvent[] events =
        [
            new(0, 13, "AAAAAA/AAAAAA", "passengerName", "PERSON", 0.9, DetectionDecision.Changed, "mask", Pointer: "/0/Notes/0", SafePointer: "/0/?/0", Pattern: "/*/Notes/*"),
            new(1, 6, "AAA999", "none", "", 0, DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected, Threshold: 0.5,
                Pointer: "/0/Notes/1", SafePointer: "/0/?/1", Pattern: "/*/Notes/*"),
            new(2, 22, "AAAA AAA", DetectingFeedProtector.SpacyRecognizer, "LOCATION", 0.85, DetectionDecision.Changed, "mask", ReviewReason.SpacyGuess, 0.5,
                Pointer: "/0/Notes/2", SafePointer: "/0/?/2", Pattern: "/*/Notes/*"),
            new(3, 6, "AA9999", DetectingFeedProtector.KeysToSkipRecognizer, "", 0, DetectionDecision.Allowed, Pointer: "/0/FlightLegId", SafePointer: "/0/FlightLegId",
                Pattern: "/*/FlightLegId"),
            new(4, 10, "AA99999999", "UsLicenseRecognizer", "US_DRIVER_LICENSE", 0.3, DetectionDecision.BelowThreshold, Reasons: ReviewReason.BelowThreshold, Threshold: 0.5,
                Pointer: "/0/Notes/4", SafePointer: "/0/?/4", Pattern: "/*/Notes/*"),
            new(5, 7, "99AAA99", DetectingFeedProtector.EntityMappingRecognizer, "DATE_OF_BIRTH", 1, DetectionDecision.Changed, "mask",
                Pointer: "/0/Customers/5/DOB", SafePointer: "/0/?/5/DOB", Pattern: "/*/Customers/*/DOB"),
            new(6, 12, "AAAA AAAAAAA", "EmailRecognizer", "EMAIL_ADDRESS", 1, DetectionDecision.Changed, "replace", ReviewReason.NoAction | ReviewReason.Cut, 0.5,
                Pointer: "/0/Notes/6", SafePointer: "/0/?/6", Pattern: "/*/Notes/*"),
        ];
        string[] fictionalBefore = ["MILLER/JORDAN", "ABC123", "LINE ONE\nSAY \"HI\" \\ X", "XY1234", "XY99887766", "14MAR85", "CALL O'NEIL"];
        string[] fictionalAfter = ["######/######", "ABC123", "######################", "XY1234", "XY99887766", "*******", "CALL <EMAIL"];
        string[] Lines(string block) => block.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string Section(string block, string heading)
        {
            var lines = Lines(block);
            int start = Array.FindIndex(lines, line => line == "  " + heading);
            int end = Array.FindIndex(lines, start + 1, line => !line.StartsWith("    ", StringComparison.Ordinal));
            return string.Join("\n", lines[(start + 1)..(end < 0 ? lines.Length : end)]);
        }
        const string ReviewHeading = "Review (check these; each line has a fix)";
        var log = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, events);
        var valued = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, events, before: fictionalBefore, after: fictionalAfter);
        Check(Lines(log)[0] == "PNR-Linking " + Guid.Empty.ToString("N") + " DETECTION PUBLISHED file=- fields=7 pii=2 non-pii=1 review=4",
            "the header names the feed, transaction, outcome, file (- when none) and field counts");
        Check(Lines(log).Skip(1).All(line => line.StartsWith(' ')) && Lines(valued).Skip(1).All(line => line.StartsWith(' ')),
            "every line after the header is indented, so a collector keeps the block as one entry");
        string pii = Section(log, "PII fields (changed)"), nonPii = Section(log, "Non-PII fields (unchanged)"), review = Section(log, ReviewHeading);
        bool Has(string section, string at) => section.Split('\n').Any(line => line.StartsWith("    " + at + " ", StringComparison.Ordinal));
        Check(Has(pii, "/0/?/0") && Has(pii, "/0/?/5/DOB") && Has(nonPii, "/0/FlightLegId")
            && new[] { "/0/?/1", "/0/?/2", "/0/?/4", "/0/?/6" }.All(at => Has(review, at)), "each string is listed in its group, located by its value-free JSON Pointer");
        Check(Lines(log).Count(line => line.StartsWith("    /0/", StringComparison.Ordinal)) == events.Length, "each string appears exactly once");
        string ReviewLine(string at) => review.Split('\n').Single(line => line.StartsWith("    " + at + " ", StringComparison.Ordinal));
        Check(ReviewLine("/0/?/1").Contains("  unchanged  ", StringComparison.Ordinal)
            && ReviewLine("/0/?/1").Contains("reason=nothing-detected fix=\"add a recognizer if this is personal data, or an allow-list item if it is not\"", StringComparison.Ordinal),
            "a nothing-detected string is listed unchanged with its fix");
        Check(ReviewLine("/0/?/4").Contains("score=0.30<0.50", StringComparison.Ordinal) && ReviewLine("/0/?/4").Contains("reason=below-threshold", StringComparison.Ordinal),
            "a below-threshold string compares its score with the threshold");
        Check(ReviewLine("/0/?/2").Contains("  changed  ", StringComparison.Ordinal) && ReviewLine("/0/?/2").Contains("operator=mask decision=changed reason=spacy-guess", StringComparison.Ordinal),
            "a spaCy guess is listed for review although it changed the string");
        Check(ReviewLine("/0/?/6").Contains("reason=no-action+cut fix=\"add an <anonymizer> for EMAIL_ADDRESS; use mask for this entity, or a shorter new_value\"", StringComparison.Ordinal),
            "several reasons are listed with one fix each");
        Check(pii.Contains("recognizer=entity_mapping entity=DATE_OF_BIRTH score=1.00 operator=mask decision=changed", StringComparison.Ordinal)
            && nonPii.Contains("recognizer=keys_to_skip entity=- score=0.00 decision=allowed", StringComparison.Ordinal),
            "a mapped key and a skipped key are named as the recognizer that decided");
        Check(fictionalBefore.All(value => !log.Contains(value, StringComparison.Ordinal)) && fictionalAfter.Where(value => !value.All(c => c == '#')).All(value => !log.Contains(value, StringComparison.Ordinal))
            && !log.Contains("->", StringComparison.Ordinal) && !log.Contains("Notes", StringComparison.Ordinal) && log.Contains("len=13 shape=AAAAAA/AAAAAA", StringComparison.Ordinal),
            "a value-free block carries lengths, shapes and declared names, never a value or an undeclared name");
        Check(Lines(valued).Length == Lines(log).Length, "an encoded value never breaks a line");
        Check(valued.Contains("\"MILLER/JORDAN\" -> \"######/######\"", StringComparison.Ordinal) && valued.Contains("\"CALL O'NEIL\" -> \"CALL <EMAIL\"", StringComparison.Ordinal)
            && Section(valued, "Non-PII fields (unchanged)").Split('\n').Single() is { } allowedLine && allowedLine.StartsWith("    \"/0/FlightLegId\" ", StringComparison.Ordinal)
            && allowedLine.Contains("  \"XY1234\"  recognizer=keys_to_skip", StringComparison.Ordinal),
            "a changed string shows before -> after; an unchanged string shows its value once; values mode shows the full pointer");
        string encoded = Lines(valued).Single(line => line.Contains("\"/0/Notes/2\"", StringComparison.Ordinal));
        string afterPointer = encoded[(encoded.IndexOf("\"/0/Notes/2\"", StringComparison.Ordinal) + "\"/0/Notes/2\"".Length)..].TrimStart();
        Check(JsonSerializer.Deserialize<string>(afterPointer[..afterPointer.IndexOf(" -> ", StringComparison.Ordinal)]) == fictionalBefore[2], "an encoded value reads back exactly");
        var rejected = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, events, "Fixture rejection.", fictionalBefore, null);
        Check(Lines(rejected)[0].EndsWith(" DETECTION DEAD-LETTERED file=- fields=7 pii=2 non-pii=1 review=4 reason=\"Fixture rejection.\"", StringComparison.Ordinal)
            && rejected.Contains("\"MILLER/JORDAN\" -> not published", StringComparison.Ordinal), "a dead-lettered block gives the reason and no after value");
        var allowedOnly = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, [events[3]]);
        Check(Section(allowedOnly, "PII fields (changed)") == "    (none)" && Section(allowedOnly, ReviewHeading) == "    (none)",
            "an empty group is printed as (none)");
        // Review R-003: fewer values than events must not fail; a slot without a value keeps the value-free form.
        var shortValues = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, events, before: ["MILLER/JORDAN"], after: fictionalAfter);
        Check(shortValues.Contains("\"MILLER/JORDAN\" -> \"######/######\"", StringComparison.Ordinal) && shortValues.Contains("len=6 shape=AAA999", StringComparison.Ordinal),
            "a slot without a value keeps the value-free form");
        // R-009: a property name that is itself a value (dynamic-key JSON) is ? in the value-free form; under WR-020 the full
        // pointer is JSON-encoded, so a name holding a line break cannot break the line.
        DetectionEvent[] jsonEvents = [new(0, 4, "AAAA", "none", "", 0, DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected, Threshold: 0.5,
            Pointer: "/0/con\ntacts/j.miller@example.com", SafePointer: "/0/?/?", Pattern: "/*/con\ntacts/j.miller@example.com")];
        var jsonFree = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, jsonEvents);
        var jsonValued = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, jsonEvents, before: ["HOME"], after: ["HOME"]);
        Check(jsonFree.Contains("    /0/?/?  len=4 shape=AAAA  unchanged", StringComparison.Ordinal) && !jsonFree.Contains("miller", StringComparison.Ordinal)
            && !Lines(jsonFree)[0].Contains("offsets=", StringComparison.Ordinal), "a value-free JSON entry shows undeclared names as ?");
        Check(jsonValued.Contains("    \"/0/con\\ntacts/j.miller@example.com\"  \"HOME\"  unchanged", StringComparison.Ordinal) && Lines(jsonValued).Length == Lines(jsonFree).Length,
            "a values JSON entry shows the full pointer, encoded so a key cannot break the line");
        // Review R-009: one very long location must not pad every other line of the block to its width.
        DetectionEvent[] wide =
        [
            new(0, 4, "AAAA", "none", "", 0, DetectionDecision.NothingDetected, Reasons: ReviewReason.NothingDetected, Threshold: 0.5,
                Pointer: "/0/" + new string('k', 5000), SafePointer: "/0/?", Pattern: "/*/" + new string('k', 5000)),
            new(1, 6, "AAA999", DetectingFeedProtector.KeysToSkipRecognizer, "", 0, DetectionDecision.Allowed, Pointer: "/0/PNROwner", SafePointer: "/0/PNROwner", Pattern: "/*/PNROwner"),
        ];
        var wideBlock = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, Guid.Empty, wide, before: ["HOME", "K7QX2M"], after: ["HOME", "K7QX2M"]);
        Check(Lines(wideBlock).Single(line => line.Contains("PNROwner", StringComparison.Ordinal)).Length < 150,
            "a long location is written unpadded instead of widening every line");
        // Review R-010: zero-width and bidirectional formatting characters are escaped, and the value still reads back.
        string disguised = "CALL " + (char)0x202E + "MILLER" + (char)0x200B + " HOME";
        string encodedDisguise = PresidioDemo.Core.Logging.DetectionLog.Encode(disguised);
        Check(!encodedDisguise.Contains((char)0x202E) && !encodedDisguise.Contains((char)0x200B) && encodedDisguise.Contains("\\u202E", StringComparison.Ordinal)
            && JsonSerializer.Deserialize<string>(encodedDisguise) == disguised, "formatting characters are escaped and the value reads back");
        Check(PresidioDemo.Core.Logging.DetectionLog.Encode("O'NEIL" + (char)0x2028 + "X") == "\"O'NEIL\\u2028X\"", "an apostrophe stays readable and a line separator is escaped");
        // Review R-011: the worker writes each block with one Console.Out.Write. Console.Out is a synchronized writer, so
        // blocks written from many threads at once (as by several consumers per task) must come out whole.
        Check(Console.Out.GetType().Name == "SyncTextWriter", "the console writer the worker uses is synchronized");
        var captured = new StringWriter();
        var console = Console.Out;
        Console.SetOut(TextWriter.Synchronized(captured));
        try
        {
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                Console.Out.Write(PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PnrLinking, new Guid(i, 0, 0, new byte[8]), events, before: fictionalBefore, after: fictionalAfter)));
        }
        finally
        {
            Console.SetOut(console);
        }
        var written = Lines(captured.ToString());
        int perBlock = Lines(valued).Length;
        // "PNR-Linking " plus the 32-digit transaction and a space: the rest of each header, and every body line, must match.
        const int afterTransaction = 45;
        Check(written.Length == 400 * perBlock && Enumerable.Range(0, 400).All(b =>
            written[b * perBlock][afterTransaction..] == Lines(valued)[0][afterTransaction..]
            && written.Skip(b * perBlock + 1).Take(perBlock - 1).SequenceEqual(Lines(valued).Skip(1))),
            "400 blocks written from 16 threads at once each come out whole");
        Console.WriteLine("PASS the detection log writes one grouped block per message, with a fix per review line and values only when passed (WR-018, WR-020)");

        // WR-014 on the shipped file: mapped keys change, skipped keys and undetected strings stay unchanged.
        var sampleParsed = Parse(linkingSample);
        var sampleStrings = JsonFeedParser.StringFields(sampleParsed);
        var sampleOutcome = await protector.ProtectAsync(FeedType.PnrLinking, sampleParsed, CancellationToken.None);
        Check(sampleOutcome.Events.Any(e => e.Recognizer == DetectingFeedProtector.EntityMappingRecognizer && e.Decision == DetectionDecision.Changed)
            && sampleOutcome.Events.Any(e => e.Recognizer == DetectingFeedProtector.KeysToSkipRecognizer && e.Decision == DetectionDecision.Allowed),
            "PNR_LINKING.txt has strings changed by a mapped key and strings allowed by a skipped key");
        Check(sampleOutcome.Events.Where(e => e.Decision is DetectionDecision.Allowed or DetectionDecision.NothingDetected or DetectionDecision.BelowThreshold)
            .All(e => sampleOutcome.Values[sampleStrings[e.Slot].Id] == sampleStrings[e.Slot].Value), "allowed and undetected strings are published unchanged");
        Check(sampleOutcome.Events.Where(e => e.Recognizer == DetectingFeedProtector.EntityMappingRecognizer).All(e => e.Score == 1 && e.Operator == "mask"
            && sampleOutcome.Values[sampleStrings[e.Slot].Id] == new string('*', e.Length)), "a mapped string is masked across its whole width");
        // AC-011: there is no PNR anchor; a record locator that is not PNR-shaped is published as received.
        var odd = ParseJson(Encoding.UTF8.GetString(FeedPayloadReader.ReadPayload(Message(linkingSample, FeedType.PnrLinking))).Replace("\"FWD4G2\"", "\"AB\"", StringComparison.Ordinal));
        var oddOutcome = await protector.ProtectAsync(FeedType.PnrLinking, odd, CancellationToken.None);
        Check(oddOutcome.Values[odd.Fields.First(f => f.Value == "AB").Id] == "AB", "a record locator that is not PNR-shaped is published as received, not dead-lettered");

        // Made-up strings through the shipped file under a key it neither maps nor skips (AC-004, AC-005, AC-013).
        var shipped = await ProtectStringsAsync(protector, "MILLER/JORDAN MR", "Please book a window seat", "2026-11-09T14:05:33.120Z", "2026-11-09 MILLER/JORDAN",
            "Contact Alex Sample", "jordan.miller@example.com");
        Check(shipped[0] == "****************", "a declared recognizer masks MILLER/JORDAN MR");
        Check(shipped[1] == "Please book a window seat", "a string in which nothing is detected is published unchanged");
        Check(shipped[2] == "2026-11-09T14:05:33.120Z", "a whole-string allow-list item keeps a timestamp");
        Check(!shipped[3].Contains("MILLER", StringComparison.Ordinal) && shipped[3] != "2026-11-09 MILLER/JORDAN",
            "an allow-list item never releases a detected piece inside a longer string");
        Check(shipped[4] == "Contact ***********", "Presidio's PERSON is masked by the PERSON anonymizer");
        Check(shipped[5] == new string('*', 25), "Presidio's EMAIL_ADDRESS is masked by its anonymizer");
        Console.WriteLine("PASS the shipped file acts per string: declared recognizers, allow list, unchanged when nothing is detected");

        // R-004, R-006, R-009, R-012: keys match regardless of case, a mapped key beats the allow list, reason text is
        // analyzed, a string nested under a mapped key is that entity, and an undeclared key is logged as ?.
        var json = ParseJson("[{\"PNROwner\":\"K7QX2M\",\"dob\":\"1985-03-12\",\"ReasonForLeaving\":\"REBOOKED, CONTACT MILLER/JORDAN\","
            + "\"contacts\":{\"jordan.miller@example.com\":\"HOME\"},\"UserId\":null,\"OperatingFlightNumber\":4733,\"IsIRROP\":true}]");
        var jsonOutcome = await protector.ProtectAsync(FeedType.PnrLinking, json, CancellationToken.None);
        string Value(string suffix) => jsonOutcome.Values[json.Fields.Single(f => f.Id.EndsWith(suffix, StringComparison.Ordinal)).Id];
        DetectionEvent EventOf(string suffix) => jsonOutcome.Events.Single(e => e.Pointer.EndsWith(suffix, StringComparison.Ordinal));
        Check(Value("/dob") == "**********", "a lower-case dob key is masked by its mapping, not kept as a timestamp");
        Check(!Value("/ReasonForLeaving").Contains("MILLER", StringComparison.Ordinal) && Value("/ReasonForLeaving").StartsWith("REBOOKED, ", StringComparison.Ordinal),
            "a name inside reason text is masked while the reason is kept");
        Check(Value("/PNROwner") == "K7QX2M" && EventOf("/PNROwner").Decision == DetectionDecision.Allowed
            && EventOf("/PNROwner").Recognizer == DetectingFeedProtector.KeysToSkipRecognizer, "a skipped key stays unchanged without analysis");
        Check(Value("/UserId") == "" && Value("/OperatingFlightNumber") == "4733" && Value("/IsIRROP") == "true",
            "a null under a mapped key and numbers and booleans under other keys publish unchanged");
        var dynamicKey = EventOf("/jordan.miller@example.com");
        Check(dynamicKey.SafePointer == "/0/?/?" && dynamicKey.Pointer == "/0/contacts/jordan.miller@example.com" && dynamicKey.Pattern == "/*/contacts/jordan.miller@example.com",
            "a value-free pointer shows undeclared names as ? and keeps the array index; the full pointer keeps every name");
        Check(EventOf("/PNROwner").SafePointer == "/0/PNROwner" && EventOf("/dob").SafePointer == "/0/dob", "a declared key keeps its name in the value-free pointer");
        var nested = ParseJson("[{\"PNROwner\":\"K7QX2M\",\"DOB\":{\"Date\":\"1985-03-12\"}}]");
        var nestedOutcome = await protector.ProtectAsync(FeedType.PnrLinking, nested, CancellationToken.None);
        Check(nestedOutcome.Values[nested.Fields.Single(f => f.Id.EndsWith("/DOB/Date", StringComparison.Ordinal)).Id] == "**********",
            "a string nested under a mapped key is that entity (R-012)");
        await RejectAsync(() => protector.ProtectAsync(FeedType.PnrLinking, ParseJson("[{\"PNROwner\":\"K7QX2M\",\"DOB\":{\"Year\":1985}}]"), CancellationToken.None),
            "number or boolean", "a number nested under a mapped key dead-letters");
        await RejectAsync(() => protector.ProtectAsync(FeedType.PnrLinking, ParseJson("[{\"FirstName\":true}]"), CancellationToken.None),
            "number or boolean", "a boolean under a mapped key dead-letters");
        // Bytes outside a changed string stay exactly as received: numeric spelling, escapes, whitespace and nulls.
        string spelled = "[ {\"OperatingFlightNumber\":4.7330e3, \"IsIRROP\":false,\"LinkType\":\"F\\u0061mily\",\"FirstName\":\"Em\\u0069ly\",\"UserId\":null} ]";
        var spelledParsed = ParseJson(spelled);
        var spelledOutcome = await protector.ProtectAsync(FeedType.PnrLinking, spelledParsed, CancellationToken.None);
        var linkingCodec = Codec(FeedType.PnrLinking);
        byte[] spelledOutput = linkingCodec.Serialize(spelledParsed, spelledOutcome.Values);
        linkingCodec.Validate(spelledParsed, spelledOutput);
        Check(Encoding.UTF8.GetString(spelledOutput) == spelled.Replace("\"Em\\u0069ly\"", "\"*****\"", StringComparison.Ordinal),
            "only the mapped string changes; every other byte, escapes and numeric spelling included, is published as received");
        Console.WriteLine("PASS JSON keys ignore case, reason text is analyzed, nested mapped numbers dead-letter, undeclared keys log as ?");

        // AC-003, AC-006, AC-008: test-only files reconfigure the PNR-Linking registry, so the shipped one is restored afterwards.
        string Temporary(string content)
        {
            string file = Path.Combine(Path.GetTempPath(), "pnr-linking-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, content);
            return file;
        }
        DetectingFeedProtector Configured(string file)
        {
            var configured = new DetectingFeedProtector(new Dictionary<FeedType, RecognizerFile> { [FeedType.PnrLinking] = RecognizerFileReader.Read(file, FeedType.PnrLinking) });
            configured.Configure();
            return configured;
        }
        string empty = Temporary("<recognizers feed=\"PNR-Linking\" />");
        string noCards = Temporary("<recognizers feed=\"PNR-Linking\"><recognizer name=\"CreditCardRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<anonymizer entity=\"PERSON\" type=\"mask\" /></recognizers>");
        string withDefault = Temporary("<recognizers feed=\"PNR-Linking\"><anonymizer entity=\"PERSON\" type=\"mask\" /><anonymizer entity=\"DEFAULT\" type=\"mask\" /></recognizers>");
        string keyed = Temporary("<recognizers feed=\"PNR-Linking\"><entity_mapping><key name=\"Remarks\" entity=\"PERSON\" /></entity_mapping>"
            + "<keys_to_skip><key name=\"Notes\" /></keys_to_skip></recognizers>");
        try
        {
            var bare = await ProtectStringsAsync(Configured(empty), "Please book a window seat", "Contact Alex Sample", "alex@example.com", "Card 4111 1111 1111 1111");
            Check(bare[0] == "Please book a window seat", "an emptied file starts and publishes an undetected string unchanged");
            Check(bare[1] == "Contact <PERSON>   " && bare[2] == "<EMAIL_ADDRESS> " && bare[3] == "Card " + "<CREDIT_CARD>".PadRight(19),
                "with nothing configured Presidio's predefined recognizers detect, and replace is fitted to width");
            var switchedOff = await ProtectStringsAsync(Configured(noCards), "Card 4111 1111 1111 1111", "Contact Alex Sample", "alex@example.com");
            Check(switchedOff[0] == "Card 4111 1111 1111 1111", "a predefined recognizer switched off detects nothing");
            Check(switchedOff[1] == "Contact ***********" && switchedOff[2] == "<EMAIL_ADDRESS> ", "an entity uses its own anonymizer, else replace");
            var fallback = await ProtectStringsAsync(Configured(withDefault), "alex@example.com");
            Check(fallback[0] == "****************", "an entity without its own anonymizer uses DEFAULT");
            var mapped = ParseJson("[{\"Remarks\":\"LI WU\",\"Notes\":\"Contact Alex Sample\"}]");
            var mappedOutcome = await Configured(keyed).ProtectAsync(FeedType.PnrLinking, mapped, CancellationToken.None);
            Check(mappedOutcome.ProtectedCandidateValues![0] == "<PERS" && mappedOutcome.Events[0].Recognizer == DetectingFeedProtector.EntityMappingRecognizer
                && mappedOutcome.Events[0].Reasons == (ReviewReason.NoAction | ReviewReason.Cut), "a mapped key without an anonymizer of its own uses replace, fitted, and is listed for review");
            Check(mappedOutcome.ProtectedCandidateValues[1] == "Contact Alex Sample" && mappedOutcome.Events[1].Decision == DetectionDecision.Allowed,
                "a skipped key publishes a detectable name unchanged, because it is never analyzed");
            Console.WriteLine("PASS an emptied file runs Presidio's defaults; switches, key mappings and anonymizers change the output with no code change");

            // Review reasons as the protector produces them (made-up strings).
            var guess = await ProtectOutcomeAsync(Configured(withDefault), "Contact Alex Sample", "alex@example.com", "LI WU");
            Check(guess.Events[0].Reasons.HasFlag(ReviewReason.SpacyGuess) && !guess.Events[0].Reasons.HasFlag(ReviewReason.NoAction),
                "a spaCy PERSON with its own anonymizer is a spacy-guess, not a no-action");
            Check(guess.Events[1].Reasons.HasFlag(ReviewReason.NoAction) && guess.Events[1].NoActionEntities.Contains("EMAIL_ADDRESS", StringComparison.Ordinal),
                "an entity that used DEFAULT is a no-action naming that entity");
            var mixed = await ProtectOutcomeAsync(Configured(noCards), "Contact Alex Sample at alex@example.com");
            Check(mixed.Events[0].NoActionEntities.Contains("EMAIL_ADDRESS", StringComparison.Ordinal) && !mixed.Events[0].NoActionEntities.Contains("PERSON", StringComparison.Ordinal),
                "the no-action fix names only the entities without their own anonymizer");
            var cut = await ProtectOutcomeAsync(Configured(empty), "LI WU");
            Check(cut.ProtectedCandidateValues![0] == "<PERS" && cut.Events[0].Reasons.HasFlag(ReviewReason.Cut), "a replace result longer than the value is cut and listed as cut");
        }
        finally
        {
            File.Delete(empty);
            File.Delete(noCards);
            File.Delete(withDefault);
            File.Delete(keyed);
            protector.Configure();
        }

        // Overlaps, keep and hash with custom recognizers (made-up codes): CODE_A covers AB1234 at 0.9 and CODE_B covers
        // 1234CD at 0.7, so CODE_A wins the shared 1234 and CODE_B keeps CD. chars_to_mask counts from CODE_B's own start,
        // so masking its first two characters changes only what CODE_A already took. spaCy is switched off, because it reads
        // a made-up code such as AB1234CD as a PERSON and would win the whole string.
        string Codes(string actionA, string actionB) => Temporary("<recognizers feed=\"PNR-Linking\">"
            + "<recognizer name=\"CryptoRecognizer\" type=\"predefined\" enabled=\"true\" />"
            + "<recognizer name=\"SpacyRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<recognizer name=\"codeA\" type=\"custom\" supported_entity=\"CODE_A\"><pattern name=\"a\" score=\"0.9\">AB\\d{4}</pattern></recognizer>"
            + "<recognizer name=\"codeB\" type=\"custom\" supported_entity=\"CODE_B\"><pattern name=\"b\" score=\"0.7\">\\d{4}CD</pattern></recognizer>"
            + "<anonymizer entity=\"CODE_A\" " + actionA + " /><anonymizer entity=\"CODE_B\" " + actionB + " /><anonymizer entity=\"DEFAULT\" type=\"mask\" /></recognizers>");
        string overlap = Codes("type=\"mask\"", "type=\"mask\" masking_char=\"#\"");
        string partial = Codes("type=\"mask\"", "type=\"mask\" masking_char=\"#\" chars_to_mask=\"2\"");
        string keepFile = Codes("type=\"keep\"", "type=\"mask\"");
        string hashed = Codes("type=\"hash\" salt=\"demo\"", "type=\"mask\"");
        string typo = Temporary("<recognizers feed=\"PNR-Linking\"><recognizer name=\"CreditCardRecogniser\" type=\"predefined\" /></recognizers>");
        // Key-path context: a weak pattern (0.3) is raised over the threshold by Presidio's context enhancer only when a
        // property name on the string's key path holds its context word, as Presidio's analyze_dict passes a key.
        string context = Temporary("<recognizers feed=\"PNR-Linking\"><recognizer name=\"SpacyRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<recognizer name=\"memberCode\" type=\"custom\" supported_entity=\"MEMBER_CODE\"><pattern name=\"code\" score=\"0.3\">ZX\\d{4}</pattern>"
            + "<context>membership</context></recognizer><anonymizer entity=\"MEMBER_CODE\" type=\"mask\" /></recognizers>");
        try
        {
            var both = await ProtectOutcomeAsync(Configured(overlap), "AB1234CD");
            Check(both.ProtectedCandidateValues![0] == "******##" && both.Events[0].Reasons.HasFlag(ReviewReason.Overlap),
                $"the higher score wins an overlap; the loser masks its own characters (got {both.ProtectedCandidateValues[0]}, {both.Events[0].Recognizer}, {both.Events[0].Entity}, {both.Events[0].Reasons})");
            var counted = await ProtectOutcomeAsync(Configured(partial), "AB1234CD");
            Check(counted.ProtectedCandidateValues![0] == "******CD", "chars_to_mask counts from the losing span's own start");
            var keep = await ProtectOutcomeAsync(Configured(keepFile), "AB1234");
            Check(keep.ProtectedCandidateValues![0] == "AB1234" && keep.Events[0].Decision == DetectionDecision.Kept && keep.Events[0].Reasons == ReviewReason.None,
                "the keep operator publishes a detected value unchanged as kept, not for review");
            var hash = await ProtectOutcomeAsync(Configured(hashed), "AB1234");
            Check(hash.ProtectedCandidateValues![0].Length == 6 && hash.ProtectedCandidateValues[0] != "AB1234" && !hash.Events[0].Reasons.HasFlag(ReviewReason.Cut),
                "a hash cut to width is not listed as cut");
            try
            {
                Configured(typo);
                throw new Exception("an unknown predefined recognizer name must stop startup");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("CreditCardRecogniser", StringComparison.Ordinal))
            {
            }
            var keyContext = ParseJson("[{\"MembershipCode\":\"ZX1234\",\"Remarks\":\"ZX1234\"}]");
            var keyOutcome = await Configured(context).ProtectAsync(FeedType.PnrLinking, keyContext, CancellationToken.None);
            Check(keyOutcome.ProtectedCandidateValues![0] == "******" && keyOutcome.Events[0].Score >= 0.5,
                "a property name holding a recognizer's context word raises a weak match over the threshold");
            Check(keyOutcome.ProtectedCandidateValues[1] == "ZX1234" && keyOutcome.Events[1].Decision == DetectionDecision.BelowThreshold,
                "the same value under a key without the context word stays below the threshold and is listed for review");
            Console.WriteLine("PASS overlaps, chars_to_mask, keep, hash and key-path context behave per span; an unknown predefined name stops startup and is named");
        }
        finally
        {
            foreach (string file in new[] { overlap, partial, keepFile, hashed, typo, context })
            {
                File.Delete(file);
            }
            protector.Configure();
        }

        // WR-020: a rejection after detection keeps its events and values, with the reason unchanged. A codec whose
        // rebuild check fails stands in for a rebuild that differs outside the protected strings (WR-016).
        var failingRebuild = FeedWorkflowRouter.Create(protector, _ => new RebuildRejectingCodec(new PnrLinkingParser()));
        var afterDetection = await new FeedMessageProcessor(failingRebuild)
            .ProcessAsync(FeedType.PnrLinking, Guid.NewGuid(), "PNR_LINKING.txt", File.ReadAllBytes(linkingSample), CancellationToken.None);
        Check(!afterDetection.Published && afterDetection.Reason == RebuildRejectingCodec.Reason
            && afterDetection.DetectionEvents is { Count: > 0 } kept && afterDetection.DetectionValues?.Count == kept.Count,
            "a message dead-lettered after detection keeps its events and values, with the same reason");
        Console.WriteLine("PASS a message dead-lettered after detection keeps its events and values for the detection log (WR-020)");

        // WR-020: a message rejected before detection has no events to log.
        var reviewed = new FeedMessageProcessor(FeedPipelineConfiguration.Load(recognizers).CreateRouter(protector));
        var before = await reviewed.ProcessAsync(FeedType.PnrLinking, Guid.NewGuid(), "truncated.bin", Encoding.UTF8.GetBytes("[{\"FirstName\":\"unfin"), CancellationToken.None);
        Check(!before.Published && before.DetectionEvents is null && before.DetectionValues is null, "truncated.bin: rejected before detection, so nothing to log");
        Console.WriteLine("PASS messages rejected before detection carry no events or values");

        // R-003: a recognizer pattern that times out dead-letters instead of publishing unchecked text. This reconfigures
        // the PNR-Linking registry, so it restores the shipped configuration afterwards.
        string slow = Temporary(File.ReadAllText(Path.Combine(recognizers, "pnr-linking.xml")).Replace("</recognizers>",
            "<recognizer name=\"slowCheck\" type=\"custom\" supported_entity=\"DEMO_CODE\"><pattern name=\"slow\" score=\"0.85\">(A|AA)+$</pattern></recognizer></recognizers>",
            StringComparison.Ordinal));
        try
        {
            var slowProtector = Configured(slow);
            var slowInput = ParseJson("[{\"ChangeReason\":\"" + new string('A', 40) + "!\"}]");
            await RejectAsync(() => slowProtector.ProtectAsync(FeedType.PnrLinking, slowInput, CancellationToken.None),
                "Local Presidio detection failed", "a pattern timeout dead-letters the message");
        }
        finally
        {
            File.Delete(slow);
            protector.Configure();
        }

        // WR-019: narrowing a recognizer file breaks parity with the frozen baseline before merge. Without the DOB mapping a
        // date of birth looks like an allowed timestamp and is published. This reconfigures the PNR-Linking registry, so it
        // runs last and restores the shipped configuration afterwards.
        string narrowed = Temporary(File.ReadAllText(Path.Combine(recognizers, "pnr-linking.xml"))
            .Replace("<key name=\"DOB\" entity=\"DATE_OF_BIRTH\" />", "", StringComparison.Ordinal));
        try
        {
            int reviewedMisses = await ParityMissesAsync(protector, projectRoot);
            Check(reviewedMisses == 0, "the shipped recognizers must have no unapproved PNR-Linking parity miss");
            int narrowedMisses = await ParityMissesAsync(Configured(narrowed), projectRoot);
            Check(narrowedMisses > 0, "a narrowed recognizer file must fail parity");
            Console.WriteLine($"PASS a recognizer file without the DOB mapping fails parity with the frozen baseline ({narrowedMisses} unapproved misses)");
        }
        finally
        {
            File.Delete(narrowed);
            protector.Configure();
        }
    }

    /// <summary>
    /// Protects made-up strings as one JSON message and returns the published values in order.
    /// </summary>
    private static async Task<string[]> ProtectStringsAsync(DetectingFeedProtector protector, params string[] values) =>
        [.. (await ProtectOutcomeAsync(protector, values)).ProtectedCandidateValues!];

    /// <summary>
    /// Protects made-up strings as one JSON message, as the items of a <c>Remarks</c> array, a key the shipped file neither
    /// maps nor skips; the events and candidate values are in the same order.
    /// </summary>
    private static ValueTask<DetectionOutcome> ProtectOutcomeAsync(DetectingFeedProtector protector, params string[] values) =>
        protector.ProtectAsync(FeedType.PnrLinking, ParseJson("[{\"Remarks\":" + JsonSerializer.Serialize(values) + "}]"), CancellationToken.None);

    /// <summary>
    /// Counts baseline values that the protector publishes unchanged and that are not approved exceptions (WR-019).
    /// Value-free: compares the strings at the frozen JSON Pointers only.
    /// </summary>
    /// <param name="protector">The protector to measure.</param>
    /// <param name="projectRoot">The repository root.</param>
    /// <returns>The number of unapproved misses.</returns>
    private static async Task<int> ParityMissesAsync(DetectingFeedProtector protector, string projectRoot)
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-baseline.json")));
        using var approved = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-exceptions.json")));
        var exceptions = approved.RootElement.EnumerateArray().Select(e => (e.GetProperty("sample").GetString(), e.GetProperty("location").GetString())).ToHashSet();
        string folder = FeedCatalog.Get(FeedType.PnrLinking).FolderName;
        int misses = 0;
        foreach (var group in baseline.RootElement.GetProperty("protected").EnumerateArray().Where(e => e.GetProperty("feed").GetString() == folder)
            .GroupBy(e => e.GetProperty("sample").GetString()!))
        {
            string path = Path.Combine(projectRoot, "docs", "sample-docs", folder, group.Key);
            var codec = Codec(FeedType.PnrLinking);
            var message = Message(path, FeedType.PnrLinking);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(FeedType.PnrLinking, parsed, CancellationToken.None);
            var after = codec.Parse(new FeedMessage(message.Delivery, codec.Serialize(parsed, outcome.Values))).Fields.ToDictionary(f => f.Id, f => f.Value);
            foreach (var entry in group)
            {
                string location = entry.GetProperty("location").GetString()!;
                string original = parsed.Fields.Single(f => f.Id == location).Value;
                // Only characters that were not already * need to change; a value that is all * was masked before it arrived.
                bool caught = after[location].Length == original.Length
                    ? original.Zip(after[location]).Where(pair => pair.First != '*').All(pair => pair.First != pair.Second)
                    : after[location] != original;
                if (!caught && !exceptions.Contains((group.Key, location)))
                {
                    misses++;
                }
            }
        }
        return misses;
    }

    /// <summary>
    /// Returns the codec the decoded-text path uses for a feed.
    /// </summary>
    private static IFeedCodec Codec(FeedType feed) => FeedCodecRegistry.Create(feed);

    /// <summary>
    /// Builds a message from a sample file.
    /// </summary>
    private static FeedMessage Message(string path, FeedType feed) => new(new FeedDelivery(feed, Guid.NewGuid(), Path.GetFileName(path), path), File.ReadAllBytes(path));

    /// <summary>
    /// Parses a sample with the decoded-text codec of its feed.
    /// </summary>
    private static ParsedFeed Parse(string path)
    {
        var feed = FeedFileRouting.FromPath(path);
        return Codec(feed).Parse(Message(path, feed));
    }

    /// <summary>
    /// Parses a made-up PNR-Linking JSON payload, sent as a raw payload.
    /// </summary>
    private static ParsedFeed ParseJson(string json) =>
        Codec(FeedType.PnrLinking).Parse(new FeedMessage(new FeedDelivery(FeedType.PnrLinking, Guid.NewGuid(), "fixture.bin", ""), Encoding.UTF8.GetBytes(json)));

    /// <summary>
    /// Requires an InvalidDataException whose message contains the expected text.
    /// </summary>
    private static async Task RejectAsync(Func<ValueTask<DetectionOutcome>> action, string expected, string label)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        throw new Exception("Expected rejection: " + label);
    }

    /// <summary>
    /// Requires a condition.
    /// </summary>
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
    }

    #endregion
}

/// <summary>
/// Wraps a real codec but fails its rebuild check, so a message is rejected after detection ran.
/// </summary>
/// <param name="inner">The codec that parses and rebuilds the message.</param>
internal sealed class RebuildRejectingCodec(IFeedCodec inner) : IFeedCodec
{

    #region Fields and Constants

    /// <summary>
    /// The value-free reason the rebuild check gives.
    /// </summary>
    public const string Reason = "Fixture rebuild check failed.";

    #endregion

    #region Methods

    /// <summary>
    /// Parses the message with the wrapped codec.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The wrapped codec's parse.</returns>
    public ParsedFeed Parse(FeedMessage message) => inner.Parse(message);

    /// <summary>
    /// Rebuilds the message with the wrapped codec.
    /// </summary>
    /// <param name="original">The original parse.</param>
    /// <param name="anonymizedFields">The protected values by field identifier.</param>
    /// <returns>The rebuilt message.</returns>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) => inner.Serialize(original, anonymizedFields);

    /// <summary>
    /// Always rejects the rebuilt message.
    /// </summary>
    /// <param name="original">The original parse.</param>
    /// <param name="serialized">The rebuilt message.</param>
    /// <exception cref="InvalidDataException">Always.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) => throw new InvalidDataException(Reason);

    #endregion
}
