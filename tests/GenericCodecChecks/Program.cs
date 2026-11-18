using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;

#region Execution

// Shared System.Text.Json codec checks (V-03). Output is value-free: counts and lengths only.
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
// Scalar and string counts of the supplied samples, measured with an independent JSON reader; the frozen value-free
// parity baseline locates the values the former field policies protected and the PNRs they kept.
var expected = new Dictionary<string, (int Scalars, int Strings)>
{
    ["PNR_LINKING.txt"] = (76, 56), ["PNR_LINKING_02_Negative_Testing.txt"] = (77, 57), ["PNR_LINKING_03_Negative_Testing.txt"] = (76, 55)
};
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "tests", "fixtures", "parity-baseline.json")));
var locations = baseline.RootElement.GetProperty("protected").EnumerateArray().Concat(baseline.RootElement.GetProperty("pnrs").EnumerateArray())
    .Where(e => e.GetProperty("feed").GetString() == "PNR-Linking")
    .Select(e => (Sample: e.GetProperty("sample").GetString()!, Pointer: e.GetProperty("location").GetString()!, Length: e.GetProperty("length").GetInt32()))
    .ToArray();
var samples = Directory.EnumerateFiles(Path.Combine(project, "docs", "sample-docs"), "*.txt", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal)
    .Select(path => (Path: path, Feed: FeedFileRouting.FromPath(path)))
    .ToArray();
Check(samples.Length == 3, "3 PNR-Linking samples expected, found " + samples.Length);
Check(locations.Length == 17, "17 baseline locations expected, found " + locations.Length);

int compared = 0;
foreach (var (path, feed) in samples)
{
    string name = Path.GetFileName(path);
    byte[] original = File.ReadAllBytes(path);
    var message = new FeedMessage(new FeedDelivery(feed, Guid.NewGuid(), name, path), original);
    var codec = FeedCodecRegistry.Create(feed);
    Check(codec is PnrLinkingParser, name + ": the registry selects the PNR-Linking JSON parser");
    var parsed = codec.Parse(message);
    var strings = JsonFeedParser.StringFields(parsed);
    var envelope = FeedPayloadReader.Read(message);

    // An independent reader (the JSON DOM) agrees on every scalar, its pointer, key path and value.
    var independent = Independent(envelope.Payload);
    Check(parsed.Fields.Count == independent.Count && parsed.Fields.Zip(independent).All(pair => pair.Second.Matches(pair.First)),
        name + ": the independent JSON reader agrees on every scalar, pointer, key path and value");
    Check(parsed.Fields.Count == expected[name].Scalars && strings.Count == expected[name].Strings,
        $"{name}: {parsed.Fields.Count} scalars and {strings.Count} strings, expected {expected[name].Scalars} and {expected[name].Strings}");
    Check(independent.Count(field => field.Kind == JsonValueKind.String) == strings.Count, name + ": every JSON string scalar is a detection candidate");

    // A-002: every baseline value and PNR is one whole string candidate of the baseline length.
    foreach (var location in locations.Where(l => l.Sample == name))
    {
        Check(strings.SingleOrDefault(f => f.Id == location.Pointer) is { } field && field.Value.Length == location.Length,
            $"{name}: a baseline location is not a string candidate of its length");
    }
    compared += strings.Count;

    // Unchanged values rebuild the original bytes exactly, for the MQ export and for the raw payload.
    var unchanged = parsed.Fields.ToDictionary(f => f.Id, f => f.Value);
    byte[] same = codec.Serialize(parsed, unchanged);
    Check(same.AsSpan().SequenceEqual(original), name + ": unmasked MQ export round trip changed bytes");
    codec.Validate(parsed, same);
    var rawMessage = message with { Delivery = message.Delivery with { OriginalName = "payload.bin" }, Content = envelope.Payload };
    var rawParsed = codec.Parse(rawMessage);
    Check(codec.Serialize(rawParsed, rawParsed.Fields.ToDictionary(f => f.Id, f => f.Value)).AsSpan().SequenceEqual(envelope.Payload),
        name + ": unmasked raw JSON round trip changed bytes");

    // Full-width masks of every string keep the structure; ASCII strings keep the payload length, and only string
    // characters change.
    var stringIds = strings.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
    var masked = parsed.Fields.ToDictionary(f => f.Id, f => stringIds.Contains(f.Id) ? new string('*', f.Value.Length) : f.Value);
    byte[] protectedContent = codec.Serialize(parsed, masked);
    codec.Validate(parsed, protectedContent);
    byte[] protectedPayload = FeedPayloadReader.Read(message with { Content = protectedContent }).Payload;
    Check(protectedPayload.Length == envelope.Payload.Length, name + ": masked ASCII strings changed the payload length");
    var reparsed = codec.Parse(message with { Content = protectedContent });
    Check(reparsed.Fields.All(f => f.Value == masked[f.Id]), name + ": every masked string reads back and every other scalar is unchanged");

    // A changed number is refused, and so is a changed byte outside the strings.
    var number = parsed.Fields.First(f => !stringIds.Contains(f.Id) && f.Value.Length > 0);
    Reject(() => codec.Serialize(parsed, unchanged.ToDictionary(p => p.Key, p => p.Key == number.Id ? p.Value + "0" : p.Value)), name + ": changed number");
    byte[] tamperedPayload = envelope.Payload.ToArray();
    int colon = Array.IndexOf(tamperedPayload, (byte)':');
    tamperedPayload[colon - 2] ^= 0x20;
    Reject(() => codec.Validate(parsed, envelope.Serialize(tamperedPayload)), name + ": changed property name");
    tamperedPayload = envelope.Payload.ToArray();
    int comma = Array.IndexOf(tamperedPayload, (byte)',');
    Reject(() => codec.Validate(parsed, envelope.Serialize([.. tamperedPayload[..comma], (byte)' ', .. tamperedPayload[comma..]])), name + ": added whitespace");

    Console.WriteLine($"PASS {name,-36} {parsed.Fields.Count,3} scalars {strings.Count,3} strings | baseline locations are whole string candidates");
}

