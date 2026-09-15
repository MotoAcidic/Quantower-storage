using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Flow;

/// <summary>
/// The forming bar's live contract counts — the "flickering number" in the corner of the pane.
/// </summary>
/// <param name="BarOpenUtc">The bar being counted. MinValue when none is forming.</param>
/// <param name="Buy">Contracts the buyer aggressed for.</param>
/// <param name="Sell">Contracts the seller aggressed for.</param>
/// <param name="Delta">Buy less sell. Unclassified prints contribute to neither.</param>
/// <param name="Volume">Every contract, classified or not.</param>
/// <param name="Trades">Prints, classified or not.</param>
public readonly record struct CounterReading(
    DateTime BarOpenUtc, double Buy, double Sell, double Delta, double Volume, int Trades)
{
    public static readonly CounterReading Empty = new(DateTime.MinValue, 0, 0, 0, 0, 0);

    /// <summary>
    /// Whether a bucket is open to report on. An open bucket nobody has traded in yet reads as
    /// zero and is still a bar — see <see cref="Idle"/>.
    /// </summary>
    public bool HasBar => this.BarOpenUtc != DateTime.MinValue;

    /// <summary>
    /// The counts for a forming bar, or <see cref="Empty"/> when nothing is forming.
    ///
    /// VOLUME AND DELTA ARE NOT THE SAME QUESTION, which is why both are carried. An
    /// unclassified print is real volume and no delta — it happened, and which side wanted it
    /// is unknown. Folding it into either side would invent the answer.
    /// </summary>
    public static CounterReading From(FootprintBar? forming)
        => forming is { } bar && bar.HasPrints
            ? new CounterReading(
                bar.OpenUtc, bar.BuyVolume, bar.SellVolume, bar.Delta, bar.Volume, bar.Trades)
            : Empty;

    /// <summary>
    /// The reading for a bucket that is open and has taken no prints yet: zero of everything, on
    /// that bucket.
    ///
    /// A BUCKET NOBODY HAS TRADED IN IS STILL A BUCKET, AND SAYING SO IS THE WHOLE POINT. The
    /// builder only creates a bar when a print lands in it, so between one bar closing and the
    /// next bar's first print there was no bar to read and the counter — the one mark on the
    /// chart that is supposed to move continuously — drew nothing at all. On a one-minute chart
    /// that gap opens at EVERY bar boundary and lasts until the next trade, which overnight is
    /// seconds. Observed on the operator's chart 2026-09-14, where consecutive frames one second
    /// apart read "counter live" and then "counter none".
    ///
    /// ZERO IS THE HONEST ANSWER, AND CARRYING THE PREVIOUS BAR FORWARD WOULD NOT BE. "Nothing
    /// has traded in this minute yet" is a fact about the tape; repeating the last bar's delta
    /// under the new bar's heading would show a stale number as a live one, which is the failure
    /// this codebase has already hit with a cached panel that outlived its chart.
    ///
    /// NO GUARD ON MINVALUE, BECAUSE ONE WOULD BE DEAD CODE. An idle reading on MinValue builds
    /// exactly <see cref="Empty"/> — same bucket, same zeros — so a branch returning Empty for it
    /// cannot change the value. It was written, and a mutation removing it survived the suite,
    /// which is how it was caught rather than shipped as a guard that guards nothing.
    /// </summary>
    /// <param name="bucketOpenUtc">The open bucket. MinValue is <see cref="Empty"/>.</param>
    public static CounterReading Idle(DateTime bucketOpenUtc)
        => new(bucketOpenUtc, 0, 0, 0, 0, 0);
}

