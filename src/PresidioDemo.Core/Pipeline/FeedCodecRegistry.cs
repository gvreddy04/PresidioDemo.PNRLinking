using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Pipeline;

/// <summary>
/// Selects the decoded-text codec for a delivery route: the shared System.Text.Json parser for the JSON feed.
/// </summary>
public static class FeedCodecRegistry
{

    #region Methods

    /// <summary>
    /// Returns the codec for a feed route.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <returns>The codec.</returns>
    public static IFeedCodec Create(FeedType feed) => feed switch
    {
        FeedType.PnrLinking => new PnrLinkingParser(),
        _ => throw new InvalidDataException("No codec is registered for this feed.")
    };

    #endregion
}