// A changed string keeps the original bytes of every unchanged character, escapes and raw UTF-8 included; only the
// changed characters are written, so the bytes outside a masked span stay exact (fictional values).
var linking = FeedCodecRegistry.Create(FeedType.PnrLinking);
string escapes = "[{\"ChangeReason\":\"Jos\\u00e9 MILLER\\/JORDAN \\ud83d\\ude00 Ren\u00e9 \u2603\",\"Other\":\"x\\ty\"}]";
var escaped = linking.Parse(Message(escapes));
string reason = escaped.Fields[0].Value;
Check(reason == "Jos\u00e9 MILLER/JORDAN \U0001F600 Ren\u00e9 \u2603", "escapes and raw UTF-8 decode to the string value");
var nameMasked = Values(escaped, reason.Replace("MILLER", "******", StringComparison.Ordinal));
Check(Encoding.UTF8.GetString(linking.Serialize(escaped, nameMasked)) == escapes.Replace("MILLER", "******", StringComparison.Ordinal),
    "a masked span changes only its own characters; \\u00e9, \\/, a surrogate-pair escape and raw UTF-8 keep their spelling");
var emojiMasked = Values(escaped, reason.Replace("\U0001F600", "**", StringComparison.Ordinal).Replace("Ren\u00e9", "****", StringComparison.Ordinal));
Check(Encoding.UTF8.GetString(linking.Serialize(escaped, emojiMasked))
    == escapes.Replace("\\ud83d\\ude00", "**", StringComparison.Ordinal).Replace("Ren\u00e9", "****", StringComparison.Ordinal),
    "a masked surrogate pair and a masked raw UTF-8 character are written as the mask characters");