/// <summary>
/// The resting book as the paint needs it.
/// </summary>
/// <param name="LargestBids">The largest bid levels within reach of the touch, largest first.</param>
/// <param name="LargestAsks">The largest ask levels within reach of the touch, largest first.</param>
/// <param name="ProfileBids">Bid levels nearest the touch, for the depth profile at the edge.</param>
/// <param name="ProfileAsks">Ask levels nearest the touch, for the depth profile at the edge.</param>
/// <param name="LargestBidOrder">The largest single resting bid ORDER. Null off a per-order feed.</param>
/// <param name="LargestAskOrder">The largest single resting ask ORDER. Null off a per-order feed.</param>
/// <param name="PerOrder">Whether the feed delivers one message per order rather than per price.</param>
/// <param name="SyntheticLevelOne">
/// Whether the connector FABRICATED this book from level 1 — one price per side. Carried so the
/// overlay can say so instead of drawing two prices as though they were depth.
/// </param>
/// <param name="BidLevels">Bid prices held.</param>
/// <param name="AskLevels">Ask prices held.</param>
/// <param name="Updates">Depth messages applied, so a silent feed is distinguishable from a flat one.</param>
/// <param name="LastUpdateUtc">When the book last moved.</param>
public sealed record DomReading(
    DepthLevel[] LargestBids,
    DepthLevel[] LargestAsks,
    DepthLevel[] ProfileBids,
    DepthLevel[] ProfileAsks,
    RestingOrder? LargestBidOrder,
    RestingOrder? LargestAskOrder,
    bool PerOrder,
    bool SyntheticLevelOne,
    int BidLevels,
    int AskLevels,
    long Updates,
    DateTime LastUpdateUtc)
{
    public static readonly DomReading Empty = new(
        Array.Empty<DepthLevel>(), Array.Empty<DepthLevel>(),
        Array.Empty<DepthLevel>(), Array.Empty<DepthLevel>(),
        null, null, false, false, 0, 0, 0, DateTime.MinValue);

    /// <summary>
    /// What the display must say rather than draw, or null when the book can be read as depth.
    ///
    /// A FABRICATED BOOK IS THE DANGEROUS CASE, not an empty one. A connector with no real depth
    /// publishes one price per side stamped with its own placeholder id; drawn without comment
    /// that reads as "the largest resting size in the book sits at the touch", which is a
    /// statement about a book nobody published.
    /// </summary>
    /// <summary>
    /// What the book actually HOLDS, for the log — not only what survived the display filters.
    ///
    /// WRITTEN BECAUSE "book 0+0" COULD NOT BE DIAGNOSED. The two numbers in that line are the
    /// LARGEST levels that cleared <c>volumeFilter</c> and <c>topPerSide</c>, so "0+0" is true of
    /// a book that never arrived, a book holding forty prices none of which is big enough, and a
    /// filter set too high — three different faults with three different fixes, reading
    /// identically. Observed on the operator's chart 2026-09-14, where one instance said "0+0"
    /// and another "no depth received" and nothing distinguished the causes.
    ///
    /// The counts are ordered by what you would check first: did anything arrive, how much is
    /// held, and only then what the filters left.
    /// </summary>
    public string Describe()
    {
        if (this.SyntheticLevelOne)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"level 1 only — the connector fabricated it, so these are not depth ({this.Updates:N0} update(s))");
        }

        if (this.Updates == 0)
            return "no depth received";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{this.Updates:N0} update(s), {this.BidLevels}+{this.AskLevels} price(s) held, " +
            $"{this.LargestBids.Length}+{this.LargestAsks.Length} past the filters");
    }

    public string? Caveat => this.SyntheticLevelOne
        ? "book is level 1 only — the connector fabricated it, so these are not depth"
        : this.Updates == 0
            ? "no depth received"
            : null;
}

