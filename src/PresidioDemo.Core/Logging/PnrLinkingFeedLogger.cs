using PresidioDemo.Core.Models;

namespace PresidioDemo.Core.Logging;

/// <summary>
/// Formats transaction logs for the PNR-Linking feed.
/// </summary>
public sealed class PnrLinkingFeedLogger : FeedLogger
{

    #region Properties, Indexers

    /// <inheritdoc />
    public override FeedType Feed => FeedType.PnrLinking;

    #endregion
}
