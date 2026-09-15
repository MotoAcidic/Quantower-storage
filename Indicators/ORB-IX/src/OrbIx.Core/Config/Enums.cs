namespace OrbIx.Core.Config;

/// <summary>
/// How far the engine is permitted to act on its own conclusions.
/// </summary>
public enum EngineMode
{
    /// <summary>Draw and journal only. No order is ever constructed.</summary>
    Signal,

    /// <summary>
    /// Build the complete order plan and stage it, but require a human action before
    /// anything is sent. Phase 1 ships here.
    /// </summary>
    Armed,

    /// <summary>
    /// Send without confirmation. Requesting this in configuration is a request, not a
    /// setting: <see cref="Risk.EngineModeGate"/> refuses it for any playbook, session and
    /// product that has not cleared §13's V4-V6, and says which evidence is missing.
    /// </summary>
    Auto,
}

/// <summary>
/// Where order-level (L3) data comes from.
///
/// The specification assumed Quantower cannot supply it and that a side-car process was
/// the only route. Decompilation of the installed platform shows <c>Level2Quote</c>
/// carrying <c>Id</c>, <c>Priority</c>, <c>Closed</c> and <c>NumberOrders</c>, and the
/// one data vendor connector implementing depth-by-order callbacks behind an "Enable MBO mode"
/// connection setting — so a native path may exist. Whether those fields are actually
/// populated is a property of the live feed and is settled by the capability probe, not
/// by reading binaries. <see cref="Auto"/> defers to the probe rather than assuming.
/// </summary>
public enum L3Source
{
    /// <summary>Use whatever the capability probe found, preferring native over synthetic.</summary>
    Auto,

    /// <summary>Read per-order fields directly from the platform's Level 2 stream.</summary>
    Native,

    /// <summary>Reconstruct order events from high-frequency Level 2 deltas.</summary>
    Synthetic,

    /// <summary>Consume a normalised stream from an external R|Protocol MBO process.</summary>
    Sidecar,
}

/// <summary>
/// How a position that spans more than one contract tier is turned into bracket orders.
///
/// This exists because a bracket belongs to exactly one order on exactly one symbol, and
/// the one data vendor connector rejects an order unless its target quantities and its stop
/// quantities each sum to the order quantity. "4 NQ + 4 MNQ" is therefore two orders with
/// two independent ladders, not one ladder over eight contracts.
/// </summary>
public enum LadderSplitMode
{
    /// <summary>
    /// Solve in micro-equivalents, decompose into per-instrument quantities, then build an
    /// independent four-target ladder for each instrument. Coarse tiers still exit first
    /// across the combined plan.
    /// </summary>
    DecomposeThenLadderPerLeg,

    /// <summary>
    /// Use a single contract tier per signal — whichever fits the risk budget best — so
    /// there is always exactly one order and one ladder.
    /// </summary>
    SingleInstrument,

    /// <summary>
    /// Size entirely in the finest available tier, so the ladder always has enough units
    /// to split across four targets.
    /// </summary>
    MicrosOnly,
}

/// <summary>
/// Who owns the trailing stop.
///
/// The connector carries one trailing flag for a whole bracket and trails from the last
/// trade price, so a per-target tick ladder cannot be expressed natively. Managing it from
/// the strategy gives finer behaviour and loses it on disconnect — the trade-off K4 warns
/// about, made explicit here instead of silently.
/// </summary>
public enum TrailMode
{
    /// <summary>
    /// Send the bracket with the connector's trailing flag and never adjust it. Survives a
    /// platform disconnect because the exit lives at the broker.
    /// </summary>
    NativeBracketOnly,

    /// <summary>
    /// The strategy moves the stop per the configured per-symbol tick ladder or to
    /// structure. Finer, and inert if the platform is not running.
    /// </summary>
    StrategyManaged,

    /// <summary>
    /// Strategy-managed while the platform is connected and healthy, falling back to the
    /// resting native bracket whenever it is not.
    /// </summary>
    Auto,
}

/// <summary>Which trailing geometry the strategy applies when it owns the stop.</summary>
public enum TrailGeometry
{
    /// <summary>Constant tick distance. Used where structure is unreliable.</summary>
    FixedTick,

    /// <summary>Last confirmed lower-timeframe swing plus a buffer.</summary>
    Structure,

    /// <summary>Structure in normal and low volatility buckets, fixed tick in high.</summary>
    Auto,
}

/// <summary>Data tiers, in the order the engine degrades through them.</summary>
public enum DataTier
{
    /// <summary>Bars and traded volume. Nothing below this is survivable.</summary>
    T0,

    /// <summary>Best bid/ask and sizes.</summary>
    T1,

    /// <summary>Trades classified against the quote — footprint and delta.</summary>
    T2,

    /// <summary>Price-aggregated depth, both sides.</summary>
    T3,

    /// <summary>Order-level lifecycle.</summary>
    T4,

    /// <summary>External options positioning.</summary>
    T5,
}

/// <summary>Opening-range quality, computed the instant the range closes.</summary>
public enum OrGrade
{
    /// <summary>Range width below the compressed threshold — favours expansion.</summary>
    Compressed,

    /// <summary>The base case.</summary>
    Normal,

    /// <summary>
    /// The day's likely range is already inside the opening range. Continuation size is
    /// reduced and fades are promoted.
    /// </summary>
    Exhausted,
}

/// <summary>Confluence grade bands.</summary>
public enum SignalGrade
{
    /// <summary>Below the B threshold. Logged, never traded.</summary>
    C,

    /// <summary>Reduced size, no fourth target.</summary>
    B,

    /// <summary>Reduced size, full ladder.</summary>
    A,

    /// <summary>Full size, fourth target armed.</summary>
    APlus,
}

/// <summary>What to do when the economic calendar cannot be read.</summary>
public enum CalendarUnavailablePolicy
{
    /// <summary>Refuse entries until a calendar is present. Correct for unattended running.</summary>
    Block,

    /// <summary>Permit entries and surface the gap. Relies on a human seeing the warning.</summary>
    Warn,
}

/// <summary>
/// The news restriction an account carries.
///
/// Named for the distinction My Funded Futures actually publishes, read verbatim from
/// a prop firm's published news-trading policy on 2026-08-20:
///
/// > "Ensuring no open positions or orders are active in the order book 2 minutes before and
/// > after any data release." ... "These protocols apply to all news releases."
///
/// > "Restricted Accounts: Trading on T1 News events is prohibited for the following:
/// > Rapid Sim Funded, Pro Sim Funded"
/// > "Unrestricted Accounts: Trading on T1 News events is permitted for the following:
/// > All evaluations, 25k and 50k Flex Plans"
/// </summary>
public enum NewsRule
{
    /// <summary>
    /// No news restriction. A retail account, where the only constraint is the trader's own.
    /// </summary>
    None,

    /// <summary>
    /// Flat around every data release. Applies to all evaluations and the 25k/50k Flex plans,
    /// which the policy lists as permitted to trade Tier 1 events.
    /// </summary>
    Standard,

    /// <summary>
    /// <see cref="Standard"/>, and Tier 1 events additionally blocked on their own wider
    /// window. Applies to Rapid Sim Funded and Pro Sim Funded, where the policy prohibits
    /// trading Tier 1 events rather than merely requiring a flat book around them.
    /// </summary>
    ProhibitTier1,
}