var quoted = Values(escaped, "\"\\" + reason[2..]);
string quotedText = Encoding.UTF8.GetString(linking.Serialize(escaped, quoted));
Check(quotedText.StartsWith("[{\"ChangeReason\":\"\\\"\\\\s\\u00e9 MILLER", StringComparison.Ordinal)
    && JsonDocument.Parse(quotedText).RootElement[0].GetProperty("ChangeReason").GetString() == quoted[escaped.Fields[0].Id],
    "a changed quote or backslash is escaped, and the rebuilt string reads back exactly");
var longer = Values(escaped, reason + "\n<grown>");
string longerText = Encoding.UTF8.GetString(linking.Serialize(escaped, longer));
Check(longerText.Contains("\\u000a<grown>", StringComparison.Ordinal) && longerText.EndsWith(",\"Other\":\"x\\ty\"}]", StringComparison.Ordinal)
    && JsonDocument.Parse(longerText).RootElement[0].GetProperty("ChangeReason").GetString() == reason + "\n<grown>",
    "a value of another width is encoded whole, with control characters escaped, and the bytes after it stay exact");

// Lexical forms: numbers keep their spelling, nulls stay null, pointers escape / and ~ (from the former JSON feed checks).
var lexical = linking.Parse(Message(" [{\"PNROwner\":\"K7QX2M\", \"Customers\":[{\"PaxId\":\"PAX-1001\",\"FirstName\":\"Em\\u0069ly\"},{\"PaxId\":\"PAX-1002\",\"FirstName\":\"Michael\"}],\"x/y~z\":\"Carter\",\"Count\":1e+02,\"Active\":true,\"Optional\":null,\"Empty\":[],\"Object\":{}}] \n"));
var values = lexical.Fields.ToDictionary(field => field.Id, field => field.Value);
Check(values["/0/x~1y~0z"] == "Carter" && lexical.Fields.Single(f => f.Id == "/0/x~1y~0z").Name == "/*/x~1y~0z", "JSON Pointer escapes keep exact key paths and instance paths");
Check(lexical.Fields.Single(f => f.Id == "/0/Customers/1/FirstName").Name == "/*/Customers/*/FirstName", "array indexes become * in the key path");
values["/0/Customers/0/FirstName"] = "[PASSENGER_NAME_WITH_LONGER_TEXT]";
byte[] changed = linking.Serialize(lexical, values);
linking.Validate(lexical, changed);
Check(Encoding.UTF8.GetString(changed).Contains("1e+02", StringComparison.Ordinal) && Encoding.UTF8.GetString(changed).Contains("\"Optional\":null", StringComparison.Ordinal),
    "string growth preserves numeric spelling and null types");
Reject(() => linking.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("1e+02", "100", StringComparison.Ordinal))), "external numeric spelling mutation");
Reject(() => linking.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("\"Empty\":[]", "\"Empty\":[{}]", StringComparison.Ordinal))),
    "external empty-container grouping mutation");
values["/0/Count"] = "100";
Reject(() => linking.Serialize(lexical, values), "non-string replacement");
values["/0/Count"] = "1e+02";
values.Remove("/0/Customers/0/FirstName");
Reject(() => linking.Serialize(lexical, values), "missing protection field");
values["unknown"] = "extra";
Reject(() => linking.Serialize(lexical, values), "substituted field ID with matching count");
values.Remove("unknown");
values["/0/Customers/0/FirstName"] = null!;
Reject(() => linking.Serialize(lexical, values), "null replacement is not a JSON-null conversion");
Reject(() => linking.Parse(Message("[{\"x\":\"a\",\"x\":\"b\"}]")), "duplicate property");
Reject(() => linking.Parse(Message("[{\"x\":\"a\",\"\\u0078\":\"b\"}]")), "escaped duplicate property");
Reject(() => linking.Parse(Message("[{\"*\":\"ambiguous\"}]")), "wildcard property name");
Reject(() => linking.Parse(Message("[{\"FirstName\":\"unfinished")), "truncated JSON");
Reject(() => linking.Parse(Message("{\"x\":\"y\"}")), "an object root (PNR-Linking requires an array)");
Reject(() => linking.Parse(Message("[{\"x\":\"y\"}] {}")), "trailing JSON");
Reject(() => linking.Parse(Message("[]")), "a payload without scalars");
Reject(() => linking.Parse(new FeedMessage(new FeedDelivery((FeedType)0, Guid.Empty, "fixture.bin", "fixture"), Encoding.UTF8.GetBytes("[{\"x\":\"y\"}]"))),
    "wrong delivery route");
