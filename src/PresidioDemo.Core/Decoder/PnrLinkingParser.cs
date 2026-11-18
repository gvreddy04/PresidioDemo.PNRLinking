using System.Text.Json;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Decoder;

/// <summary>
/// Parses, rebuilds and validates PNR-Linking messages (a JSON array root); the shared logic is in <see cref="JsonFeedParser"/>.
/// </summary>
public sealed class PnrLinkingParser() : JsonFeedParser(FeedType.PnrLinking, JsonTokenType.StartArray)
{
}