/// <summary>
/// Everything the absorbed Aramid Flow displays draw, assembled in one piece.
///
/// ASSEMBLED IN CORE, WHICH IS THE POINT OF THE TYPE. Aramid Flow built the same thing inside
/// its Quantower shell, where no test could reach it: the settings were checkable, the engines
/// were checkable, and what the two of them together produced was not. Everything here is
/// answerable without a platform, so the suite asserts the assembly rather than the pieces.
///
/// A FRAME IS A VALUE AND IS NEVER MUTATED. The builder produces a new one per fold; a cached
/// frame that outlived its chart is a bug this codebase has already had once, in a panel that
/// kept rendering a verdict for an instrument the chart had stopped showing.
/// </summary>
/// <param name="Columns">One statistics column per bar, oldest first, forming bar last.</param>
/// <param name="StatRows">Which rows to draw, in order.</param>
/// <param name="Spans">Level spans per feature, oldest first.</param>
/// <param name="ProvisionalAbsorption">
/// Absorption levels found on the FORMING bar. Separate from <paramref name="Spans"/> because
/// they are not yet levels: the bar can still change and they can vanish.
/// </param>
/// <param name="Hits">Cluster-search hits within the look-back.</param>
/// <param name="BigTrades">Large prints held for marking.</param>
/// <param name="Counter">The forming bar's live counts.</param>
/// <param name="Dom">The resting book.</param>
/// <param name="ClosedBars">Closed bars held, so an empty chart is distinguishable from an idle one.</param>
/// <param name="AbsorptionRefusals">
/// Why the BOOK reading of absorption said nothing, when it is the active source. Null when
/// the footprint reading is in force, because it has no such states.
///
/// CARRIED SO THAT "absorption 0" IS DIAGNOSABLE. A zero there can mean the book was never
/// known, the touch had no observation before the window opened, or nothing cleared the floor —
/// three different faults with three different fixes, reading identically. This is the same
/// lesson as the book's own line, applied to the tool that reads it.
/// </param>
/// <param name="Bias">
/// Reference geometry over the chart's own bars — fans, trend lines, gamma walls. Built from the
/// chart's bars rather than from the footprint, so the host supplies it and the frame carries it:
/// one frame is everything the paint draws, and a second channel to paint from is a second thing
/// that can go stale on its own.
/// </param>
public sealed record FlowFrame(
    StatColumn[] Columns,
    StatRow[] StatRows,
    IReadOnlyDictionary<LevelFeature, LevelSpan[]> Spans,
    FlowLevel[] ProvisionalAbsorption,
    ClusterHit[] Hits,
    BigTrade[] BigTrades,
    CounterReading Counter,
    DomReading Dom,
    int ClosedBars,
    FlowBias Bias,
    string? AbsorptionRefusals = null)
{
    public static readonly FlowFrame Empty = new(
        Array.Empty<StatColumn>(),
        Array.Empty<StatRow>(),
        new Dictionary<LevelFeature, LevelSpan[]>(),
        Array.Empty<FlowLevel>(),
        Array.Empty<ClusterHit>(),
        Array.Empty<BigTrade>(),
        CounterReading.Empty,
        DomReading.Empty,
        0,
        FlowBias.Empty,
        null);

    /// <summary>The spans for one feature, or an empty list when it drew nothing.</summary>
    public IReadOnlyList<LevelSpan> SpansFor(LevelFeature feature)
        => this.Spans.TryGetValue(feature, out var spans) ? spans : Array.Empty<LevelSpan>();

    /// <summary>
    /// What this frame actually CONTAINS, for the log.
    ///
    /// WRITTEN AFTER A DEPLOY THAT DREW NOTHING AND COULD NOT BE DIAGNOSED. The status line said
    /// "flow: 8 of 11 on", which is a statement about SWITCHES — it is true whether every tool
    /// found a hundred things or none of them found anything, so it could not distinguish a
    /// display that was off, a display that was on and empty, and a display that was broken.
    ///
    /// Every count here is one a reader can act on: a zero next to a tool that is switched on says
    /// where to look, and that is the whole job of this line.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(8);

        foreach (var feature in new[]
        {
            LevelFeature.StackedImbalance, LevelFeature.Absorption,
            LevelFeature.UnfinishedAuction, LevelFeature.ClusterSearch,
        })
        {
            // Absent from the dictionary means the tool is OFF; present and empty means it is on
            // and has found nothing. Those are different states and they read differently here.
            if (this.Spans.TryGetValue(feature, out var spans))
                parts.Add($"{Name(feature)} {spans.Length}");
        }

        if (this.ProvisionalAbsorption.Length > 0)
            parts.Add($"provisional {this.ProvisionalAbsorption.Length}");

        parts.Add($"hits {this.Hits.Length}");
        parts.Add($"big trades {this.BigTrades.Length}");
        parts.Add($"stat columns {this.Columns.Length}");
        parts.Add($"closed bars {this.ClosedBars}");
        parts.Add($"counter {(this.Counter.HasBar ? "live" : "none")}");

        parts.Add($"book {this.Dom.Describe()}");

        if (this.AbsorptionRefusals is { Length: > 0 } refusals)
            parts.Add($"absorption reads {refusals}");

        if (this.Bias.HasGeometry)
            parts.Add($"fans {this.Bias.Fans.Length}, trend lines {this.Bias.TrendLines.Length}, walls {this.Bias.Gex.Length}");

        return "flow frame: " + string.Join(", ", parts);
    }

    private static string Name(LevelFeature feature) => feature switch
    {
        LevelFeature.StackedImbalance => "stacked",
        LevelFeature.Absorption => "absorption",
        LevelFeature.UnfinishedAuction => "auction",
        _ => "search levels",
    };
}