string export = "A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX " + Convert.ToHexString(Encoding.UTF8.GetBytes("[{\"FirstName\":\"Emily\"}]"));
var exported = linking.Parse(Message(export, "fixture.txt"));
byte[] rebuilt = linking.Serialize(exported, exported.Fields.ToDictionary(field => field.Id, field => "*****"));
linking.Validate(exported, rebuilt);
Check(Encoding.UTF8.GetString(rebuilt).StartsWith("A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX ", StringComparison.Ordinal) && rebuilt[^1] != '\n',
    "MQ CRLF and absent final newline retained during protection");
Reject(() => linking.Validate(exported, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(rebuilt).Replace("A VER 2", "A VER 3", StringComparison.Ordinal))), "MQ header mutation");
Reject(() => linking.Parse(Message(export.Replace("1208", "437", StringComparison.Ordinal), "fixture.txt")), "unsupported JSON CCSID");

Console.WriteLine($"PASS Shared JSON codec checks complete | {samples.Length} samples | {compared} string candidates");

static FeedMessage Message(string json, string name = "fixture.bin") =>
    new(new FeedDelivery(FeedType.PnrLinking, Guid.Empty, name, "fixture"), Encoding.UTF8.GetBytes(json));

// The values of a parse with its first field set to a new value.
static Dictionary<string, string> Values(ParsedFeed parsed, string first) =>
    parsed.Fields.Select((field, i) => (field.Id, Value: i == 0 ? first : field.Value)).ToDictionary(pair => pair.Id, pair => pair.Value);

// This reader uses the independent DOM API and has no dependency on production JSON span code.
static IReadOnlyList<IndependentField> Independent(byte[] payload)
{
    using (var document = JsonDocument.Parse(payload))
    {
        var fields = new List<IndependentField>();
        Walk(document.RootElement, "", "");
        return fields;

        void Walk(JsonElement element, string id, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    string name = property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                    Walk(property.Value, id + "/" + name, path + "/" + name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, id + "/" + index++, path + "/*");
                }
            }
            else
            {
                string value = element.ValueKind == JsonValueKind.String ? element.GetString()! :
                    element.ValueKind == JsonValueKind.Null ? "" : element.GetRawText();
                fields.Add(new(id, path, value, element.ValueKind));
            }
        }
    }
}

static void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new Exception(label);
    }
}

static void Reject(Action action, string label)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new Exception("Expected rejection: " + label);
}

#endregion

#region Models

/// <summary>
/// One scalar as the independent JSON reader sees it.
/// </summary>
/// <param name="Id">The JSON Pointer.</param>
/// <param name="Name">The key path, with array indexes as <c>*</c>.</param>
/// <param name="Value">The decoded value: a string's text, a number's or boolean's JSON text, or empty for null.</param>
/// <param name="Kind">The JSON value kind.</param>
internal sealed record IndependentField(string Id, string Name, string Value, JsonValueKind Kind)
{

    #region Methods

    /// <summary>
    /// Returns whether a parsed field has this pointer, key path and value.
    /// </summary>
    /// <param name="field">The parsed field.</param>
    /// <returns>True when they agree.</returns>
    public bool Matches(FeedField field) => field.Id == Id && field.Name == Name && field.Value == Value;

    #endregion
}

#endregion
