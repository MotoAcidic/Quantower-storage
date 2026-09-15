using System;

using OrbIx.Core.Config;
using OrbIx.Core.Features;

namespace OrbIx.Core.Flow;

/// <summary>
/// How a level feature is drawn: which configured colour, and how thick.
///
/// THIS IS IN CORE BECAUSE IT IS A DECISION, and the overlay that would otherwise hold it cannot be
/// tested. The chart project targets net10.0-windows and this suite runs on Linux, so anything the
/// overlay decides is a rule nothing here can exercise — and a guard in an untestable layer is
/// hollow. The overlay converts sRGB bytes to a GDI+ colour and nothing else; which bytes belong to
/// which feature and side is settled here, where a test can call it.
///
/// The mapping looks obvious enough to be left in the renderer, which is exactly why it is worth
/// pinning: a bullish absorption line drawn in the bearish colour is wrong in a way that renders
/// perfectly and reads as a real market event.
/// </summary>
/// <param name="Bullish">The colour for a level that argues up.</param>
/// <param name="Bearish">The colour for a level that argues down.</param>
/// <param name="Width">Line width in pixels.</param>
/// <param name="Alpha">
/// Line opacity, 0 transparent to 255 opaque. Defaults to fully opaque, which is what every
/// level family drew before any of them had a reason not to.
/// </param>
public readonly record struct FlowLevelStyle(Rgb Bullish, Rgb Bearish, int Width, int Alpha = 255)
{
    /// <summary>The colour for one side of a level.</summary>
    public Rgb For(LevelSide side) => side == LevelSide.Bullish ? this.Bullish : this.Bearish;

    /// <summary>
    /// The style for a feature, from the document.
    ///
    /// THE CLUSTER SEARCH HAS ONE COLOUR FOR BOTH SIDES, and that is the document's shape rather
    /// than an oversight: a hit is a hit, and its side is already said by where the marker sits
    /// against the bar. Returning the same colour twice is how that is expressed without the
    /// renderer needing to know it is a special case.
    /// </summary>
    public static FlowLevelStyle For(FlowConfig config, LevelFeature feature)
    {
        ArgumentNullException.ThrowIfNull(config);

        return feature switch
        {
            LevelFeature.StackedImbalance => new FlowLevelStyle(
                config.StackedImbalance.BullishColour,
                config.StackedImbalance.BearishColour,
                config.StackedImbalance.LineWidth),

            LevelFeature.Absorption => new FlowLevelStyle(
                config.Absorption.BullishColour,
                config.Absorption.BearishColour,
                config.Absorption.LineWidth),

            // LOW IS THE BULLISH SIDE HERE. An unfinished auction at a bar's LOW is bids left
            // unfilled beneath it, which argues up — so the low line takes the bullish slot, and
            // the high line the bearish one. Reading the names the other way round would colour
            // every auction level backwards.
            LevelFeature.UnfinishedAuction => new FlowLevelStyle(
                config.UnfinishedAuction.LowColour,
                config.UnfinishedAuction.HighColour,
                config.UnfinishedAuction.LineWidth),

            LevelFeature.ClusterSearch => new FlowLevelStyle(
                config.ClusterSearch.Colour, config.ClusterSearch.Colour, Width: 1),

            // FOUR COLOURS, NOT TWO, because tier and side are independent facts and a reader
            // has to tell them apart at a glance. The moderate tier is the dimmer pair and the
            // heavy tier the brighter, so strength reads as brightness without needing a label.
            LevelFeature.VolumeAbsorptionModerate => new FlowLevelStyle(
                config.VolumeAbsorption.Tier1BullishColour,
                config.VolumeAbsorption.Tier1BearishColour,
                config.VolumeAbsorption.LineWidth,
                config.VolumeAbsorption.Opacity),

            LevelFeature.VolumeAbsorptionHeavy => new FlowLevelStyle(
                config.VolumeAbsorption.Tier2BullishColour,
                config.VolumeAbsorption.Tier2BearishColour,
                config.VolumeAbsorption.LineWidth,
                config.VolumeAbsorption.Opacity),

            _ => throw new ArgumentOutOfRangeException(
                nameof(feature), feature, "No level style is defined for this feature."),
        };
    }
}
