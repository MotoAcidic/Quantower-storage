using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FinchLite;
using TradingPlatform.BusinessLayer;

namespace finchDomScalpStrategy;

/// <summary>
/// "i wanna make this into a strategy now that i can run in quantower were it plays off the
/// orders from the dom and the ifvg and absorption levels and unfinished auctions" (the
/// operator's own ask, 2026-09-25) — automates the same read Finch-Lite's own indicator has been
/// showing on the chart all week: large resting DOM orders, tiered absorption strength, and
/// unfinished auctions (all from <see cref="RestingOrderEngine"/>, compiled in by source from
/// Finch-Lite's own indicator project so this strategy trades the EXACT SAME detection logic the
/// chart draws, not a second copy that could drift), confirmed by an aligned inverse fair value
/// gap (<see cref="FairValueGapEngine"/>, same source-sharing).
///
/// DESIGN, CONFIRMED VIA FOUR CLARIFYING QUESTIONS BEFORE ANY CODE WAS WRITTEN (2026-09-25):
/// - A DOM/unfinished-auction level is the ANCHOR. Strong absorption and an aligned IFVG are
///   CONFIRMATION filters, not independent triggers of their own — all three must line up.
/// - If the level's own side and the nearest IFVG's direction disagree, the trade is SKIPPED
///   entirely rather than picking a side.
/// - Stop sits just beyond the level itself; target is the nearest opposing DOM/IFVG level ahead
///   of price, or a fixed risk:reward fallback if nothing qualifies ahead — same shape as this
///   repo's own `directionAbsorptionScalpStrategy.TryComputeStop`/`TryComputeTarget`.
/// - **NO `DryRun`, NO `ConfirmSimOrEvalAccount`** — explicitly declined when offered, unlike
///   every other auto-trading strategy in this repo. This strategy places real orders the moment
///   it is attached and enabled. Attaching it to a sim/eval account is the operator's own
///   responsibility; nothing in this code enforces or checks that.
///
/// Unfinished auctions are NOT given separate entry logic from ordinary large-order levels —
/// `RestingOrderEngine` already unifies both into one tracked-level list (`IsUnfinished` is just
/// a flag on the same record), and the entry check below treats a qualifying UA level exactly the
/// same as a qualifying fresh level. This was flagged as a judgment call in the plan this
/// strategy was built from, in case "plays off... unfinished auctions" meant something more
/// specific (e.g. a retest-only setup) — worth revisiting if that turns out to be wrong.
///
/// Absorption is computed purely from BOOK SIZE CHANGES between DOM polls (already inside
/// `RestingOrderEngine`), and IFVG detection reads closed BARS, not individual trades. It still
/// holds a no-op `NewLevel2` subscription for the same reason the indicator does:
/// `Symbol.NewLevel2 +=` is what tells the platform to keep maintaining live depth for this
/// symbol at all — the DOM pull depends on that subscription existing somewhere, discovered the
/// hard way earlier this same week.
///
/// POC TRADING, added 2026-09-25 ("can we add trading with the poc in the strategy as well") —
/// SIX more clarifying questions answered before writing this, since it added a genuinely new
/// entry pathway with real-money implications:
/// - **Target role**: current-move and 15m POC (`PocEngine`, same source-shared engine and same
///   "since the last confirmed swing" design as the indicator's own feature — see its own doc
///   comment) both now join the candidate pool in `TryComputeTarget`, for EITHER entry pathway.
/// - **Entry role**: a brand-new, STANDALONE trigger, independent of the DOM/absorption/IFVG
///   pathway above — `CheckPocRejection`. It does NOT touch `TryEnter`'s own logic at all.
/// - **Direction**: a REJECTION away from the POC (POC acts as support/resistance), not a
///   reversion toward it.
/// - **Detection**: bar-close confirmed — a closed bar's high/low must touch or pierce the POC,
///   but its CLOSE must end up beyond `PocRejectionBufferTicks` on the origin side, same
///   close-based confirmation style Finch-Lite's own `OrderBlockEngine`/`FairValueGapEngine` use.
/// - **Eligible POCs**: either the current-move or the 15m POC independently qualifies.
/// - **Gating**: fully SHARED with the DOM/absorption pathway — same cooldown, same daily-loss/
///   drawdown/trade-count limits, same `StrategyTag`, same "flat before entering" check, same one
///   position at a time. `CheckPocRejection` is just a second candidate signal checked once per
///   poll, after `TryEnter` — whichever pathway's guard clauses let it through first wins.
///
/// This DOES require a live tick subscription (`OnLast`), unlike the DOM/absorption/IFVG pathway
/// alone — POC volume-by-price needs individual trade prints, the same as the indicator's own
/// `PocEngine.FeedTrade` usage. Ticks are queued on the market-data thread and drained on the
/// poll timer, same §10-style discipline as everywhere else in this codebase.
///
/// SAFETY NOTE ON BACKLOG BARS: on first attach, `RunPoll` seeds `PocEngine`/`FairValueGapEngine`
/// from up to 500 bars of already-closed chart history in one burst (same backlog-priming the
/// IFVG engine already does). `CheckPocRejection` is deliberately SKIPPED entirely during that
/// first backlog burst — checking it against historical bars that closed before this attach would
/// mean potentially firing a REAL order off stale price action the instant the strategy starts,
/// which given this strategy's own no-dry-run design would be considerably worse than a redraw
/// bug. It only starts evaluating once a bar closes live, after that first catch-up.
///
/// SEPARATE STOP/TARGET ORDERS, added 2026-09-27 — the first live fill on a real Rithmic account
/// exposed a real bug: the entry order used to embed its stop/target as a single
/// `SlTpHolder.CreateSL/CreateTP(price, PriceMeasurement.Absolute)` bracket, and Quantower's own
/// Serilog output showed that bracket's PRICE getting silently reinterpreted as a TICK-COUNT
/// OFFSET from entry (multiplied by tick size, then applied as a distance from entry) rather than
/// used as the literal absolute price requested — confirmed by the math (both legs landed almost
/// exactly at entry ± (requestedPrice × tickSize), not at the requested price itself). The
/// take-profit leg came out ~7700 points away and was instantly refused by the exchange; the
/// stop-loss leg came out ~7700 points the other way and sat there uselessly, LEAVING THE POSITION
/// COMPLETELY UNPROTECTED. `PriceMeasurement.Absolute` was confirmed correct via SDK reflection
/// (the only other value is `Offset`), so this is a bug in how this connection turns an embedded
/// bracket into child orders, not a mistake in the price math feeding it.
///
/// Fix: the entry order now carries NO embedded bracket at all. Once `Core_PositionAdded` confirms
/// the position is open, `PlaceProtectiveOrders` places the stop and target as TWO SEPARATE,
/// EXPLICIT orders — a Stop order using `TriggerPrice` and a Limit order using `Price`, each
/// resolved via their own `OrderTypeBehavior.Stop`/`.Limit` order type, sidestepping whatever in
/// the embedded-bracket path was reinterpreting the value. Both legs carry the SAME `StrategyTag`
/// comment as the entry — this is what lets the EXISTING `Core_PositionRemoved` cleanup correctly
/// cancel whichever leg didn't fill once the position closes (no broker-side OCO is used; this
/// strategy provides that itself by cancelling `MyOrders()` the moment the position count reaches
/// zero). The platform's own auto-generated bracket children from the OLD approach came back with
/// an EMPTY `Comment`, which is exactly why the leftover mispriced stop from that first live trade
/// was never picked up by this same cleanup logic and had to be cancelled by hand.
///
/// A SECOND, independent bug found in that same incident: Quantower's own log showed five
/// "Exception has been thrown by the target of an invocation" errors, exactly correlated with
/// that one order's state-change broadcasts and nowhere else all day — one of this strategy's own
/// `Core_*` event handlers was throwing, almost certainly from dereferencing a null event argument
/// with no null-check (every handler now null-checks its argument first). Quantower's own log
/// doesn't retain the inner exception detail, so the exact original line is unconfirmed, but left
/// unfixed this risked `waitOpenPosition` getting stuck permanently true if the throw happened
/// before that flag was cleared — silently blocking every future entry.
///
/// BREAKEVEN, added 2026-09-27 ("work on the logic for opening a take profit and stop loss and if
/// its running good in profit to move the stop to inprofit to cover fees") — a ONE-TIME move, not
/// a continuously ratcheting trail (the operator's own explicit choice via AskUserQuestion): once
/// open profit reaches `BreakevenTriggerTicks`, `CheckBreakeven` (run every poll alongside
/// `CheckRiskLimits`) modifies the protective stop order's own `TriggerPrice` in place — via
/// `Core.Instance.ModifyOrder`, not a cancel/replace — to sit `BreakevenBufferTicks` beyond entry,
/// then never touches it again for the rest of that trade.
///
/// A THIRD, much bigger bug found testing the fix above: the very next live signal still placed
/// NO protective orders at all. Root cause (see `IsMine`'s own doc comment for the full account):
/// this strategy's `CurrentSymbol` (a continuous selection, e.g. "MNQ") is a DIFFERENT OBJECT from
/// the specific underlying contract ("MNQZ6") every actual Position/Order/Trade comes back tagged
/// with, and NONE of Symbol/Position/Order/Account overload `==` (confirmed via SDK reflection) —
/// so `obj.Symbol == this.CurrentSymbol` was ALWAYS false, silently breaking `MyPositions()`/
/// `MyOrders()` (always empty), every `Core_*` handler's filter, risk limits, breakeven, AND PnL
/// accumulation, all at once — not just the protective-order call. Fixed by never comparing Symbol
/// object identity: the first position/order this strategy sees (matched on Account.Id +
/// ConnectionId + the StrategyTag Comment alone) resolves and caches the actual contract's own
/// Symbol.Id, and every later match compares that STRING instead.
///
/// A FOURTH issue, flagged directly by the operator from that same signal ("the stop it called was
/// so small and short it didnt make sense"): the POC-rejection pathway's stop sits just beyond the
/// REJECTION BAR's own wick, which can be an arbitrarily tiny distance from the actual fill price —
/// a single bar's range says nothing about the instrument's real volatility. `MinStopDistanceTicks`
/// (default 20) now floors BOTH entry pathways' computed stop to at least that many ticks from the
/// current/reference price, widening it out when the naive calculation comes in tighter.
///
/// A FIFTH issue, also operator-flagged ("it seems as soon as i turn it on it enters a trade from
/// the poc line which just sits there and bounces"): `TryPocRejection` alone never required an
/// actual APPROACH to the POC — a market simply chopping right on top of a flat POC satisfies the
/// touch-and-close-beyond-buffer pattern on nearly every bar. `HasGenuineApproach` now requires at
/// least one of `PocApproachLookbackBars` bars BEFORE the rejection bar to have genuinely been
/// `PocApproachDistanceTicks` away from the POC on the origin side, confirming price actually
/// travelled from a distance rather than already sitting on the level; no prior bars available is
/// treated as "cannot confirm" and skipped, not allowed through.
///
/// A SIXTH issue, same day: even the very next backlog-safe signal STILL placed no protective
/// orders. Root cause: `isFirstDrain` (the guard meant to skip stale backlog bars) only ever gated
/// the FIRST poll — the SECOND poll, typically ~250ms later and nowhere near enough time for a new
/// bar to close, read `closedUpTo - 1` again and treated whatever bar was already sitting there at
/// attach time as "the latest closed bar" regardless, firing off pre-existing price action almost
/// instantly on every restart. Fixed with `pocRejectionCheckedUpTo`, an actual bar-index cursor:
/// the first bar this run ever sees is marked as already-accounted-for and never itself eligible;
/// only a STRICTLY LATER index — one that closes live, after this run started watching — is ever
/// passed to `CheckPocRejection`.
///
/// A SEVENTH issue, found immediately after fixing the sixth: a signal fired correctly (minutes
/// after attach, off a genuinely new bar) and STILL placed no protective orders, with zero trace
/// in either log of `PlaceProtectiveOrders` even being attempted. Order/Trade/OrderHistory have
/// shown the correct `Comment: FinchDomScalp` in EVERY single logged fill without exception — but
/// there is no equivalent confirmation `Position.Comment` is ever actually populated on this
/// connection, and `Core_PositionAdded`'s own gate depended on exactly that field. Fixed with
/// `IsMyPosition`, which primarily matches by the ALREADY-resolved `Symbol.Id` (bootstrapped by an
/// Order/Trade event well before any position event needs it) plus Account.Id — no Comment
/// involved — falling back to Position's own Comment only if that resolution hasn't happened yet.
/// A diagnostic log line now fires whenever `Core_PositionAdded` still doesn't match, so any
/// further surprise is visible immediately instead of requiring another blind round-trip.
///
/// AN EIGHTH issue, found on the VERY NEXT live signal against the seventh fix above: the exact
/// same duplicate-order storm reproduced — the "already placed" guard checked
/// `protectiveStopOrder is not null`, but that field only ever got set by an immediate
/// post-placement `GetOrderById` lookup that was failing essentially every time (see
/// `protectiveOrdersPlaced`'s own doc comment for the full account) — so the guard never actually
/// engaged, on either attempt. Replaced with a plain boolean set at the INSTANT placement is
/// attempted, no lookup involved; anything needed later (breakeven) is now found on demand via
/// `FindProtectiveStopOrder`, through the same Comment-based `MyOrders()` filter already proven
/// reliable, rather than a cached reference from the lookup that never worked.
///
/// A NINTH issue, same incident, operator-flagged from the chart itself: a target realized only
/// 9 ticks from the actual fill despite `MinTargetDistanceTicks` being 10. That floor is enforced
/// when the candidate is CHOSEN (`TryComputeTarget`), against a signal-time reference price —
/// not the price the market order fills at moments later. `PlaceProtectiveOrders` now
/// re-validates both `pendingStopPrice`/`pendingTargetPrice` against `position.OpenPrice` (the
/// REAL fill) right before submitting either order, via `EnforceMinStopDistance` and a new mirror
/// `EnforceMinTargetDistance` — both widen outward if the realized distance from the actual fill
/// would otherwise land under the configured minimum.
///
/// FOUR MORE CHANGES, 2026-09-28, all operator-driven from reviewing live signals:
/// - **Swing-based stops**: "stop losses should be places at recent swing low for longs and
///   recent swing high for shorts" — `SwingTracker` (a new, standalone fractal-pivot tracker, run
///   UNCONDITIONALLY regardless of whether any POC feature is enabled, since stops now depend on
///   it) replaces the old level-price/rejection-bar-extreme stop calculation in BOTH entry
///   pathways via `ComputeSwingStop`, falling back to the old calculation only if no swing has
///   confirmed yet this run. `MinStopDistanceTicks`/fill-price re-validation still apply on top.
/// - **5-minute POC**: "while trading based off the 15min poc could be dangerous... it could take
///   a long time for that to play out" — `Poc5mEnabled` adds a middle-ground timeframe between the
///   fast current-move POC and the slow 15m one, same engine/rejection logic, own dedicated
///   5-minute series, own on/off switch, eligible as both a target candidate and its own
///   standalone rejection trigger exactly like the other two.
/// - **Current-move minimum swing size**: "playing off the 1min poc this makes no sense the rr
///   for this trade is super negative" — the current-move POC sits on the chart's own timeframe
///   and can be formed by a tiny, barely-there swing while still producing a target sized like any
///   other anchor. `PocCurrentMoveMinSwingTicks` (via `HasSufficientSwingSize`, using the same
///   `SwingTracker`) requires the most recent completed swing leg to span a minimum distance
///   before the current-move POC is allowed to anchor a trade at all. Deliberately NOT applied to
///   5m/15m, which already represent more deliberate structure by virtue of their own timeframe.
/// - **Delta filter**: "if we keep trying to take longs when delta is negative and in the red we
///   are just fighting our selves" — `DeltaFilterEnabled`/`DeltaLookbackBars` (a new
///   `DeltaTracker`, same tick-classification approach as Finch-Lite's own delta panel) sums
///   buy-minus-sell volume over the last N closed bars and HARD-BLOCKS (the operator's own choice
///   over a softer confirmation) any long when that rolling sum is net negative, and any short
///   when it's net positive — applied via `PassesDeltaFilter` to every entry pathway, not just
///   the POC one that prompted it.
///
/// THREE MORE FIXES, still 2026-09-28, from ONE live sequence the operator caught in real time:
/// a target hit, then ~200ms later a BRAND NEW entry fired whose stop got refused outright by
/// Rithmic, leaving that position with a target and ZERO stop-loss until it closed on its own.
/// - **Wrong-side swing stop**: the refused stop sat on the WRONG side of the trade's own entry —
///   `SwingTracker.LastSwingHigh`/`LastSwingLow` only update on a NEWLY confirmed pivot, so after a
///   strong one-directional run with no pullback long enough to confirm a fresh one, the tracked
///   swing can be stale and already behind where price has since moved. `ComputeSwingStop` now
///   takes the trade's own current/entry price and rejects (returns null, falling back to the
///   caller's own correct-by-construction calculation) any computed stop that isn't actually on
///   the right side of it.
/// - **No safety net on a refused stop**: even with that fix, some OTHER refusal reason could
///   still happen. `PlaceProtectiveOrders` now closes the position IMMEDIATELY if the stop leg
///   fails for any reason at all, rather than continuing on to place the target and leave the
///   position running unprotected — with no dry-run gate and no human watching every signal, a
///   position with zero stop is never acceptable, full stop.
/// - **Cooldown never reset on a CLOSE, only on an entry**: `MinBarsBetweenEntries` only ever
///   measured bars since `lastEntryBarIndex` was set at ENTRY time — a trade that rides a while
///   before hitting target can clear that cooldown long before it actually closes, leaving nothing
///   to stop a fresh signal firing the instant the position goes flat (confirmed: ~200ms gap in
///   the log). `Core_PositionRemoved` now also resets `lastEntryBarIndex` to the current bar on
///   every close, so a new entry needs the full cooldown from whichever happened more recently —
///   the last entry OR the last close.
///
/// A TENTH issue, found the very next round of live testing after the above three fixes: a trade
/// entered off the current-move POC with a stop that was technically on the correct side (passed
/// the ninth fix's own check) but 84 points away — an unretraced rally had left the last confirmed
/// swing low far below current price — producing roughly a 1:5.5 risk:reward trade ("what is this
/// risk to reward here this is crazy"). A maximum-distance cap on the stop itself was considered
/// and rejected: a wide-but-PROPORTIONATE stop (an earlier legitimate trade risked 118 ticks for a
/// ~2.9:1 reward) shouldn't be rejected just for being wide. `PassesRiskRewardFilter` checks the
/// actual economics instead — reward must be worth at least `MinRewardRiskPercent` of the risk,
/// regardless of what produced either number — applied as the LAST gate before any entry, in both
/// pathways.
///
/// TRAILING STOP, added 2026-09-28 ("does it ever trail the stop loss after it keeps going in
/// profit?" — no, `CheckBreakeven` was always a ONE-TIME move). Watching an open, well-in-profit
/// trade keep climbing after its breakeven move, the operator proposed "trail to the bottom of the
/// previous candle once its in profit already," then validated the idea live by moving that same
/// trade's stop manually: "worked wonders price came down to it but never hit it because that is
/// the support area for the continue push up" — and framed the goal as "look for the point were we
/// can move the stop more into profit but avoid being filled." `CheckTrailingStop` implements
/// exactly that: once `CheckBreakeven` has already fired (never before, never as a replacement for
/// it), every newly-closed chart bar ratchets the stop to just beyond that bar's own low (long) /
/// high (short) — a level price has just finished respecting, so an ordinary pullback retesting it
/// shouldn't stop the trade out, only a genuine break of it should. Same ratchet-only discipline as
/// breakeven (a worse candidate than the current stop is simply skipped) and the same benign-race
/// handling on a failed modify.
///
/// TREND FILTER, added 2026-09-29, from a full overnight loss review ("review some of the trades
/// from last night in the logs and see what we could have done better because we are down -130ish
/// over the night"): 9 of 20 round trips were the DOM/absorption pathway re-shorting the SAME
/// resting ask level over ~3 hours against a sustained overnight rally (only 3 of the 9 won, the
/// other 6 combined for -$278), while the mirror-image longs off the opposing level (trading WITH
/// that rally) went 4-2 for +$124.50. `PassesDeltaFilter` was already wired into this pathway but
/// its default 8-bar window is local enough to clear on almost any short pullback inside an
/// hours-long trend. `PassesTrendFilter` adds a second, independent hard block on the SAME
/// `DeltaTracker` with a much longer window (`TrendDeltaLookbackBars`, default 60) — meant to catch
/// "is the whole session running against this trade," not just the last few bars' local reaction.
///
/// ABSORPTION AS POC CONFLUENCE, added 2026-09-29 — SUPERSEDES the original 2026-09-25 design at
/// the top of this comment. The overnight review above (see "TREND FILTER") showed the standalone
/// DOM/absorption pathway (`TryEnter` — the ANCHOR/CONFIRMATION design from that original section)
/// was where nearly all of one bad night's losses actually came from. The operator's own question:
/// "would it make more sense to use the absorption levels and resting orders as a confluence to the
/// poc trading instead of its own trading since it doesnt work out really well on its own?" —
/// confirmed via `AskUserQuestion`: remove `TryEnter` ENTIRELY (no standalone DOM/absorption
/// trigger at all anymore) and make a qualifying resting-order level a HARD requirement inside the
/// POC-rejection pathway instead, via `HasAbsorptionConfluence`. Every POC rejection now needs a
/// qualifying level (same `MinLevelSize`/`AbsorptionStrongContracts` thresholds the old standalone
/// pathway used) within `PocAbsorptionProximityTicks` of the POC, on the side that actually defends
/// the rejection direction — no exceptions, however clean the rejection itself looks. Resting
/// levels are still consumed elsewhere (as TARGET candidates in `TryComputeTarget`) — only their
/// role as an independent ENTRY trigger is gone. `CheckPocRejection` is now the only entry trigger
/// in this strategy.
///
/// STOP vs. RESTING LIQUIDITY, same day — a separate, related observation from live charts: "the
/// stop is right on top of the resting orders which is were price normally wants to go to pick up
/// orders so it should be a few ticks past that so if it comes and touches it doesnt stop us out
/// but we need to be out if it blows through those orders." A resting order sitting right where the
/// stop already lands is a magnet for a normal liquidity sweep, not evidence the trade is wrong —
/// `ExtendStopPastRestingLevel` pushes the stop `StopBufferTicks` further past any such level
/// instead of leaving it sitting on top of one, so a bare touch doesn't stop the trade out but a
/// genuine break past that liquidity still does. Only ever widens the stop, never tightens it.
///
/// OBSERVABILITY, added 2026-09-29 ("is there a way to show logs on if some of the conditions are
/// not met so i know its working becuase it hasnt gotten in a trade in a while") — with this many
/// stacked gates now, a quiet stretch is ambiguous from the outside: nothing qualifying, or
/// something stuck? Two additions, neither touches trading logic at all: `TryGatePocSignal` logs a
/// `[Signal skipped]` line naming the EXACT gate (swing size, local delta, trend, absorption
/// confluence) that blocked a rejection ONCE ONE IS ALREADY GENUINELY DETECTED — so it only fires
/// on a real near-miss, never as noise on every ordinary bar. `CheckHeartbeat` logs a periodic
/// snapshot (POC levels, delta readings, session state) on a fixed interval regardless of whether
/// anything is happening at all — proof the poll loop itself is alive even when no rejection
/// pattern has even been attempted. `HeartbeatIntervalMinutes` (0=off) controls the second one only;
/// the skipped-signal logging is unconditional.
///
/// LOCAL DELTA FILTER DISABLED BY DEFAULT, same day — the observability logging above immediately
/// showed the local (8-bar) delta filter blocking nearly every genuine rejection attempt. Not a
/// tuning issue but a structural mismatch: a POC rejection is a REVERSAL bet, and this filter
/// requires recent order flow to ALREADY agree with the new direction — but at the exact moment of
/// a real rejection, recent delta still reflects the OLD, about-to-reverse direction almost by
/// definition. `DeltaFilterEnabled` now defaults to false; `TrendFilterEnabled`'s 60-bar window
/// asks a different, still-valid question ("is the whole session running against this trade") and
/// stays on. See `DeltaFilterEnabled`'s own doc comment.
/// </summary>
public sealed class finchDomScalpStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    private const string StrategyTag = "FinchDomScalp";

    // ---- instrument/account ---------------------------------------------------------------

    [InputParameter("Symbol", 0)]
    public Symbol CurrentSymbol { get; set; }

    [InputParameter("Account", 1)]
    public Account CurrentAccount { get; set; }

    [InputParameter("Period", 2)]
    public Period Period { get; set; }

    [InputParameter("Start Point", 3)]
    public DateTime StartPoint { get; set; }

    [InputParameter("Quantity", 4, 1, 1000000, 1, 0)]
    public int Quantity { get; set; }

    [InputParameter("Poll interval (ms)", 5, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; }

    [InputParameter("DOM: levels to scan per side", 6, 5, 2000, 5, 0)]
    public int LevelsToScan { get; set; }

    // ---- resting-order/absorption thresholds — CHANGED 2026-09-29 from "the DOM/UA level is the
    // anchor" (its own standalone entry trigger, `TryEnter`) to "absorption is a required
    // CONFLUENCE check for a POC rejection" (see `HasAbsorptionConfluence` and the class doc
    // comment's "ABSORPTION AS POC CONFLUENCE" section) — the operator's own call after review
    // showed the standalone DOM pathway was where nearly all of one bad night's losses came from.
    // These two thresholds are unchanged in meaning, just consumed by a different caller now. ----

    [InputParameter("Min level size (contracts)", 10, 1, 100000, 1, 0)]
    public int MinLevelSize { get; set; }

    /// <summary>Same tiering concept as the indicator's own "maxed out" absorption tier — a
    /// level must have this much size traded through it while still standing before it counts
    /// as confirmation, not just a large resting order nobody has tested yet.</summary>
    [InputParameter("Absorption: strong tier (contracts)", 11, 1, 1000000, 1, 0)]
    public int AbsorptionStrongContracts { get; set; }

    [InputParameter("Unfinished auction: price must move past by (ticks)", 12, 1, 100000, 1, 0)]
    public int UnfinishedDistanceTicks { get; set; }

    // ---- exits ------------------------------------------------------------------------------

    /// <summary>How far beyond the stop reference — the recent swing low/high since 2026-09-28
    /// (see <see cref="ComputeSwingStop"/>), or the DOM level/rejection-bar extreme as a fallback
    /// before any swing has confirmed — the stop actually sits. FOUND 2026-09-28 ("have it a few
    /// ticks below the swing low incase it comes and bounces off that point again"): the original
    /// default (2 ticks) left almost no room for an ordinary wick retest of the exact swing point
    /// to avoid clipping the stop before price actually reverses.</summary>
    [InputParameter("Stop buffer beyond swing/level (ticks)", 20, 0, 1000, 1, 0)]
    public int StopBufferTicks { get; set; }

    [InputParameter("Minimum target distance (ticks)", 21, 1, 100000, 1, 0)]
    public int MinTargetDistanceTicks { get; set; }

    [InputParameter("Fallback target if nothing qualifies ahead (ticks)", 22, 1, 100000, 1, 0)]
    public int FallbackTargetTicks { get; set; }

    /// <summary>FOUND 2026-09-28 ("what is this risk to reward here this is crazy") — a live trade
    /// risked 84 points to make 15.25 (~1:5.5 AGAINST the trade) after a stale swing-low reference
    /// survived `ComputeSwingStop`'s own "correct side" check by being technically valid but
    /// absurdly far away. Checked as the LAST gate before any entry, in both pathways, via
    /// `PassesRiskRewardFilter`: the reward must be worth at least this percentage of the risk
    /// being taken, regardless of what produced either number. 100 = require at least 1:1. 0
    /// disables the check entirely.</summary>
    [InputParameter("Minimum reward:risk (%, 0=off)", 25, 0, 100000, 1, 0)]
    public int MinRewardRiskPercent { get; set; }

    /// <summary>FOUND 2026-09-28 ("is there a way we can have a button to turn off break evens") —
    /// the trigger field below already doubled as an off-switch at 0, but that's a numeric trick,
    /// not an obvious toggle. This is a plain checkbox instead. Gates `CheckBreakeven` directly, on
    /// top of (not instead of) the trigger's own 0-disables behavior. NOTE: since
    /// `CheckTrailingStop` only ever activates once breakeven has already fired (the operator's own
    /// choice — see `TrailAfterBreakevenEnabled`'s doc comment), turning this OFF also means the
    /// stop will never trail for that run.</summary>
    [InputParameter("Breakeven: enable", 53)]
    public bool BreakevenEnabled { get; set; }

    /// <summary>
    /// Open profit required before the stop moves to breakeven+buffer, as a PERCENTAGE of the
    /// trade's own risk (its stop's own distance from the actual fill) — a ONE-TIME move, not a
    /// continuous trail (see the class doc comment's "BREAKEVEN" section). 0 disables the feature
    /// entirely (redundant with `BreakevenEnabled` above, kept for anyone who already relies on it).
    ///
    /// CHANGED 2026-09-28 from a fixed tick count ("i got this error on break even stop failed
    /// after it got out of the trade... the rr for trades are way negative because it will loose
    /// like $30 with no issue but once it gets into a trade a little bit it moves the breakeven
    /// and only makes like 2-10$"): once stops became swing-based (often far wider than the old
    /// level/bar-extreme calculation — one live trade had a 118-tick stop), a FIXED tick trigger
    /// became trivially easy to clear long before a trade had earned back anything close to its
    /// own risk, capping every winner at a tiny scratch while losers still ran the full, now much
    /// wider stop. 50 (the default) means "once halfway back to even relative to risk," a standard
    /// trade-management convention; the actual risk distance is captured fresh per trade in
    /// `PlaceProtectiveOrders`, from the REAL fill price, not whatever was estimated at signal
    /// time.
    /// </summary>
    [InputParameter("Breakeven: trigger (% of trade's own risk, 0=off)", 23, 0, 500, 1, 0)]
    public int BreakevenTriggerRiskPercent { get; set; }

    /// <summary>
    /// How far beyond entry (in the trade's own favor) the stop moves to once triggered — meant to
    /// cover round-turn fees/commission rather than landing exactly at entry.
    ///
    /// WIDENED 2026-09-28 from an original 3 ("it just did an order that was so tight that i
    /// actually lost money because of fees when it moved the stop loss"): a live trade's own
    /// numbers showed why 3 wasn't enough — the breakeven stop triggered at 30,520.00, but being a
    /// STOP-MARKET order, it doesn't guarantee a fill AT that trigger; the actual fill landed at
    /// 30,521.75, SEVEN ticks of adverse slippage that alone wiped out the entire buffer and left
    /// the trade WORSE than its own entry price (30,521.5) before fees were even counted. This
    /// isn't a bug in the breakeven math — `CheckBreakeven` triggered and moved the stop exactly as
    /// designed — it's that a 3-tick buffer has no real margin against ordinary stop-order slippage
    /// on a fast move, on top of the fees it was already meant to cover. A bigger buffer gives up
    /// more of the original risk before it counts as "locked in," but a thin one that gets erased
    /// by slippage the first time price moves quickly isn't actually protecting anything.
    /// </summary>
    [InputParameter("Breakeven: buffer beyond entry (ticks)", 24, 0, 1000, 1, 0)]
    public int BreakevenBufferTicks { get; set; }

    /// <summary>FOUND 2026-09-27 ("the stop it called was so small and short it didnt make
    /// sense") — the POC-rejection pathway's own stop sits just beyond the REJECTION BAR's own
    /// wick, which can be an arbitrarily tiny distance from where the entry actually fills (a
    /// single bar's range says nothing about the instrument's real volatility). A stop computed
    /// closer than this many ticks from the current/reference price is widened out to exactly
    /// this distance instead — applies to BOTH entry pathways' stop, not just the POC one, for the
    /// same reason.</summary>
    [InputParameter("Minimum stop distance (ticks)", 25, 1, 100000, 1, 0)]
    public int MinStopDistanceTicks { get; set; }

    // ---- risk management — same shape as directionAbsorptionScalpStrategy's own -------------

    [InputParameter("Max daily loss ($, 0=off)", 30, 0, 100000, 50, 0)]
    public int MaxDailyLoss { get; set; }

    [InputParameter("Max drawdown ($, 0=off)", 31, 0, 100000, 50, 0)]
    public int MaxDrawdown { get; set; }

    [InputParameter("Max trades per session (0=off)", 32, 0, 200, 1, 0)]
    public int MaxTradesPerSession { get; set; }

    [InputParameter("Cooldown between entries (bars)", 33, 0, 500, 1, 0)]
    public int MinBarsBetweenEntries { get; set; }

    /// <summary>FOUND 2026-09-28 ("i also need the ability to choose to trade in asia and london
    /// and ny sessions so i need to be able to turn on and off sessions") — replaces the old single
    /// RTH on/off window (which was really just a crude stand-in for "NY session only") with the
    /// standard order-flow session breakdown the operator's own long-term vision is built on. Master
    /// switch: when off, every session toggle below is ignored and trading runs 24h, matching this
    /// strategy's behavior before this feature existed. When on, a signal is only allowed through if
    /// the current time (America/New_York, same `SessionZone` every other time-of-day check in this
    /// file already uses) falls inside at least one ENABLED session's own window — see
    /// `IsInAllowedSession`. NOTE: this is a different "session" than `CheckSessionReset`'s daily
    /// P&L-rollover session — that one tracks the trading DAY boundary, this one tracks which market
    /// (Asia/London/New York) is open.</summary>
    [InputParameter("Session filter: enable (off = trade 24h)", 54)]
    public bool SessionFilterEnabled { get; set; }

    /// <summary>Default 19:00–04:00 ET, spanning midnight (Tokyo/Sydney open) — `IsInHourWindow`
    /// handles the wraparound.</summary>
    [InputParameter("Session - Asia: enable", 55)]
    public bool AsiaSessionEnabled { get; set; }

    [InputParameter("Session - Asia: start hour (ET)", 56, 0, 23, 1, 0)]
    public int AsiaSessionStartHour { get; set; }

    [InputParameter("Session - Asia: end hour (ET)", 57, 0, 23, 1, 0)]
    public int AsiaSessionEndHour { get; set; }

    /// <summary>Default 03:00–12:00 ET (London open through the NY overlap).</summary>
    [InputParameter("Session - London: enable", 58)]
    public bool LondonSessionEnabled { get; set; }

    [InputParameter("Session - London: start hour (ET)", 59, 0, 23, 1, 0)]
    public int LondonSessionStartHour { get; set; }

    [InputParameter("Session - London: end hour (ET)", 60, 0, 23, 1, 0)]
    public int LondonSessionEndHour { get; set; }

    /// <summary>Default 08:00–17:00 ET — the full NY futures session (wider than the old RTH
    /// default of 9–16, which was specifically equity regular hours).</summary>
    [InputParameter("Session - New York: enable", 61)]
    public bool NySessionEnabled { get; set; }

    [InputParameter("Session - New York: start hour (ET)", 62, 0, 23, 1, 0)]
    public int NySessionStartHour { get; set; }

    [InputParameter("Session - New York: end hour (ET)", 63, 0, 23, 1, 0)]
    public int NySessionEndHour { get; set; }

    // ---- POC — target candidates for both entry pathways, plus its own standalone rejection
    // trigger (see the class doc comment's "POC TRADING" section) ----------------------------

    [InputParameter("POC: enable current-move", 40)]
    public bool PocCurrentMoveEnabled { get; set; }

    [InputParameter("POC: enable 15m higher-timeframe", 41)]
    public bool Poc15mEnabled { get; set; }

    /// <summary>FOUND 2026-09-27 ("while trading based off the 15min poc could be dangerous
    /// because if its in a down trend and we are looking for a pull back it could take a long
    /// time for that to play out") — a middle-ground timeframe between the fast, reactive
    /// current-move POC and the slow, structurally-heavy 15m one. Same engine, same rejection
    /// logic, own dedicated 5-minute series and own on/off switch, so it can be used instead of or
    /// alongside either of the other two.</summary>
    [InputParameter("POC: enable 5m", 47)]
    public bool Poc5mEnabled { get; set; }

    /// <summary>Same fractal swing-pivot rule as Finch-Lite's own indicator, shared by all three
    /// POC engines (current-move, 5m, 15m).</summary>
    [InputParameter("POC: swing pivot lookback (bars)", 42, 1, 20, 1, 0)]
    public int PocSwingPivotLookback { get; set; }

    /// <summary>How far back the dedicated 5m/15m series load on attach — only needs enough bars
    /// to locate the current swing structure, same reasoning as the indicator's own
    /// PocLookbackDays.</summary>
    [InputParameter("POC: 5m/15m history lookback (days)", 43, 1, 90, 1, 0)]
    public int PocLookbackDays { get; set; }

    /// <summary>How far beyond the POC a bar's CLOSE must end up, on the origin side, before a
    /// touch/pierce counts as a confirmed rejection rather than an inconclusive wick.</summary>
    [InputParameter("POC: rejection close buffer (ticks)", 44, 0, 1000, 1, 0)]
    public int PocRejectionBufferTicks { get; set; }

    /// <summary>How many bars BEFORE the rejection bar to scan for a genuine approach — see
    /// <see cref="PocApproachDistanceTicks"/>'s own doc comment for why this exists.</summary>
    [InputParameter("POC rejection: approach lookback (bars)", 45, 1, 100, 1, 0)]
    public int PocApproachLookbackBars { get; set; }

    /// <summary>FOUND 2026-09-27 ("it seems as soon as i turn it on it enters a trade from the poc
    /// line which just sits there and bounces") — without this, a market chopping right on top of
    /// a flat POC satisfies the rejection pattern on nearly every bar, since some wick always
    /// touches a nearby POC and ordinary noise closes past a small buffer constantly. At least one
    /// of the <see cref="PocApproachLookbackBars"/> bars before the rejection bar must have been
    /// this many ticks away from the POC on the origin side, confirming price actually travelled
    /// from a distance rather than already sitting on the level.</summary>
    [InputParameter("POC rejection: minimum approach distance (ticks)", 46, 1, 100000, 1, 0)]
    public int PocApproachDistanceTicks { get; set; }

    /// <summary>FOUND 2026-09-27 (operator, reading a live signal: "playing off the 1min poc this
    /// makes no sense the rr for this trade is super negative") — the current-move POC sits on the
    /// CHART's own timeframe and can be formed by a tiny, barely-there swing, yet still produces a
    /// target sized the same as any other anchor. Requires the most recently confirmed swing leg
    /// (high to low, from `SwingTracker`) to span at least this many ticks before the current-move
    /// POC is allowed to anchor a trade at all — 0 disables the check. Deliberately NOT applied to
    /// the 5m/15m POCs, which already represent more deliberate structure by virtue of their own
    /// timeframe.</summary>
    [InputParameter("POC current-move: minimum swing size to qualify (ticks, 0=off)", 48, 0, 100000, 1, 0)]
    public int PocCurrentMoveMinSwingTicks { get; set; }

    /// <summary>FOUND 2026-09-29 ("would it make more sense to use the absorption levels and
    /// resting orders as a confluence to the poc trading instead of its own trading since it
    /// doesnt work out really well on its own?") — the operator's own call, after a loss review
    /// showed the standalone DOM/absorption pathway (`TryEnter`, now REMOVED) was where nearly all
    /// of one bad night's losses came from. A POC rejection now requires a qualifying resting-order
    /// level (same `MinLevelSize`/`AbsorptionStrongContracts` thresholds the old standalone pathway
    /// used) within this many ticks of the POC price, on the side that actually defends the
    /// rejection direction — a BID near the POC for a bullish rejection, an ASK for a bearish one.
    /// A HARD requirement (the operator's own choice, same as every other filter added this
    /// project): no qualifying level nearby, no trade, however clean the POC rejection itself
    /// looks. See `HasAbsorptionConfluence`.
    ///
    /// WIDENED 2026-09-29 from an original 10 to 20 — confirmed via the new `[Signal skipped]`
    /// logging (built specifically to answer "did we make the restrictions to tight now") that this
    /// was the actual bottleneck: a full 60 minutes into a strongly trending session (60-bar trend
    /// delta around -2181), Sell rejections aligned WITH that trend were clearing the delta/trend
    /// filters fine and dying here specifically — "no qualifying absorption within 10 ticks" — while
    /// zero trades fired all session. Widened rather than touching `MinLevelSize`/
    /// `AbsorptionStrongContracts` themselves, since those thresholds also define "real absorption"
    /// everywhere else and a proximity change is more targeted.</summary>
    [InputParameter("POC: required absorption confluence proximity (ticks)", 66, 1, 100000, 1, 0)]
    public int PocAbsorptionProximityTicks { get; set; }

    /// <summary>FOUND 2026-09-29 ("lets make a check box to add that confluence so to enable or
    /// disable it") — even after widening `PocAbsorptionProximityTicks`, absorption confluence kept
    /// rejecting trades the operator felt should have gone through, and there was no way to turn the
    /// requirement off without reverting the whole architecture change. A plain checkbox instead:
    /// when off, `HasAbsorptionConfluence` is bypassed entirely (always passes, no confirming level)
    /// and a POC rejection can fire on the delta/trend/R:R gates alone, the way the POC pathway
    /// originally worked before absorption became a hard requirement. Defaults to true — this does
    /// NOT silently revert today's confluence change; the operator turns it off explicitly if the
    /// gate proves too restrictive in practice.</summary>
    [InputParameter("POC: require absorption confluence", 68)]
    public bool PocAbsorptionConfluenceEnabled { get; set; }

    // ---- delta filter — see the class doc comment's "DELTA FILTER" section ------------------

    /// <summary>FOUND 2026-09-27 (operator: "if we keep trying to take longs when delta is
    /// negative and in the red we are just fighting our selves") — a HARD block (the operator's
    /// own choice over a softer confirmation): a long is skipped entirely when the rolling delta
    /// window is net negative, a short is skipped entirely when it's net positive, regardless of
    /// which pathway or POC produced the signal.
    ///
    /// DISABLED BY DEFAULT 2026-09-29 ("it keeps saying local delta blocks everything is the delta
    /// really important to this trading style?") — a structural conflict, not a tuning problem: a
    /// POC rejection is a REVERSAL bet (price is expected to turn AWAY from what it was just doing),
    /// but this filter requires the last <see cref="DeltaLookbackBars"/> bars' order flow to
    /// ALREADY agree with the NEW direction before allowing entry. At the exact moment of a genuine
    /// rejection, recent delta is still going to reflect the OLD, about-to-reverse direction almost
    /// by definition — that's what a turning point looks like. This filter fits a continuation/trend
    /// entry far better than a reversal one, which is what this strategy actually trades since
    /// `TryEnter` was removed. `TrendFilterEnabled`'s own 60-bar window asks a different, still-
    /// valid question ("is the whole SESSION running against this trade") and stays on by default.
    /// </summary>
    [InputParameter("Delta filter: enable", 49)]
    public bool DeltaFilterEnabled { get; set; }

    /// <summary>How many recently-closed chart bars the rolling delta sum covers — the operator's
    /// own choice over session-cumulative or single-bar delta, since it reacts to recent flow
    /// without being as noisy as one bar alone.</summary>
    [InputParameter("Delta filter: rolling window (bars)", 50, 1, 200, 1, 0)]
    public int DeltaLookbackBars { get; set; }

    /// <summary>FOUND 2026-09-29, reviewing an overnight session down ~$130 — 9 of the night's 20
    /// trades were the DOM/absorption pathway re-shorting the SAME resting ask level over and over
    /// across a ~3-hour stretch while price trended up the entire night (only 3 of those 9 won, a
    /// combined -$278 on the other 6); the mirror-image longs off the opposing bid level, trading
    /// WITH that same trend, went 4-2 for +$124.50. `PassesDeltaFilter`'s existing rolling window
    /// (default 8 bars) is local enough to clear on almost any short pullback even inside an
    /// hours-long trend, so it didn't stop the pathway from repeatedly fading a level the broader
    /// session flow was actually running through. This is a SECOND, independent hard block — same
    /// `DeltaTracker`, same rolling-sum mechanism, just a much longer window meant to capture the
    /// broader session bias rather than the last few bars' reaction: a long is skipped if the LONG
    /// window is net negative, a short is skipped if it's net positive, applied everywhere
    /// `PassesDeltaFilter` already is. Independent on/off switch from the local delta filter, since
    /// they answer different questions ("did the last few bars just react against me" vs. "is the
    /// whole session running against me").</summary>
    [InputParameter("Trend filter: enable", 64)]
    public bool TrendFilterEnabled { get; set; }

    /// <summary>Deliberately much larger than <see cref="DeltaLookbackBars"/> — meant to span
    /// hours, not minutes, on whatever chart period this strategy is attached to.</summary>
    [InputParameter("Trend filter: rolling window (bars)", 65, 1, 200, 1, 0)]
    public int TrendDeltaLookbackBars { get; set; }

    // ---- diagnostics — see the class doc comment's "OBSERVABILITY" section ------------------

    /// <summary>FOUND 2026-09-29 ("is there a way to show logs on if some of the conditions are
    /// not met so i know its working becuase it hasnt gotten in a trade in a while") — with as many
    /// stacked gates as this strategy now has (delta, trend, absorption confluence, R:R, session,
    /// cooldown...), a quiet stretch is genuinely ambiguous from the outside: is nothing qualifying,
    /// or is something actually stuck? Two pieces of new logging answer that without needing to
    /// touch a debugger: `CheckPocRejection` now logs a `[Signal skipped]` line naming EXACTLY which
    /// gate blocked a genuine POC rejection once one is actually detected, and this heartbeat logs a
    /// periodic snapshot (POC levels, delta readings, session state) even when nothing at all is
    /// happening — proof the poll loop itself is alive. 0 disables the heartbeat entirely (the
    /// skipped-signal logging is unconditional, since it only ever fires on a genuine near-miss, not
    /// on every poll).</summary>
    [InputParameter("Heartbeat: log interval (minutes, 0=off)", 67, 0, 1440, 1, 0)]
    public int HeartbeatIntervalMinutes { get; set; }

    // ---- trailing stop (after breakeven) — see the class doc comment's "TRAILING STOP" section ----

    /// <summary>FOUND 2026-09-28 — the operator watched a trade move to breakeven twice as it kept
    /// running, then asked "does it ever trail the stop loss after it keeps going in profit?" The
    /// existing breakeven move is a ONE-TIME step; this is a SEPARATE, CONTINUOUS mechanism that
    /// takes over only once breakeven has already fired (never instead of it, never before it).
    /// The operator's own proposed reference: "trail to the bottom of the previous candle once its
    /// in profit already" — confirmed live by the operator manually doing exactly this ("like were
    /// i just moved the stop manually worked wonders price came down to it but never hit it because
    /// that is the support area for the continue push up") and by the explicit goal "look for the
    /// point were we can move the stop more into profit but avoid being filled" — i.e. trail using
    /// a level price has already respected (the prior closed bar's own low/high) rather than a
    /// tight, noise-prone distance, so an ordinary pullback that retests support/resistance doesn't
    /// stop the trade out of its own trend. See <see cref="CheckTrailingStop"/>.</summary>
    [InputParameter("Trailing stop after breakeven: enable", 51)]
    public bool TrailAfterBreakevenEnabled { get; set; }

    /// <summary>How far beyond the prior closed bar's own low (long) / high (short) the trailing
    /// stop sits — same slippage/fee reasoning as <see cref="BreakevenBufferTicks"/>, just applied
    /// on every ratchet instead of once.</summary>
    [InputParameter("Trailing stop: buffer beyond prior candle (ticks)", 52, 0, 1000, 1, 0)]
    public int TrailBufferTicks { get; set; }

    // ---- lifecycle state --------------------------------------------------------------------

    private Timer? pollTimer;
    private HistoricalData? hdm;
    private RestingOrderEngine? restingOrderEngine;
    private FairValueGapEngine? fvgEngine;
    private PocEngine? pocCurrentMoveEngine;
    private PocEngine? poc15mEngine;
    private HistoricalData? poc15mHistory;
    private int poc15mBarsSeen;
    private PocEngine? poc5mEngine;
    private HistoricalData? poc5mHistory;
    private int poc5mBarsSeen;

    /// <summary>Runs UNCONDITIONALLY, independent of whether either POC feature is enabled — see
    /// its own class doc comment for why stop placement can't depend on a feature toggle.</summary>
    private SwingTracker? swingTracker;
    private readonly ConcurrentQueue<(double Price, double Size)> pocTickQueue = new();

    /// <summary>Runs only when <see cref="DeltaFilterEnabled"/> is on — unlike the swing tracker,
    /// this has no other use in the strategy, so there is no reason to pay for tick classification
    /// when the filter itself is switched off.</summary>
    private DeltaTracker? deltaTracker;
    private readonly ConcurrentQueue<(DateTime TimeUtc, double Size, bool IsBuy)> deltaTickQueue = new();
    private string? orderTypeId;
    private string? stopOrderTypeId;
    private string? limitOrderTypeId;
    private int chartBarsSeen;

    /// <summary>-1 sentinel: not yet initialized this run. See the "FOUND 2026-09-27" comment at
    /// its own use site in <c>RunPoll</c> for why this exists instead of a one-shot boolean.</summary>
    private int pocRejectionCheckedUpTo = -1;

    private int barCounter;
    private int lastEntryBarIndex;

    private bool waitOpenPosition;
    private bool waitClosePositions;

    // ---- heartbeat — see HeartbeatIntervalMinutes's own doc comment -------------------------
    private DateTime runStartUtc;
    private DateTime lastHeartbeatUtc;
    private DateTime? lastTradeOpenedUtc;

    // ---- protective stop/target — placed as separate orders once the position confirms open,
    // never as an embedded SlTpHolder bracket on the entry (see the class doc comment's
    // "SEPARATE STOP/TARGET ORDERS" section) ---------------------------------------------------

    private double pendingStopPrice;
    private double pendingTargetPrice;

    /// <summary>FOUND 2026-09-27 (a runaway duplicate-order storm — roughly ten protective
    /// stop/target pairs placed and cancelled within ~100ms on a live account, several rejected
    /// outright by Rithmic's own risk system): the PREVIOUS guard against re-placing protective
    /// orders checked `protectiveStopOrder is not null`, where that field was set from
    /// `Core.Instance.GetOrderById(...)` called IMMEDIATELY after `PlaceOrder` returned. That
    /// lookup was failing essentially every time (logged as "could not be looked up for later
    /// breakeven modification") — almost certainly a race between the synchronous `PlaceOrder`
    /// return and the platform's own internal indexing of the new order — so the field stayed
    /// null and the "already placed" guard was a complete no-op every single time
    /// `Core_PositionAdded` fired again. This flag is set the instant placement is ATTEMPTED
    /// (not dependent on any lookup succeeding), so the guard in `Core_PositionAdded` is now real
    /// regardless of whether that lookup ever works. Finding the actual stop order later (for
    /// breakeven) now happens on demand via `FindProtectiveStopOrder`, filtered through
    /// `MyOrders()` — which relies on Order.Comment, confirmed reliable in every logged fill,
    /// not the same unreliable immediate lookup.</summary>
    private bool protectiveOrdersPlaced;

    /// <summary>The current trade's own risk (in PRICE units, not ticks) — the final,
    /// fill-price-re-validated stop's own distance from the actual fill. Captured once in
    /// <see cref="PlaceProtectiveOrders"/>, used by <see cref="CheckBreakeven"/> to scale its
    /// trigger to THIS trade's own risk rather than a fixed tick count (see
    /// <see cref="BreakevenTriggerRiskPercent"/>'s own doc comment for why).</summary>
    private double pendingRiskDistance;

    private bool breakevenMoved;

    private double dailyPnl;
    private double totalRealizedPnl;
    private double peakEquity;
    private bool dailyLimitHit;
    private bool drawdownLimitHit;
    private bool tradesLimitHit;
    private int tradesThisSession;
    private int lastResetDay;
    private int lastTradeSessionDay;

    public finchDomScalpStrategy() : base()
    {
        this.Name = "finchDomScalpStrategy";
        this.Description =
            "Trades Finch-Lite's own DOM/absorption/unfinished-auction levels, confirmed by an "
            + "aligned inverse fair value gap. NO dry-run, NO sim/eval confirmation gate -- "
            + "places real orders immediately once attached and enabled. Attach to a sim/eval "
            + "account yourself; nothing in this code checks that for you.";

        this.Period = Period.MIN1;
        this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-5);
        this.Quantity = 1;
        this.PollIntervalMs = 250;
        this.LevelsToScan = 500;

        this.MinLevelSize = 100;
        this.AbsorptionStrongContracts = 200;
        this.UnfinishedDistanceTicks = 8;

        this.StopBufferTicks = 8;
        this.MinTargetDistanceTicks = 10;
        this.FallbackTargetTicks = 40;
        this.MinRewardRiskPercent = 100;

        this.BreakevenEnabled = true;
        this.BreakevenTriggerRiskPercent = 50;
        this.BreakevenBufferTicks = 15;
        this.MinStopDistanceTicks = 20;

        this.PocCurrentMoveEnabled = true;
        this.Poc15mEnabled = true;
        this.Poc5mEnabled = true;
        this.PocSwingPivotLookback = 3;
        this.PocLookbackDays = 5;
        this.PocRejectionBufferTicks = 3;
        this.PocApproachLookbackBars = 5;
        this.PocApproachDistanceTicks = 10;
        this.PocCurrentMoveMinSwingTicks = 20;
        this.PocAbsorptionProximityTicks = 20;
        this.PocAbsorptionConfluenceEnabled = true;

        this.DeltaFilterEnabled = false;
        this.DeltaLookbackBars = 8;

        this.TrendFilterEnabled = true;
        this.TrendDeltaLookbackBars = 60;

        this.HeartbeatIntervalMinutes = 15;

        this.TrailAfterBreakevenEnabled = true;
        this.TrailBufferTicks = 4;

        this.MaxDailyLoss = 0;
        this.MaxDrawdown = 2000;
        this.MaxTradesPerSession = 10;
        this.MinBarsBetweenEntries = 5;

        this.SessionFilterEnabled = false;
        this.AsiaSessionEnabled = true;
        this.AsiaSessionStartHour = 19;
        this.AsiaSessionEndHour = 4;
        this.LondonSessionEnabled = true;
        this.LondonSessionStartHour = 3;
        this.LondonSessionEndHour = 12;
        this.NySessionEnabled = true;
        this.NySessionStartHour = 8;
        this.NySessionEndHour = 17;
    }

    // ---- lifecycle ----------------------------------------------------------------------------

    protected override void OnRun()
    {
        this.runStartUtc = Core.TimeUtils.DateTimeUtcNow;
        this.lastHeartbeatUtc = this.runStartUtc;
        this.lastTradeOpenedUtc = null;

        this.waitOpenPosition = false;
        this.waitClosePositions = false;
        this.dailyPnl = 0;
        this.lastResetDay = -1;
        this.dailyLimitHit = false;
        this.totalRealizedPnl = 0;
        this.peakEquity = 0;
        this.drawdownLimitHit = false;
        this.tradesThisSession = 0;
        this.lastTradeSessionDay = -1;
        this.tradesLimitHit = false;
        this.barCounter = -1;

        // NOT int.MinValue: `barCounter - lastEntryBarIndex` in the cooldown check would
        // overflow (int.MinValue subtracted from a small positive barCounter exceeds
        // int.MaxValue and wraps to a large NEGATIVE number in unchecked arithmetic), which
        // would incorrectly satisfy `< MinBarsBetweenEntries` and block the very first entry
        // forever. Far enough negative that no realistic barCounter will ever be "within
        // cooldown" of it, without being close enough to int.MinValue to risk the same overflow.
        this.lastEntryBarIndex = -1_000_000;
        this.chartBarsSeen = -1;
        this.pocRejectionCheckedUpTo = -1;

        this.pendingStopPrice = 0;
        this.pendingTargetPrice = 0;
        this.pendingRiskDistance = 0;
        this.protectiveOrdersPlaced = false;
        this.breakevenMoved = false;
        this.resolvedSymbolId = null;
        this.poc5mBarsSeen = 0;

        if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
            this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());
        if (this.CurrentSymbol == null) { this.Log("Symbol not specified.", StrategyLoggingLevel.Error); return; }

        if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
            this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());
        if (this.CurrentAccount == null) { this.Log("Account not specified.", StrategyLoggingLevel.Error); return; }

        if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
        {
            this.Log("Symbol and Account are from different connections.", StrategyLoggingLevel.Error);
            return;
        }

        // LOUD AND UNCONDITIONAL, matching the reference strategy's own account-identity log —
        // the one piece of that pattern kept even though the gate itself was declined.
        this.Log(
            $"[Account] name='{this.CurrentAccount.Name}' id={this.CurrentAccount.Id} "
            + $"connection={this.CurrentAccount.ConnectionId} — NO DryRun, NO ConfirmSimOrEvalAccount "
            + "gate on this strategy. It will place real orders as soon as a signal confirms.",
            StrategyLoggingLevel.Trading);

        this.orderTypeId = Core.OrderTypes
            .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market)
            ?.Id;
        if (string.IsNullOrEmpty(this.orderTypeId))
        {
            this.Log("Connection does not support market orders.", StrategyLoggingLevel.Error);
            return;
        }

        // Resolved once here, same as the market order type above — used to place the protective
        // stop/target as separate orders (see the class doc comment's "SEPARATE STOP/TARGET
        // ORDERS" section) rather than an embedded SlTpHolder bracket on the entry.
        this.stopOrderTypeId = Core.OrderTypes
            .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Stop)
            ?.Id;
        if (string.IsNullOrEmpty(this.stopOrderTypeId))
        {
            this.Log("Connection does not support stop orders.", StrategyLoggingLevel.Error);
            return;
        }

        this.limitOrderTypeId = Core.OrderTypes
            .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Limit)
            ?.Id;
        if (string.IsNullOrEmpty(this.limitOrderTypeId))
        {
            this.Log("Connection does not support limit orders.", StrategyLoggingLevel.Error);
            return;
        }

        this.restingOrderEngine = new RestingOrderEngine();
        this.fvgEngine = new FairValueGapEngine();

        // Lazily constructed here (not as field initializers) so each engine picks up whatever
        // PocSwingPivotLookback the operator actually configured, not whatever the property
        // equalled at object-construction time, before Quantower applies saved InputParameter
        // values — same reasoning Finch-Lite's own indicator documents for its own lazy
        // OrderBlockEngine/PocEngine construction.
        if (this.PocCurrentMoveEnabled)
            this.pocCurrentMoveEngine = new PocEngine(this.PocSwingPivotLookback);

        if (this.Poc15mEnabled)
        {
            try
            {
                var lookback = Core.TimeUtils.DateTimeUtcNow.AddDays(-Math.Max(1, this.PocLookbackDays));
                this.poc15mHistory = this.CurrentSymbol.GetHistory(Period.MIN15, this.CurrentSymbol.HistoryType, lookback);
                this.poc15mEngine = new PocEngine(this.PocSwingPivotLookback);
                this.poc15mBarsSeen = 0;
            }
            catch (Exception ex)
            {
                this.Log($"15m POC unavailable: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // "lets add in the 5min poc that would be safer to play off" (2026-09-27) — a middle
        // ground between the fast current-move POC and the slow 15m one, same engine, own series.
        if (this.Poc5mEnabled)
        {
            try
            {
                var lookback = Core.TimeUtils.DateTimeUtcNow.AddDays(-Math.Max(1, this.PocLookbackDays));
                this.poc5mHistory = this.CurrentSymbol.GetHistory(Period.MIN5, this.CurrentSymbol.HistoryType, lookback);
                this.poc5mEngine = new PocEngine(this.PocSwingPivotLookback);
                this.poc5mBarsSeen = 0;
            }
            catch (Exception ex)
            {
                this.Log($"5m POC unavailable: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // Runs unconditionally — stop placement needs a swing reference regardless of whether any
        // POC feature is enabled (see SwingTracker's own class doc comment).
        this.swingTracker = new SwingTracker(this.PocSwingPivotLookback);

        if (this.DeltaFilterEnabled || this.TrendFilterEnabled)
            this.deltaTracker = new DeltaTracker(this.Period.Duration);

        this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

        // Do-nothing handler, subscribed purely for the side effect: without SOME NewLevel2
        // subscriber, the platform stops maintaining live depth for this symbol and the DOM pull
        // below silently returns an empty book — discovered the hard way in Finch-Lite's own
        // indicator earlier this week (see Quantower-storage/CLAUDE.md).
        //
        // EVERY subscription below unsubscribes first, defensively — FOUND 2026-09-27 ("it like
        // freaked out and kept trying to enter on stop loss lines"): if OnRun ever runs again
        // without OnStop having fully unwound the previous run first (a restart racing its own
        // shutdown, for instance), `+=` alone would leave the SAME handler subscribed twice,
        // so a single real event fires it twice — this is exactly what produced five duplicate
        // protective stop/target pairs at identical prices in one live incident, and reset
        // waitOpenPosition/breakevenMoved on every extra firing too. `-=` before `+=` is a no-op
        // when nothing was subscribed yet and a real fix when something was.
        this.CurrentSymbol.NewLevel2 -= this.OnLevel2;
        this.CurrentSymbol.NewLevel2 += this.OnLevel2;

        // Needed for POC's own volume-by-price accumulation AND (since 2026-09-27) the delta
        // filter's tick classification — the DOM/absorption/IFVG pathway alone never needed a
        // tick subscription (see the class doc comment).
        if (this.PocCurrentMoveEnabled || this.Poc15mEnabled || this.Poc5mEnabled || this.DeltaFilterEnabled
            || this.TrendFilterEnabled)
        {
            this.CurrentSymbol.NewLast -= this.OnLast;
            this.CurrentSymbol.NewLast += this.OnLast;
        }

        Core.PositionAdded -= this.Core_PositionAdded;
        Core.PositionAdded += this.Core_PositionAdded;
        Core.PositionRemoved -= this.Core_PositionRemoved;
        Core.PositionRemoved += this.Core_PositionRemoved;
        Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
        Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
        Core.TradeAdded -= this.Core_TradeAdded;
        Core.TradeAdded += this.Core_TradeAdded;

        var interval = Math.Max(this.PollIntervalMs, 50);
        this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);

        this.Log($"Started [{StrategyTag}].", StrategyLoggingLevel.Trading);
    }

    protected override void OnStop()
    {
        this.pollTimer?.Dispose();
        this.pollTimer = null;

        Core.PositionAdded -= this.Core_PositionAdded;
        Core.PositionRemoved -= this.Core_PositionRemoved;
        Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
        Core.TradeAdded -= this.Core_TradeAdded;

        if (this.CurrentSymbol != null)
        {
            this.CurrentSymbol.NewLevel2 -= this.OnLevel2;
            this.CurrentSymbol.NewLast -= this.OnLast;
        }

        this.hdm?.Dispose();
        this.hdm = null;
        this.poc15mHistory?.Dispose();
        this.poc15mHistory = null;
        this.poc5mHistory?.Dispose();
        this.poc5mHistory = null;
        this.pocCurrentMoveEngine = null;
        this.poc15mEngine = null;
        this.poc5mEngine = null;
        this.pocTickQueue.Clear();
        this.swingTracker = null;
        this.deltaTracker = null;
        this.deltaTickQueue.Clear();
    }

    private void OnLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom)
    {
    }

    /// <summary>
    /// §10-style discipline, same as Finch-Lite's own indicator: no work on the market-data
    /// thread beyond a comparison and an enqueue. POC counts every trade's own volume toward its
    /// own price regardless of which side was the aggressor, so it always gets queued; the delta
    /// filter DOES care about side, so it's only queued when actually classifiable.
    /// </summary>
    private void OnLast(Symbol symbol, Last last)
    {
        if (last is null || last.Size <= 0)
            return;

        this.pocTickQueue.Enqueue((last.Price, last.Size));

        if ((this.DeltaFilterEnabled || this.TrendFilterEnabled) && TryClassify(symbol, last, out var isBuy))
            this.deltaTickQueue.Enqueue((last.Time, last.Size, isBuy));
    }

    /// <summary>
    /// Same aggressor-classification fallback as Finch-Lite's own indicator (`TryClassify`) —
    /// trusts the feed's own AggressorFlag first, falls back to comparing the print's price
    /// against the current bid/ask when the flag is missing/neither. A print that carries no
    /// evidence either way (inside the spread, or a locked/crossed quote) is dropped, not guessed.
    /// </summary>
    private static bool TryClassify(Symbol symbol, Last last, out bool isBuy)
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
            return false;

        isBuy = takenByBuyer;
        return true;
    }

    // ---- the poll -----------------------------------------------------------------------------

    private string? lastPollFault;

    private void OnPollTimer(object? state)
    {
        try
        {
            this.RunPoll();
        }
        catch (Exception ex)
        {
            // An exception escaping a Timer callback entirely is unhandled and terminates the
            // whole platform process — same lesson Finch-Lite's own indicator already learned
            // twice this week. Log and move on; never let this crash the platform.
            var reason = $"Poll threw ({ex.GetType().Name}: {ex.Message})";
            if (!string.Equals(this.lastPollFault, reason, StringComparison.Ordinal))
            {
                this.lastPollFault = reason;
                this.Log(reason, StrategyLoggingLevel.Error);
            }
        }
    }

    private void RunPoll()
    {
        var symbol = this.CurrentSymbol;
        var engine = this.restingOrderEngine;
        var fvg = this.fvgEngine;
        var hdm = this.hdm;

        if (symbol is null || engine is null || fvg is null || hdm is null)
            return;

        this.CheckSessionReset();
        this.CheckRiskLimits();
        this.CheckBreakeven();
        this.CheckHeartbeat();

        // ---- 1. pull the DOM, reconcile tracked levels ----
        var market = symbol.DepthOfMarket;
        if (market is null)
        {
            this.ReportPollFault("symbol no longer exposes a depth-of-market feed");
            return;
        }

        var book = market.GetDepthOfMarketAggregatedCollections(new GetDepthOfMarketParameters
        {
            GetLevel2ItemsParameters = new GetLevel2ItemsParameters { LevelsCount = this.LevelsToScan, GetMBOItems = false },
        });

        if (book is null)
        {
            this.ReportPollFault("depth-of-market call returned nothing");
            return;
        }

        var bids = book.Bids;
        var asks = book.Asks;

        if ((bids?.Length ?? 0) == 0 && (asks?.Length ?? 0) == 0)
        {
            this.ReportPollFault("depth-of-market call returned an empty book (0 bids, 0 asks)");
            return;
        }

        this.lastPollFault = null;

        var nowUtc = Core.TimeUtils.DateTimeUtcNow;
        var bestBid = bids is { Length: > 0 } ? bids.Max(b => b.Price) : double.NaN;
        var bestAsk = asks is { Length: > 0 } ? asks.Min(a => a.Price) : double.NaN;
        var midPrice = double.IsNaN(bestBid) || double.IsNaN(bestAsk) ? double.NaN : (bestBid + bestAsk) / 2.0;
        var tickSize = symbol.TickSize;
        var distanceThreshold = tickSize > 0 ? this.UnfinishedDistanceTicks * tickSize : double.NaN;
        var dayStart = TradingDayStart(nowUtc);

        var levels = engine.Reconcile(nowUtc, dayStart, bids, asks, this.MinLevelSize, midPrice, distanceThreshold);

        // ---- 2. feed any newly-closed chart bars into the IFVG + current-move POC + swing +
        // delta engines ----
        Bar? latestClosedBar = null;
        var priorBars = Array.Empty<Bar>();

        // Drained BEFORE the bar-close loop below so a bar's own delta reflects every tick that
        // arrived for it, not whatever happened to already be queued a poll cycle late.
        while (this.deltaTickQueue.TryDequeue(out var deltaTick))
            this.deltaTracker?.FeedTick(deltaTick.TimeUtc, deltaTick.Size, deltaTick.IsBuy);

        if (hdm.Count > 1)
        {
            var closedUpTo = hdm.Count - 1; // Count - 1 is the still-forming bar
            this.barCounter = closedUpTo;

            var isFirstDrain = this.chartBarsSeen < 0;

            if (isFirstDrain)
                this.chartBarsSeen = Math.Max(0, closedUpTo - 500);

            for (var i = this.chartBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(hdm, i, out var bar))
                {
                    fvg.Feed(bar);
                    this.swingTracker?.FeedBar(bar);
                    this.deltaTracker?.CloseBar(bar.OpenUtc);

                    if (this.PocCurrentMoveEnabled)
                        this.pocCurrentMoveEngine?.FeedBar(bar);
                }
            }

            this.chartBarsSeen = closedUpTo;

            // FOUND 2026-09-27 ("it seems as soon as i turn it on it enters a trade" — repeatedly,
            // on every restart, seconds after attach): `isFirstDrain` above only ever gates the
            // VERY FIRST poll. On the SECOND poll — typically ~250ms later, nowhere near enough
            // time for a new bar to close — `isFirstDrain` is already false, so the OLD guard
            // `!isFirstDrain && ...` happily read `closedUpTo - 1` and treated whatever bar was
            // ALREADY sitting there at attach time as "the latest closed bar," running
            // CheckPocRejection against pre-existing, already-happened price action instead of
            // waiting for a genuinely NEW bar close. `pocRejectionCheckedUpTo` fixes this properly:
            // it tracks the actual BAR INDEX already accounted for, and the first bar this run
            // ever sees is deliberately marked as already-seen (never itself eligible) — only a
            // STRICTLY LATER bar index, meaning one that closes live after this run started
            // watching, is ever passed to CheckPocRejection.
            var rejectionBarIndex = closedUpTo - 1;

            if (this.pocRejectionCheckedUpTo < 0)
            {
                this.pocRejectionCheckedUpTo = rejectionBarIndex;
            }
            else if (rejectionBarIndex > this.pocRejectionCheckedUpTo
                && TryReadBar(hdm, rejectionBarIndex, out var lastBar))
            {
                latestClosedBar = lastBar;

                // Bars strictly BEFORE the rejection bar, used by HasGenuineApproach to confirm
                // price actually travelled from a distance rather than already sitting on the POC
                // (see PocApproachDistanceTicks's own doc comment).
                var priorList = new List<Bar>(Math.Max(0, this.PocApproachLookbackBars));
                for (var back = 1; back <= this.PocApproachLookbackBars; back++)
                {
                    var idx = rejectionBarIndex - back;
                    if (idx < 0) break;
                    if (TryReadBar(hdm, idx, out var priorBar)) priorList.Add(priorBar);
                }

                priorBars = priorList.ToArray();
                this.pocRejectionCheckedUpTo = rejectionBarIndex;
            }
        }

        // ---- 3. drain the dedicated 15m POC series and every queued trade print ----
        this.DrainPoc();

        // ---- 4. evaluate entries — POC rejection is the ONLY entry trigger (CHANGED 2026-09-29,
        // see the class doc comment's "ABSORPTION AS POC CONFLUENCE" section); DOM/absorption
        // levels are now a required confirmation INSIDE that pathway, not a standalone trigger ----
        if (double.IsNaN(midPrice) || tickSize <= 0)
            return;

        if (latestClosedBar is { } rejectionBar)
        {
            this.CheckPocRejection(rejectionBar, priorBars, tickSize, levels, fvg.Active);
            this.CheckTrailingStop(rejectionBar, tickSize);
        }
    }

    /// <summary>Feeds the dedicated 15-minute series into the higher-timeframe POC engine (own
    /// cursor, own history object — independent of the chart's own series above) and every
    /// queued trade print into whichever POC engine(s) are enabled.</summary>
    private void DrainPoc()
    {
        if (this.Poc15mEnabled && this.poc15mHistory is { } h15 && this.poc15mEngine is { } htf && h15.Count > 1)
        {
            var closedUpTo = h15.Count - 1;

            for (var i = this.poc15mBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(h15, i, out var bar))
                    htf.FeedBar(bar);
            }

            this.poc15mBarsSeen = closedUpTo;
        }

        if (this.Poc5mEnabled && this.poc5mHistory is { } h5 && this.poc5mEngine is { } m5 && h5.Count > 1)
        {
            var closedUpTo = h5.Count - 1;

            for (var i = this.poc5mBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(h5, i, out var bar))
                    m5.FeedBar(bar);
            }

            this.poc5mBarsSeen = closedUpTo;
        }

        while (this.pocTickQueue.TryDequeue(out var tick))
        {
            if (this.PocCurrentMoveEnabled)
                this.pocCurrentMoveEngine?.FeedTrade(tick.Price, tick.Size);

            if (this.Poc15mEnabled)
                this.poc15mEngine?.FeedTrade(tick.Price, tick.Size);

            if (this.Poc5mEnabled)
                this.poc5mEngine?.FeedTrade(tick.Price, tick.Size);
        }
    }

    private void ReportPollFault(string reason)
    {
        if (string.Equals(this.lastPollFault, reason, StringComparison.Ordinal))
            return;

        this.lastPollFault = reason;
        this.Log(reason, StrategyLoggingLevel.Trading);
    }

    private static bool TryReadBar(HistoricalData data, int index, out Bar bar)
    {
        bar = default;

        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item)
            return false;

        bar = new Bar(item.TimeLeft, item.Open, item.High, item.Low, item.Close);
        return true;
    }

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan TradingDayOpen = new(18, 0, 0);

    /// <summary>Same 18:00 America/New_York trading-day boundary Finch-Lite's own indicator
    /// already established — one convention, reused here rather than a second one invented for
    /// this strategy alone.</summary>
    private static DateTime TradingDayStart(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, SessionZone);
        var openToday = local.Date + TradingDayOpen;
        var open = local.TimeOfDay >= TradingDayOpen ? openToday : openToday.AddDays(-1);
        return TimeZoneInfo.ConvertTimeToUtc(open, SessionZone);
    }

    // ---- entry logic --------------------------------------------------------------------------

    /// <summary>Nearest OPPOSING resting level, IFVG zone, or POC ahead of price in the trade's
    /// own direction, past the minimum-distance floor — same `IsAhead`/min-distance/nearest-wins
    /// shape as `directionAbsorptionScalpStrategy.TryComputeTarget`, built from Finch-Lite's own
    /// engines instead of that strategy's HH/LL/VWAP/prior-day levels. Resting levels are still a
    /// TARGET candidate here even though they no longer trigger entries on their own (see the class
    /// doc comment's "ABSORPTION AS POC CONFLUENCE" section) — an unrelated use of the same data.</summary>
    private bool TryComputeTarget(
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels,
        IReadOnlyList<FairValueGapEngine.InverseFvgZone> ifvgZones,
        bool isLong, double price, double tickSize,
        out double targetPrice, out string source)
    {
        targetPrice = 0d;
        source = string.Empty;
        var minDistance = this.MinTargetDistanceTicks * tickSize;

        bool IsAhead(double candidate) => isLong ? candidate > price : candidate < price;

        var candidates = new List<(double Price, string Source)>();

        foreach (var level in levels)
        {
            if (level.IsBid == isLong) continue; // same side as the trade — not an opposing level
            if (!IsAhead(level.Price)) continue;
            candidates.Add((level.Price, "opposing DOM/UA level"));
        }

        foreach (var zone in ifvgZones)
        {
            if (zone.IsBullish == isLong) continue; // same role as the trade's own direction
            var edge = isLong ? zone.Bottom : zone.Top;
            if (!IsAhead(edge)) continue;
            candidates.Add((edge, "opposing IFVG zone"));
        }

        // POC has no side/role of its own — a price ahead of the trade in its own direction
        // qualifies regardless of which POC it came from.
        if (this.PocCurrentMoveEnabled && this.pocCurrentMoveEngine?.Poc is { } cmPoc && IsAhead(cmPoc))
            candidates.Add((cmPoc, "current-move POC"));

        if (this.Poc5mEnabled && this.poc5mEngine?.Poc is { } m5Poc && IsAhead(m5Poc))
            candidates.Add((m5Poc, "5m POC"));

        if (this.Poc15mEnabled && this.poc15mEngine?.Poc is { } htfPoc && IsAhead(htfPoc))
            candidates.Add((htfPoc, "15m POC"));

        var qualified = candidates.Where(c => Math.Abs(c.Price - price) >= minDistance).ToList();
        if (qualified.Count == 0)
            return false;

        var nearest = qualified.OrderBy(c => Math.Abs(c.Price - price)).First();
        targetPrice = nearest.Price;
        source = nearest.Source;
        return true;
    }

    // ---- POC rejection — the ONLY entry pathway, see the class doc comment's "POC TRADING" and
    // "ABSORPTION AS POC CONFLUENCE" sections --------------------------------------------------

    /// <summary>
    /// Checked once per poll against only the MOST RECENTLY closed chart bar — never the backlog
    /// (see the class doc comment's "SAFETY NOTE ON BACKLOG BARS"). CHANGED 2026-09-29: no longer
    /// independent of DOM/absorption — <see cref="HasAbsorptionConfluence"/> is now a hard
    /// requirement here, not a separate standalone pathway.
    /// </summary>
    private void CheckPocRejection(
        Bar bar, Bar[] priorBars, double tickSize,
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels,
        IReadOnlyList<FairValueGapEngine.InverseFvgZone> ifvgZones)
    {
        if (!this.PocCurrentMoveEnabled && !this.Poc5mEnabled && !this.Poc15mEnabled) return;
        if (this.waitOpenPosition || this.waitClosePositions) return;
        if (this.dailyLimitHit || this.drawdownLimitHit || this.tradesLimitHit) return;
        if (this.MyPositions().Any()) return;
        if (!this.IsInAllowedSession()) return;
        if (this.barCounter - this.lastEntryBarIndex < this.MinBarsBetweenEntries) return;

        var buffer = this.PocRejectionBufferTicks * tickSize;
        var approachDistance = this.PocApproachDistanceTicks * tickSize;

        if (this.PocCurrentMoveEnabled && this.pocCurrentMoveEngine?.Poc is { } cmPoc
            && TryPocRejection(bar, cmPoc, buffer, out var cmSide)
            && HasGenuineApproach(priorBars, cmPoc, approachDistance, cmSide))
        {
            if (this.TryGatePocSignal("current-move", cmSide, cmPoc, tickSize, levels, requireSwingSize: true, out var cmLevel))
            {
                this.PlacePocEntry(cmSide, bar, cmPoc, tickSize, "current-move", levels, ifvgZones, cmLevel);
                return;
            }
        }

        if (this.Poc5mEnabled && this.poc5mEngine?.Poc is { } m5Poc
            && TryPocRejection(bar, m5Poc, buffer, out var m5Side)
            && HasGenuineApproach(priorBars, m5Poc, approachDistance, m5Side))
        {
            if (this.TryGatePocSignal("5m", m5Side, m5Poc, tickSize, levels, requireSwingSize: false, out var m5Level))
            {
                this.PlacePocEntry(m5Side, bar, m5Poc, tickSize, "5m", levels, ifvgZones, m5Level);
                return;
            }
        }

        if (this.Poc15mEnabled && this.poc15mEngine?.Poc is { } htfPoc
            && TryPocRejection(bar, htfPoc, buffer, out var htfSide)
            && HasGenuineApproach(priorBars, htfPoc, approachDistance, htfSide))
        {
            if (this.TryGatePocSignal("15m", htfSide, htfPoc, tickSize, levels, requireSwingSize: false, out var htfLevel))
                this.PlacePocEntry(htfSide, bar, htfPoc, tickSize, "15m", levels, ifvgZones, htfLevel);
        }
    }

    /// <summary>Runs the remaining gates (swing size for current-move only, delta, trend,
    /// absorption confluence) for a POC rejection that's ALREADY confirmed genuine — a real
    /// rejection pattern with a real prior approach, not chop. Only reached at that point, so a
    /// `[Signal skipped]` log here (see <see cref="HeartbeatIntervalMinutes"/>'s own doc comment)
    /// means something genuinely close to a trade got blocked, not routine noise on every bar.</summary>
    private bool TryGatePocSignal(
        string pocLabel, Side side, double poc, double tickSize,
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels, bool requireSwingSize,
        out RestingOrderEngine.RestingLevel? confirmingLevel)
    {
        confirmingLevel = null;

        if (requireSwingSize && !this.HasSufficientSwingSize(tickSize))
        {
            this.LogPocSignalSkipped(pocLabel, side, poc, "swing leg too small to qualify (PocCurrentMoveMinSwingTicks)");
            return false;
        }

        if (!this.PassesDeltaFilter(side))
        {
            var rollingDelta = this.deltaTracker?.RollingDelta(this.DeltaLookbackBars);
            this.LogPocSignalSkipped(pocLabel, side, poc, $"local delta filter ({this.DeltaLookbackBars}-bar delta={rollingDelta:F0} against this side)");
            return false;
        }

        if (!this.PassesTrendFilter(side))
        {
            var trendDelta = this.deltaTracker?.RollingDelta(this.TrendDeltaLookbackBars);
            this.LogPocSignalSkipped(pocLabel, side, poc, $"trend filter ({this.TrendDeltaLookbackBars}-bar delta={trendDelta:F0} against this side)");
            return false;
        }

        if (!this.HasAbsorptionConfluence(side, poc, tickSize, levels, out confirmingLevel))
        {
            this.LogPocSignalSkipped(pocLabel, side, poc, $"no qualifying absorption within {this.PocAbsorptionProximityTicks} ticks (PocAbsorptionProximityTicks)");
            return false;
        }

        return true;
    }

    private void LogPocSignalSkipped(string pocLabel, Side side, double poc, string reason)
    {
        this.Log($"[Signal skipped] {side} rejection off {pocLabel} POC {poc:0.####} blocked: {reason}.", StrategyLoggingLevel.Trading);
    }

    /// <summary>See <see cref="PocAbsorptionProximityTicks"/>'s own doc comment — a HARD gate, not
    /// a confirmation: a qualifying resting-order level (`MinLevelSize`/`AbsorptionStrongContracts`)
    /// must sit within proximity of the POC, on the side that actually defends the rejection
    /// direction (a BID for a bullish/Buy rejection, an ASK for a bearish/Sell one). Returns the
    /// NEAREST qualifying level to the POC for logging/stop use, not just a bool, since the caller
    /// wants to say which order flow actually backed the trade. See
    /// <see cref="PocAbsorptionConfluenceEnabled"/>'s own doc comment for the on/off switch.</summary>
    private bool HasAbsorptionConfluence(
        Side side, double poc, double tickSize,
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels,
        out RestingOrderEngine.RestingLevel? confirmingLevel)
    {
        confirmingLevel = null;
        if (!this.PocAbsorptionConfluenceEnabled) return true;

        var proximity = this.PocAbsorptionProximityTicks * tickSize;
        var wantBid = side == Side.Buy;

        confirmingLevel = levels
            .Where(l => l.IsBid == wantBid
                && l.Current >= this.MinLevelSize
                && l.Absorbed >= this.AbsorptionStrongContracts
                && Math.Abs(l.Price - poc) <= proximity)
            .OrderBy(l => Math.Abs(l.Price - poc))
            .Cast<RestingOrderEngine.RestingLevel?>()
            .FirstOrDefault();

        return confirmingLevel is not null;
    }

    /// <summary>See <see cref="PocCurrentMoveMinSwingTicks"/>'s own doc comment. Only gates the
    /// current-move POC — 0 (the default-off value would be surprising here; default is
    /// non-zero) disables the check entirely.</summary>
    private bool HasSufficientSwingSize(double tickSize)
    {
        if (this.PocCurrentMoveMinSwingTicks <= 0) return true;
        if (this.swingTracker?.LastSwingHigh is not { } high) return false;
        if (this.swingTracker?.LastSwingLow is not { } low) return false;

        return (high - low) >= this.PocCurrentMoveMinSwingTicks * tickSize;
    }

    /// <summary>See <see cref="DeltaFilterEnabled"/>'s own doc comment — a HARD block, not a
    /// confirmation: a long is skipped entirely when the rolling delta window is net negative, a
    /// short is skipped entirely when it's net positive. Applies to every entry pathway.</summary>
    private bool PassesDeltaFilter(Side side)
    {
        if (!this.DeltaFilterEnabled || this.deltaTracker is null) return true;

        var rollingDelta = this.deltaTracker.RollingDelta(this.DeltaLookbackBars);

        if (side == Side.Buy && rollingDelta < 0) return false;
        if (side == Side.Sell && rollingDelta > 0) return false;

        return true;
    }

    /// <summary>See <see cref="TrendFilterEnabled"/>'s own doc comment — same hard-block shape as
    /// <see cref="PassesDeltaFilter"/>, same underlying tracker, just a much longer window meant to
    /// catch a multi-hour session bias rather than the last few bars' local reaction. Independent
    /// of the local delta filter: either, both, or neither can be on.</summary>
    private bool PassesTrendFilter(Side side)
    {
        if (!this.TrendFilterEnabled || this.deltaTracker is null) return true;

        var trendDelta = this.deltaTracker.RollingDelta(this.TrendDeltaLookbackBars);

        if (side == Side.Buy && trendDelta < 0) return false;
        if (side == Side.Sell && trendDelta > 0) return false;

        return true;
    }

    /// <summary>
    /// See <see cref="SwingTracker"/>'s own class doc comment for why stop placement uses a
    /// dedicated, always-running tracker rather than any POC engine's internal swing state. Null
    /// (no swing confirmed yet this run, OR the confirmed swing is on the WRONG side of price —
    /// see below) lets the caller fall back to its own previous stop calculation.
    ///
    /// FOUND 2026-09-28 ("it opened another order instantly with no stop loss"): a swing high/low
    /// only updates when a NEW pivot actually confirms. After a strong one-directional run with no
    /// pullback long enough to confirm a fresh pivot, the tracked swing can be STALE — already on
    /// the wrong side of current price entirely (a live example: a short's own "swing high"
    /// reference sat BELOW its entry after price had already rallied straight through it with no
    /// pullback). A stop on the wrong side of price is nonsensical, and Rithmic refused it outright
    /// — leaving that position with NO protective stop until `PlaceProtectiveOrders`'s own new
    /// close-on-failure safety net (see its doc comment) caught it. Now validated here directly:
    /// <paramref name="referencePrice"/> is the trade's own current/entry price, and a computed
    /// stop that isn't actually on the correct side of it is rejected rather than sent to the
    /// broker at all.
    /// </summary>
    private double? ComputeSwingStop(bool isLong, double referencePrice, double tickSize)
    {
        var swing = isLong ? this.swingTracker?.LastSwingLow : this.swingTracker?.LastSwingHigh;
        if (swing is not { } s) return null;

        var stop = isLong ? s - (this.StopBufferTicks * tickSize) : s + (this.StopBufferTicks * tickSize);
        var isCorrectSide = isLong ? stop < referencePrice : stop > referencePrice;

        return isCorrectSide ? stop : null;
    }

    /// <summary>
    /// FOUND 2026-09-27 ("it seems as soon as i turn it on it enters a trade from the poc line
    /// which just sits there and bounces") — a real rejection means price approached the POC from
    /// a distance and got turned away; nothing in <see cref="TryPocRejection"/> alone enforces
    /// that. Without this check, a market simply chopping right on top of a flat POC satisfies the
    /// rejection pattern on nearly every bar (some bar's wick will always touch a nearby POC, and
    /// ordinary noise closes beyond a small buffer constantly). Requires at least one of the bars
    /// BEFORE the rejection bar to have genuinely been <see cref="PocApproachDistanceTicks"/> away
    /// from the POC on the origin side — confirming an actual move toward the level, not chop
    /// already sitting on it. No prior bars available (e.g. right after the backlog-priming
    /// window) is treated as "cannot confirm an approach" and skipped, not allowed through.
    /// </summary>
    private static bool HasGenuineApproach(Bar[] priorBars, double poc, double approachDistance, Side side)
    {
        if (priorBars.Length == 0)
            return false;

        // Buy (bullish rejection, price dipped down into the POC and rejected up): price must
        // genuinely have been ABOVE the POC recently. Sell (bearish rejection): genuinely BELOW.
        return side == Side.Buy
            ? priorBars.Any(b => b.Low >= poc + approachDistance)
            : priorBars.Any(b => b.High <= poc - approachDistance);
    }

    /// <summary>
    /// A REJECTION away from the POC (the operator's own explicit choice over a reversion toward
    /// it — see the class doc comment): the bar reached or pierced the POC intrabar, but its own
    /// CLOSE ended up beyond <paramref name="buffer"/> on the ORIGIN side, meaning the level held
    /// as support/resistance rather than being accepted through. Both checks use the SAME bar —
    /// they cannot both fire (a close cannot be simultaneously below AND above the POC by a
    /// positive buffer). This alone does NOT require a genuine approach — see
    /// <see cref="HasGenuineApproach"/>, checked separately by the caller.
    /// </summary>
    private static bool TryPocRejection(Bar bar, double poc, double buffer, out Side side)
    {
        side = default;

        // Bearish rejection: price reached up to/through the POC, but closed meaningfully below
        // it — POC held as resistance, expect continuation down.
        if (bar.High >= poc && bar.Close <= poc - buffer)
        {
            side = Side.Sell;
            return true;
        }

        // Bullish rejection: price reached down to/through the POC, but closed meaningfully
        // above it — POC held as support, expect continuation up.
        if (bar.Low <= poc && bar.Close >= poc + buffer)
        {
            side = Side.Buy;
            return true;
        }

        return false;
    }

    /// <summary>Widens a computed stop out to <see cref="MinStopDistanceTicks"/> from the current/
    /// reference price if it would otherwise sit closer than that — see that parameter's own doc
    /// comment for why this exists.</summary>
    private double EnforceMinStopDistance(double stopPrice, double referencePrice, bool isLong, double tickSize)
    {
        var minDistance = this.MinStopDistanceTicks * tickSize;
        var actualDistance = Math.Abs(referencePrice - stopPrice);

        if (actualDistance >= minDistance)
            return stopPrice;

        return isLong ? referencePrice - minDistance : referencePrice + minDistance;
    }

    /// <summary>
    /// FOUND 2026-09-28 (operator, reading a live signal: "what is this risk to reward here this
    /// is crazy") — a live trade risked 84 points to make 15.25 (roughly 1:5.5 AGAINST the trade).
    /// `ComputeSwingStop`'s own "correct side" validation (added the same day for a related bug)
    /// didn't catch this one: after a strong one-directional rally with no pullback long enough to
    /// confirm a fresh swing low, the tracked swing was stale — technically still on the correct
    /// side of price, just absurdly far from it. Rather than guess at an arbitrary maximum stop
    /// distance (which would also reject a wide-but-PROPORTIONATE stop, like an earlier 118-tick
    /// one that had a comfortable ~2.9:1 reward on top of it and was never a problem), this checks
    /// the actual ECONOMICS of the trade directly: the reward must be worth at least
    /// <see cref="MinRewardRiskPercent"/> of the risk being taken, regardless of what produced
    /// either number. Applied as the LAST check before any entry, in both pathways.
    /// </summary>
    private bool PassesRiskRewardFilter(double referencePrice, double stopPrice, double targetPrice)
    {
        if (this.MinRewardRiskPercent <= 0) return true;

        var risk = Math.Abs(referencePrice - stopPrice);
        if (risk <= 0) return true; // shouldn't happen; nothing meaningful to compare against

        var reward = Math.Abs(targetPrice - referencePrice);
        return reward / risk >= this.MinRewardRiskPercent / 100.0;
    }

    private void PlacePocEntry(
        Side side, Bar rejectionBar, double poc, double tickSize, string pocLabel,
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels,
        IReadOnlyList<FairValueGapEngine.InverseFvgZone> ifvgZones,
        RestingOrderEngine.RestingLevel? confirmingLevel)
    {
        var isLong = side == Side.Buy;

        // "stop losses should be places at recent swing low for longs and recent swing high for
        // shorts" (2026-09-27) — falls back to the rejection bar's own extreme only if no swing
        // has confirmed yet this run.
        var stopPrice = this.ComputeSwingStop(isLong, rejectionBar.Close, tickSize)
            ?? (side == Side.Sell
                ? rejectionBar.High + (this.StopBufferTicks * tickSize)
                : rejectionBar.Low - (this.StopBufferTicks * tickSize));

        // The rejection bar's own CLOSE, not the POC price itself, stands in for "current price"
        // here — same role `midPrice` plays for the DOM/absorption pathway's own target call.
        // The bar has already closed beyond the POC by the rejection buffer by definition, so
        // anchoring target/fallback distance to the POC price instead would measure from a point
        // price has already moved away from.
        stopPrice = this.EnforceMinStopDistance(stopPrice, rejectionBar.Close, isLong, tickSize);

        // "the stop is right on top of the resting orders which is were price normally wants to go
        // to pick up orders so it should be a few ticks past that so if it comes and touches it
        // doesnt stop us out but we need to be out if it blows through those orders" (2026-09-29)
        // — if a qualifying resting-order level sits right where the stop already landed, price
        // sweeping THAT liquidity (a normal thing for price to do, not evidence the trade is wrong)
        // would trigger the stop before any real reversal even has a chance to show up. Pushes the
        // stop past that level instead, never tighter than the swing-based calculation above.
        stopPrice = this.ExtendStopPastRestingLevel(stopPrice, isLong, tickSize, levels);

        var referencePrice = rejectionBar.Close;

        // Reuses the SAME levels/ifvgZones this poll already computed — RestingOrderEngine.
        // Reconcile is stateful (mutates its own tracking dictionaries as a side effect of being
        // called), so it must never be called a second time in the same poll just to get a
        // "fresh" snapshot; doing so would corrupt its own live state.
        var hasTarget = this.TryComputeTarget(
            levels, ifvgZones, isLong, referencePrice, tickSize, out var computedTarget, out var targetSource);

        var targetPrice = hasTarget
            ? computedTarget
            : isLong
                ? referencePrice + (this.FallbackTargetTicks * tickSize)
                : referencePrice - (this.FallbackTargetTicks * tickSize);

        if (!hasTarget)
            targetSource = "fallback R:R";

        if (!this.PassesRiskRewardFilter(referencePrice, stopPrice, targetPrice)) return;

        // Names the confirming resting order too — see HasAbsorptionConfluence's own doc comment;
        // confirmingLevel is never null here (CheckPocRejection already required it to proceed).
        var confluenceDescription = confirmingLevel is { } cl
            ? $", confluence={(cl.IsBid ? "BID" : "ASK")} {cl.Price:0.####} absorbed={cl.Absorbed:N0}"
            : string.Empty;
        var anchorDescription = $"{pocLabel} POC {poc:0.####} rejection{confluenceDescription}";

        this.PlaceEntry(side, stopPrice, targetPrice, anchorDescription, targetSource);
    }

    /// <summary>See the class doc comment's "STOP vs. RESTING LIQUIDITY" section — if a qualifying
    /// resting-order level (same thresholds as <see cref="HasAbsorptionConfluence"/>) sits within
    /// one stop-buffer's width of the naive stop, on the side that would defend against it, the
    /// stop is pushed <see cref="StopBufferTicks"/> further PAST that level instead of sitting on
    /// top of it — a bare touch/sweep of that resting liquidity shouldn't stop the trade out, only
    /// an actual break past it should. Only ever moves the stop FURTHER from entry (never tighter
    /// than what the caller already computed).</summary>
    private double ExtendStopPastRestingLevel(
        double stopPrice, bool isLong, double tickSize, IReadOnlyList<RestingOrderEngine.RestingLevel> levels)
    {
        var buffer = this.StopBufferTicks * tickSize;
        var wantBid = isLong; // a BID sits below price (defends a long's stop side); an ASK above (a short's)

        foreach (var level in levels)
        {
            if (level.IsBid != wantBid) continue;
            if (level.Current < this.MinLevelSize) continue;
            if (Math.Abs(level.Price - stopPrice) > buffer) continue; // not near the stop — leave it alone

            var extended = isLong ? level.Price - buffer : level.Price + buffer;
            stopPrice = isLong ? Math.Min(stopPrice, extended) : Math.Max(stopPrice, extended);
        }

        return stopPrice;
    }

    /// <summary>See <see cref="SessionFilterEnabled"/>'s own doc comment. Returns true (trade
    /// allowed) whenever the filter is off, or the current ET hour falls inside at least one
    /// ENABLED session's own window.</summary>
    private bool IsInAllowedSession()
    {
        if (!this.SessionFilterEnabled) return true;

        var hour = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, SessionZone).Hour;

        if (this.AsiaSessionEnabled && IsInHourWindow(hour, this.AsiaSessionStartHour, this.AsiaSessionEndHour))
            return true;
        if (this.LondonSessionEnabled && IsInHourWindow(hour, this.LondonSessionStartHour, this.LondonSessionEndHour))
            return true;
        if (this.NySessionEnabled && IsInHourWindow(hour, this.NySessionStartHour, this.NySessionEndHour))
            return true;

        return false;
    }

    /// <summary>Hour-of-day window check that handles a session spanning midnight (e.g. Asia's
    /// default 19:00–04:00 ET) — a plain `hour >= start && hour < end` only works when start &lt;
    /// end.</summary>
    private static bool IsInHourWindow(int hour, int startHour, int endHour)
    {
        if (startHour == endHour) return true; // full 24h window
        return startHour < endHour
            ? hour >= startHour && hour < endHour
            : hour >= startHour || hour < endHour;
    }

    /// <summary>Places ONLY the entry order — no embedded bracket. The stop/target are placed as
    /// separate orders once <see cref="Core_PositionAdded"/> confirms the position is actually
    /// open (see the class doc comment's "SEPARATE STOP/TARGET ORDERS" section for why).</summary>
    private void PlaceEntry(
        Side side, double stopPrice, double targetPrice, string anchorDescription, string targetSource)
    {
        this.waitOpenPosition = true;
        this.pendingStopPrice = stopPrice;
        this.pendingTargetPrice = targetPrice;

        this.Log(
            $"[Signal] {side} anchor={anchorDescription} "
            + $"stop={stopPrice:0.####} target={targetPrice:0.####} ({targetSource})",
            StrategyLoggingLevel.Trading);

        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.orderTypeId,
            Quantity = this.Quantity,
            Side = side,
            Comment = StrategyTag,
        });

        if (result.Status == TradingOperationResultStatus.Failure)
        {
            this.Log($"[Order] failed: {result.Message}", StrategyLoggingLevel.Error);
            this.waitOpenPosition = false;
            return;
        }

        this.lastEntryBarIndex = this.barCounter;
        this.tradesThisSession++;

        if (this.MaxTradesPerSession > 0 && this.tradesThisSession >= this.MaxTradesPerSession)
        {
            this.tradesLimitHit = true;
            this.Log($"[Risk] max trades per session ({this.MaxTradesPerSession}) reached.", StrategyLoggingLevel.Trading);
        }
    }

    /// <summary>
    /// Places the stop and target as TWO SEPARATE, EXPLICIT orders (Stop via
    /// <c>TriggerPrice</c>, Limit via <c>Price</c>) once the entry position is confirmed open —
    /// see the class doc comment's "SEPARATE STOP/TARGET ORDERS" section for the live-fill bug
    /// this replaces. Both legs carry the SAME <see cref="StrategyTag"/> comment as the entry,
    /// which is what lets <see cref="Core_PositionRemoved"/>'s existing cleanup find and cancel
    /// whichever leg didn't fill.
    /// </summary>
    private void PlaceProtectiveOrders(Position position)
    {
        if (string.IsNullOrEmpty(this.stopOrderTypeId) || string.IsNullOrEmpty(this.limitOrderTypeId))
        {
            this.Log(
                "[Order] cannot place protective stop/target: stop/limit order type unavailable.",
                StrategyLoggingLevel.Error);
            return;
        }

        var closingSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;

        // FOUND 2026-09-27 ("even the spot the order was placed looks super weird... price was
        // never even there at that price") — a live signal computed target=30735 against a
        // signal-time reference price, but the market order actually filled at 30732.75 a moment
        // later; the realized distance (9 ticks) ended up under MinTargetDistanceTicks (10)
        // despite the candidate having qualified when it was chosen. `pendingStopPrice`/
        // `pendingTargetPrice` were computed against whatever price stood at SIGNAL time —
        // `position.OpenPrice` here is the REAL fill, known only now that the position exists.
        // Re-validating both against it (same floor functions TryEnter/PlacePocEntry already use
        // pre-fill) closes that gap rather than trusting a price that's since moved on.
        var isLong = position.Side == Side.Buy;
        var tickSize = this.CurrentSymbol.TickSize;
        var stopPrice = tickSize > 0
            ? this.EnforceMinStopDistance(this.pendingStopPrice, position.OpenPrice, isLong, tickSize)
            : this.pendingStopPrice;
        var targetPrice = tickSize > 0
            ? this.EnforceMinTargetDistance(this.pendingTargetPrice, position.OpenPrice, isLong, tickSize)
            : this.pendingTargetPrice;

        // Captured here, not at signal time — this is the trade's own REAL risk, used by
        // CheckBreakeven to scale its trigger instead of a fixed tick count (see
        // BreakevenTriggerRiskPercent's own doc comment).
        this.pendingRiskDistance = Math.Abs(stopPrice - position.OpenPrice);

        var stopResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.stopOrderTypeId,
            Quantity = position.Quantity,
            Side = closingSide,
            Comment = StrategyTag,
            TriggerPrice = stopPrice,
        });

        if (stopResult.Status == TradingOperationResultStatus.Failure)
        {
            // FOUND 2026-09-28 ("it opened another order instantly with no stop loss") — a live
            // position rode with a target but ZERO protective stop after the stop leg got refused
            // outright (the wrong-side-swing bug `ComputeSwingStop` now guards against, but this
            // net stays regardless — a refusal could come from elsewhere too, e.g. an exchange
            // price-band check). This strategy has no dry-run gate and no human watching every
            // signal; a position with no stop at all is not an acceptable state to leave running
            // under any circumstance. If the stop cannot be established, the position is closed
            // immediately instead — no target either, since there is nothing left to protect.
            this.Log(
                $"[Order] protective stop failed: {stopResult.Message}. Closing position "
                + "immediately — a trade must never run with no stop at all.",
                StrategyLoggingLevel.Error);

            var closeResult = position.Close();
            if (closeResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] failed to close unprotected position: {closeResult.Message}", StrategyLoggingLevel.Error);

            return;
        }

        var targetResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.limitOrderTypeId,
            Quantity = position.Quantity,
            Side = closingSide,
            Comment = StrategyTag,
            Price = targetPrice,
        });

        if (targetResult.Status == TradingOperationResultStatus.Failure)
            this.Log($"[Order] protective target failed: {targetResult.Message}", StrategyLoggingLevel.Error);

        this.Log(
            $"[Order] protective stop={stopPrice:0.####} target={targetPrice:0.####} "
            + $"(fill={position.OpenPrice:0.####}) placed as separate orders.",
            StrategyLoggingLevel.Trading);
    }

    /// <summary>Widens a computed target out to <see cref="MinTargetDistanceTicks"/> from the
    /// reference price if it would otherwise sit closer than that — mirrors
    /// <see cref="EnforceMinStopDistance"/> for the opposite side of the trade.</summary>
    private double EnforceMinTargetDistance(double targetPrice, double referencePrice, bool isLong, double tickSize)
    {
        var minDistance = this.MinTargetDistanceTicks * tickSize;
        var actualDistance = Math.Abs(targetPrice - referencePrice);

        if (actualDistance >= minDistance)
            return targetPrice;

        return isLong ? referencePrice + minDistance : referencePrice - minDistance;
    }

    /// <summary>Looked up on demand (via `MyOrders()`, Comment-based and confirmed reliable) rather
    /// than cached from the moment of placement — see `protectiveOrdersPlaced`'s own doc comment
    /// for why the immediate-lookup approach this replaces did not work.</summary>
    private Order? FindProtectiveStopOrder() =>
        this.MyOrders().FirstOrDefault(o => string.Equals(o.OrderTypeId, this.stopOrderTypeId, StringComparison.Ordinal));

    /// <summary>
    /// ONE-TIME move to breakeven+buffer — see the class doc comment's "BREAKEVEN" section. Modifies
    /// the existing protective stop order's own TriggerPrice in place rather than cancelling and
    /// replacing it.
    /// </summary>
    private void CheckBreakeven()
    {
        if (this.breakevenMoved || !this.BreakevenEnabled || this.BreakevenTriggerRiskPercent <= 0) return;
        if (!this.protectiveOrdersPlaced) return;

        var positions = this.MyPositions();
        if (positions.Length == 0) return;

        var position = positions[0]; // one position at a time, by design

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0 || this.pendingRiskDistance <= 0) return;

        // Scaled to THIS trade's own risk, not a fixed tick count — see
        // BreakevenTriggerRiskPercent's own doc comment for why a fixed count stopped making
        // sense once stops became swing-based (and therefore much more variable trade to trade).
        var requiredProfitTicks = (this.pendingRiskDistance / tickSize) * (this.BreakevenTriggerRiskPercent / 100.0);
        if (position.GrossPnLTicks < requiredProfitTicks) return;

        var stopOrder = this.FindProtectiveStopOrder();
        if (stopOrder is null) return; // not found yet (or already filled/cancelled) — try again next poll

        var breakevenPrice = position.Side == Side.Buy
            ? position.OpenPrice + (this.BreakevenBufferTicks * tickSize)
            : position.OpenPrice - (this.BreakevenBufferTicks * tickSize);

        var request = new ModifyOrderRequestParameters(stopOrder) { TriggerPrice = breakevenPrice };
        var result = Core.Instance.ModifyOrder(request);

        if (result.Status == TradingOperationResultStatus.Failure)
        {
            // FOUND 2026-09-28 ("i got this error on break even stop failed after it got out of
            // the trade") — a benign race, not a real failure: the position/stop snapshot above is
            // taken BEFORE the network round-trip to modify the order, and if the target fills (or
            // the position otherwise closes) in the gap between that snapshot and this call
            // actually reaching the broker, the order being modified may already be gone —
            // reported here as a generic "Time out" rather than a clean "order not found". The
            // trade itself still closed correctly through its own existing bracket; only logged
            // loudly when a position still genuinely exists, since THAT would mean something
            // actually went wrong rather than just losing a race with the trade finishing first.
            if (this.MyPositions().Length == 0)
                this.Log($"[Risk] breakeven stop move skipped: position closed first ({result.Message}).", StrategyLoggingLevel.Trading);
            else
                this.Log($"[Risk] breakeven stop move failed: {result.Message}", StrategyLoggingLevel.Error);

            return;
        }

        this.breakevenMoved = true;
        this.Log(
            $"[Risk] moved stop to breakeven+buffer ({breakevenPrice:0.####}) after "
            + $"{position.GrossPnLTicks:F1} ticks profit.",
            StrategyLoggingLevel.Trading);
    }

    /// <summary>
    /// CONTINUOUS trail, layered on top of (never instead of, never before) the one-time
    /// <see cref="CheckBreakeven"/> move — see <see cref="TrailAfterBreakevenEnabled"/>'s own doc
    /// comment for the operator's own reasoning. Runs once per newly-closed chart bar (the same
    /// "latestClosedBar" event `CheckPocRejection` already reacts to), ratcheting the stop to just
    /// beyond that bar's own low (long) / high (short) — a level price has just finished respecting,
    /// which is the whole point: an ordinary pullback that retests it shouldn't stop the trade out,
    /// only a genuine break of it should. Only ever moves the stop FURTHER into profit (a stale or
    /// wider candidate than the current stop is simply skipped), so this can never give back
    /// ground the breakeven move (or an earlier trail step) already locked in.
    /// </summary>
    private void CheckTrailingStop(Bar priorBar, double tickSize)
    {
        if (!this.TrailAfterBreakevenEnabled || !this.breakevenMoved) return;
        if (!this.protectiveOrdersPlaced || tickSize <= 0) return;

        var positions = this.MyPositions();
        if (positions.Length == 0) return;

        var position = positions[0]; // one position at a time, by design
        var stopOrder = this.FindProtectiveStopOrder();
        if (stopOrder is null) return; // not found yet (or already filled/cancelled) — try again next bar

        var isLong = position.Side == Side.Buy;
        var buffer = this.TrailBufferTicks * tickSize;
        var candidate = isLong ? priorBar.Low - buffer : priorBar.High + buffer;

        // Ratchet only — never move the stop backward toward (or past) where it already sits.
        var isImprovement = isLong ? candidate > stopOrder.TriggerPrice : candidate < stopOrder.TriggerPrice;
        if (!isImprovement) return;

        var request = new ModifyOrderRequestParameters(stopOrder) { TriggerPrice = candidate };
        var result = Core.Instance.ModifyOrder(request);

        if (result.Status == TradingOperationResultStatus.Failure)
        {
            // Same benign race as CheckBreakeven's own modify (see its doc comment): the position
            // may have closed between the snapshot above and this call reaching the broker.
            if (this.MyPositions().Length == 0)
                this.Log($"[Risk] trailing stop move skipped: position closed first ({result.Message}).", StrategyLoggingLevel.Trading);
            else
                this.Log($"[Risk] trailing stop move failed: {result.Message}", StrategyLoggingLevel.Error);

            return;
        }

        this.Log(
            $"[Risk] trailed stop to {candidate:0.####} (beyond prior candle {(isLong ? "low" : "high")} "
            + $"{(isLong ? priorBar.Low : priorBar.High):0.####}).",
            StrategyLoggingLevel.Trading);
    }

    /// <summary>See <see cref="HeartbeatIntervalMinutes"/>'s own doc comment — a periodic "still
    /// running" snapshot for a quiet strategy, so the operator never has to wonder whether the poll
    /// loop is stuck or genuinely just hasn't seen a qualifying setup. Skips logging while a
    /// position is open — the position itself is already proof the strategy is working.</summary>
    private void CheckHeartbeat()
    {
        if (this.HeartbeatIntervalMinutes <= 0) return;

        var nowUtc = Core.TimeUtils.DateTimeUtcNow;
        if ((nowUtc - this.lastHeartbeatUtc).TotalMinutes < this.HeartbeatIntervalMinutes) return;

        this.lastHeartbeatUtc = nowUtc;

        if (this.MyPositions().Any()) return;

        static string Fmt(double? v) => v is { } x ? x.ToString("0.####") : "n/a";

        var sinceLastTrade = this.lastTradeOpenedUtc is { } t
            ? $"{(nowUtc - t).TotalMinutes:F0} min since last trade"
            : $"{(nowUtc - this.runStartUtc).TotalMinutes:F0} min since start, no trade yet";

        var localDelta = this.DeltaFilterEnabled ? this.deltaTracker?.RollingDelta(this.DeltaLookbackBars) : null;
        var trendDelta = this.TrendFilterEnabled ? this.deltaTracker?.RollingDelta(this.TrendDeltaLookbackBars) : null;

        this.Log(
            $"[Heartbeat] still running, flat, {sinceLastTrade}. "
            + $"Session={(this.IsInAllowedSession() ? "allowed" : "BLOCKED")}. "
            + $"POC: current-move={Fmt(this.pocCurrentMoveEngine?.Poc)} 5m={Fmt(this.poc5mEngine?.Poc)} "
            + $"15m={Fmt(this.poc15mEngine?.Poc)}. "
            + $"Delta: local({this.DeltaLookbackBars})={Fmt(localDelta)} trend({this.TrendDeltaLookbackBars})={Fmt(trendDelta)}. "
            + "Waiting for a genuine POC rejection with absorption confluence.",
            StrategyLoggingLevel.Trading);
    }

    // ---- position isolation ---------------------------------------------------------------

    /// <summary>
    /// FOUND 2026-09-27, investigating why a confirmed live fill never got its protective stop/
    /// target placed: this strategy's own `CurrentSymbol` (e.g. a continuous "MNQ" selection) is
    /// a DIFFERENT OBJECT from the specific underlying contract ("MNQZ6") that every actual
    /// Position/Order/Trade comes back tagged with — Quantower's own order-placing log showed
    /// "Symbol: MNQ" in the request but "MNQZ6" in every resulting Position/Order/Trade record.
    /// Confirmed via SDK reflection that `Symbol`/`Position`/`Order`/`Account` do NOT overload the
    /// `==` operator, so `obj.Symbol == this.CurrentSymbol` was comparing two DIFFERENT, if
    /// related, objects and was ALWAYS false — silently breaking `MyPositions()`/`MyOrders()`
    /// (always empty), every `Core_*` handler's own filter (never matched), risk limits and
    /// breakeven (gated on `MyPositions()`), and PnL accumulation (gated on `Core_TradeAdded`'s
    /// own filter) all at once. This exact pattern was copied from
    /// `directionAbsorptionScalpStrategy`'s own reference shape, which is presumably equally
    /// affected wherever it trades a continuous/generic symbol selection — NOT fixed here, since
    /// this pass only touches this file, but worth knowing.
    ///
    /// Fix: never compare Symbol OBJECT identity. The very first position/order this strategy
    /// observes (matched on Account.Id + ConnectionId + the StrategyTag Comment alone — the
    /// specific contract isn't known yet) resolves and caches the ACTUAL underlying contract's
    /// own Symbol.Id in <see cref="resolvedSymbolId"/>; every later match additionally requires
    /// that same resolved Symbol.Id, comparing STRINGS. Resolved ONCE per run and never reset on
    /// a flat position — the underlying contract for a continuous selection does not change
    /// mid-session outside a contract roll; a roll mid-run would need a restart, an accepted
    /// limitation rather than something silently handled.
    /// </summary>
    private string? resolvedSymbolId;

    private bool IsMine(Symbol? symbol, Account? account, string? comment)
    {
        if (comment != StrategyTag) return false;
        if (account is null || this.CurrentAccount is null) return false;
        if (!string.Equals(account.Id, this.CurrentAccount.Id, StringComparison.Ordinal)) return false;
        if (symbol is null || this.CurrentSymbol is null) return false;
        if (!string.Equals(symbol.ConnectionId, this.CurrentSymbol.ConnectionId, StringComparison.Ordinal)) return false;

        if (this.resolvedSymbolId is null)
        {
            this.resolvedSymbolId = symbol.Id;
            return true;
        }

        return string.Equals(symbol.Id, this.resolvedSymbolId, StringComparison.Ordinal);
    }

    /// <summary>
    /// FOUND 2026-09-27 (third live fill in a row with no protective orders — even after fixing
    /// the Symbol-identity bug `IsMine` documents above): every `Order`/`OrderHistory`/`Trade` in
    /// the platform log has carried the correct `Comment: FinchDomScalp` in EVERY single logged
    /// fill without exception, but `Core_PositionAdded` still never reached
    /// <see cref="PlaceProtectiveOrders"/>. There is no confirmation `Position.Comment` is ever
    /// actually populated on this connection — unlike Order/Trade, it simply has never been
    /// observed working. Position identification now PRIMARILY relies on the resolved
    /// `Symbol.Id` (bootstrapped by `IsMine` from an Order/Trade event, which reliably fires
    /// several times over — see the platform log's own repeated "Order update"/"Order history"
    /// broadcasts — before any position event needs it) plus Account.Id, comparing STRINGS, no
    /// Comment involved. Only if `resolvedSymbolId` isn't set yet (a bootstrap race this strategy
    /// has not actually observed, but can't rule out) does this fall back to checking the
    /// Position's own Comment, in case it DOES work on some other connection.
    ///
    /// ACCEPTED RISK: once `resolvedSymbolId` is set, ANY position on that exact contract+account
    /// is treated as this strategy's own, regardless of Comment — including one the operator
    /// opened manually. Narrower than ideal, but strictly better than the alternative this
    /// replaces, which — per three consecutive live tests — appears to never match at all.
    /// </summary>
    private bool IsMyPosition(Symbol? symbol, Account? account, string? comment)
    {
        if (account is null || this.CurrentAccount is null) return false;
        if (!string.Equals(account.Id, this.CurrentAccount.Id, StringComparison.Ordinal)) return false;
        if (symbol is null) return false;

        if (this.resolvedSymbolId is not null)
            return string.Equals(symbol.Id, this.resolvedSymbolId, StringComparison.Ordinal);

        if (comment != StrategyTag) return false;
        if (this.CurrentSymbol is null || !string.Equals(symbol.ConnectionId, this.CurrentSymbol.ConnectionId, StringComparison.Ordinal))
            return false;

        this.resolvedSymbolId = symbol.Id;
        return true;
    }

    private Position[] MyPositions() => Core.Instance.Positions
        .Where(x => this.IsMyPosition(x.Symbol, x.Account, x.Comment))
        .ToArray();

    private Order[] MyOrders() => Core.Instance.Orders
        .Where(x => this.IsMine(x.Symbol, x.Account, x.Comment))
        .ToArray();

    /// <summary>Every Core_* handler below null-checks its argument first — FOUND 2026-09-27, from
    /// Quantower's own log showing five uncaught exceptions fired exactly when one of these
    /// handlers was invoked with (almost certainly) a null argument, on the very first live fill.
    /// See the class doc comment's second bullet under "SEPARATE STOP/TARGET ORDERS".</summary>
    private void Core_PositionAdded(Position obj)
    {
        if (obj is null) return;

        if (!this.IsMyPosition(obj.Symbol, obj.Account, obj.Comment))
        {
            // Diagnostic only, kept deliberately terse — cheap insurance after three rounds of
            // guessing why this handler wasn't reaching PlaceProtectiveOrders. If this still
            // doesn't fire correctly next time, this line says exactly which field didn't match.
            this.Log(
                $"[Diag] PositionAdded ignored: comment='{obj.Comment}' symbolId='{obj.Symbol?.Id}' "
                + $"accountId='{obj.Account?.Id}' resolvedSymbolId='{this.resolvedSymbolId}' "
                + $"expectedAccountId='{this.CurrentAccount?.Id}'",
                StrategyLoggingLevel.Trading);
            return;
        }

        // FOUND 2026-09-27 ("it like freaked out and kept trying to enter on stop loss lines",
        // then AGAIN after the first fix attempt): this handler fired repeatedly for the SAME
        // already-open position on a live account — each firing placed ANOTHER full stop+target
        // pair (roughly ten duplicate pairs placed and cancelled within ~100ms in the worst
        // observed case, several rejected outright by Rithmic's own trading-protection system)
        // and reset waitOpenPosition/breakevenMoved every single time. The FIRST attempt at this
        // guard checked `protectiveStopOrder is not null` — but that field depended on an
        // immediate post-placement lookup that was failing essentially every time (see
        // `protectiveOrdersPlaced`'s own doc comment), so the guard was silently a no-op and the
        // storm repeated even after that fix was deployed. `protectiveOrdersPlaced` is set the
        // instant placement is ATTEMPTED, independent of any lookup, so this guard is now real.
        // Checked BEFORE touching any other state, so a duplicate/redundant firing does nothing
        // at all — not even the flag resets below.
        if (this.protectiveOrdersPlaced)
            return;

        this.protectiveOrdersPlaced = true;
        this.waitOpenPosition = false;
        this.breakevenMoved = false;
        this.lastTradeOpenedUtc = Core.TimeUtils.DateTimeUtcNow;

        try
        {
            this.PlaceProtectiveOrders(obj);
        }
        catch (Exception ex)
        {
            this.Log(
                $"[Order] failed to place protective stop/target: {ex.GetType().Name}: {ex.Message}",
                StrategyLoggingLevel.Error);
        }
    }

    private void Core_PositionRemoved(Position obj)
    {
        if (obj is null || !this.IsMyPosition(obj.Symbol, obj.Account, obj.Comment)) return;

        if (!this.MyPositions().Any())
        {
            this.waitClosePositions = false;
            this.protectiveOrdersPlaced = false;
            this.breakevenMoved = false;

            // FOUND 2026-09-28 ("it opened another order instantly with no stop loss" / "going
            // into stacked orders"): MinBarsBetweenEntries only ever measured bars since the last
            // ENTRY was placed, never bars since the last position CLOSED. A trade that rode for a
            // while before hitting its target could easily have already cleared that cooldown by
            // the time it closed, leaving nothing to stop a brand-new signal from firing the
            // instant the position went flat — confirmed live: a target fill and the next entry
            // were ~200ms apart. Resetting the SAME cooldown clock here too means a fresh signal
            // now needs MinBarsBetweenEntries bars from whichever happened more recently — the
            // last entry OR the last close.
            this.lastEntryBarIndex = this.barCounter;

            var equity = this.totalRealizedPnl;
            if (equity > this.peakEquity) this.peakEquity = equity;

            try
            {
                foreach (var order in this.MyOrders())
                {
                    var r = order.Cancel();
                    if (r.Status != TradingOperationResultStatus.Success)
                        this.Log($"[Order] failed to cancel leftover order: {r.Message}", StrategyLoggingLevel.Error);
                }
            }
            catch (Exception ex)
            {
                this.Log($"[Order] failed while cancelling leftover orders: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }
    }

    private void Core_OrdersHistoryAdded(OrderHistory obj)
    {
        if (obj is null || !this.IsMine(obj.Symbol, obj.Account, obj.Comment)) return;

        if (obj.Status == OrderStatus.Refused)
        {
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
        }
    }

    private void Core_TradeAdded(Trade obj)
    {
        if (obj is null || !this.IsMine(obj.Symbol, obj.Account, obj.Comment)) return;

        if (obj.GrossPnl is { } pnl)
        {
            this.dailyPnl += pnl.Value;
            this.totalRealizedPnl += pnl.Value;
        }
    }

    // ---- risk management — same shape as directionAbsorptionScalpStrategy's own ---------------

    private static int EstSessionDayKey(DateTime utcNow)
    {
        var est = TimeZoneInfo.ConvertTimeFromUtc(utcNow, SessionZone);
        return est.Hour >= 18 ? est.DayOfYear + 1 : est.DayOfYear;
    }

    private void CheckSessionReset()
    {
        var dayKey = EstSessionDayKey(Core.TimeUtils.DateTimeUtcNow);

        if (dayKey != this.lastResetDay)
        {
            this.lastResetDay = dayKey;
            this.dailyPnl = 0;
            this.dailyLimitHit = false;
            this.Log("[Risk] daily P&L reset (new EST session).", StrategyLoggingLevel.Trading);
        }

        if (dayKey != this.lastTradeSessionDay)
        {
            this.lastTradeSessionDay = dayKey;
            this.tradesThisSession = 0;
            this.tradesLimitHit = false;
        }
    }

    private void CheckRiskLimits()
    {
        if (this.waitOpenPosition || this.waitClosePositions) return;

        var positions = this.MyPositions();
        if (!positions.Any()) return;

        var unrealizedPnlTicks = positions.Sum(x => x.GrossPnLTicks);
        var tickValue = this.CurrentSymbol.TickSize > 0 ? this.CurrentSymbol.GetTickCost(1) : 0;
        var unrealizedPnl = unrealizedPnlTicks * tickValue;

        if (this.MaxDailyLoss > 0 && !this.dailyLimitHit)
        {
            var totalDailyPnl = this.dailyPnl + unrealizedPnl;
            if (totalDailyPnl <= -this.MaxDailyLoss)
            {
                this.dailyLimitHit = true;
                this.waitClosePositions = true;
                this.Log($"[Risk] DAILY LOSS LIMIT HIT — ${totalDailyPnl:F2} <= -${this.MaxDailyLoss}. Closing all.", StrategyLoggingLevel.Trading);
                foreach (var pos in positions) pos.Close();
                return;
            }
        }

        if (this.MaxDrawdown > 0 && !this.drawdownLimitHit)
        {
            var currentEquity = this.totalRealizedPnl + unrealizedPnl;
            if (currentEquity > this.peakEquity) this.peakEquity = currentEquity;
            var drawdown = this.peakEquity - currentEquity;

            if (drawdown >= this.MaxDrawdown)
            {
                this.drawdownLimitHit = true;
                this.waitClosePositions = true;
                this.Log($"[Risk] MAX DRAWDOWN HIT — ${drawdown:F2} >= ${this.MaxDrawdown}. Closing all.", StrategyLoggingLevel.Trading);
                foreach (var pos in positions) pos.Close();
            }
        }
    }
}
