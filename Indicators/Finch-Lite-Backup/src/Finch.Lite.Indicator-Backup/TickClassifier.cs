using TradingPlatform.BusinessLayer;

namespace FinchLiteBackup;

/// <summary>
/// EXTRACTED 2026-09-29, while building `oceansStackStrategy` — this same classifier existed as
/// two independent, byte-identical copies before this (one in `FinchLiteBackupIndicator.cs`, one pasted
/// directly into `finchDomScalpStrategy.cs`). Rather than pasting a third copy for the new
/// strategy's own absorption/delta tracking, this pulls it into one shared, source-included file —
/// same `<Compile Include>` reasoning as `RestingOrderEngine.cs`/`FairValueGapEngine.cs`/
/// `PocEngine.cs` already use (Quantower's `Assembly.Load(bytes)` caches by assembly FullName, so a
/// shared REFERENCED dll would collision-cache between independent scripts; compiling the same
/// SOURCE into each consumer avoids that while still keeping one authored copy of the logic).
///
/// The two pre-existing copies in `FinchLiteBackupIndicator.cs` and `finchDomScalpStrategy.cs` are left
/// as-is for now (switching them over touches those files' own build graphs, out of scope for the
/// task that motivated this extraction) — this file is simply the version any NEW consumer should
/// compile in from here on.
/// </summary>
internal static class TickClassifier
{
    /// <summary>
    /// Classifies a print's aggressor side — trusting only <see cref="Last.AggressorFlag"/> and
    /// dropping everything else left prints unclassified far more often than expected on some feeds
    /// (found 2026-09-22 on MGC/Rithmic — most prints on that feed apparently don't come flagged
    /// Buy/Sell at all). This codebase already found and named this exact class of problem: ORB-IX's
    /// own `AggressorConvention` (`OrbIx.Core/Features/AggressorConvention.cs`) measured that a
    /// vendor's aggressor flag can be missing, sparse, or even INVERTED, and the fix is to classify a
    /// print from its own GEOMETRY against the quote rather than trust a flag that might not be
    /// there — a print at or above the ask was taken by a buyer lifting the offer, a print at or
    /// below the bid was hit into a resting bid, inclusive on both touches (the overwhelmingly common
    /// case; a strict cross would discard nearly every print). The feed's own flag is still trusted
    /// FIRST when it says Buy or Sell outright; this fallback only fires when it says neither. A
    /// print strictly inside the spread, or a quote that is missing/locked/crossed, still carries no
    /// evidence and is still dropped, not guessed.
    /// </summary>
    public static bool TryClassify(Symbol symbol, Last last, out bool isBuy)
    {
        if (last.AggressorFlag == AggressorFlag.Buy) { isBuy = true; return true; }
        if (last.AggressorFlag == AggressorFlag.Sell) { isBuy = false; return true; }

        isBuy = false;

        var bid = symbol.Bid;
        var ask = symbol.Ask;

        if (!double.IsFinite(bid) || !double.IsFinite(ask) || bid <= 0 || ask <= 0 || bid >= ask)
            return false;

        var takenByBuyer = last.Price >= ask;
        var takenBySeller = last.Price <= bid;

        if (takenByBuyer == takenBySeller)
            return false; // strictly inside the spread (or the touch matched neither) — no evidence

        isBuy = takenByBuyer;
        return true;
    }
}
