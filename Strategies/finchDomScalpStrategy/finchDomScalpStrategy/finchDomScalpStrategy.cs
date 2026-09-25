using System;
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
/// Deliberately does NOT need tick classification at all: absorption is computed purely from
/// BOOK SIZE CHANGES between DOM polls (already inside `RestingOrderEngine`), and IFVG detection
/// reads closed BARS, not individual trades — so unlike Finch-Lite's own indicator, this strategy
/// has no `OnLast`/`TryClassify` machinery. It still holds a no-op `NewLevel2` subscription for
/// the same reason the indicator does: `Symbol.NewLevel2 +=` is what tells the platform to keep
/// maintaining live depth for this symbol at all — the DOM pull depends on that subscription
/// existing somewhere, discovered the hard way earlier this same week.
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

    // ---- entry confluence: the DOM/UA level is the anchor ----------------------------------

    [InputParameter("Min level size (contracts)", 10, 1, 100000, 1, 0)]
    public int MinLevelSize { get; set; }

    /// <summary>Same tiering concept as the indicator's own "maxed out" absorption tier — a
    /// level must have this much size traded through it while still standing before it counts
    /// as a confirmed anchor, not just a large resting order nobody has tested yet.</summary>
    [InputParameter("Absorption: strong tier (contracts)", 11, 1, 1000000, 1, 0)]
    public int AbsorptionStrongContracts { get; set; }

    [InputParameter("Unfinished auction: price must move past by (ticks)", 12, 1, 100000, 1, 0)]
    public int UnfinishedDistanceTicks { get; set; }

    /// <summary>How close (in ticks) a level's own price must be to an active IFVG zone's own
    /// [Bottom, Top] range to count as "aligned" confirmation.</summary>
    [InputParameter("IFVG: proximity tolerance (ticks)", 13, 0, 1000, 1, 0)]
    public int IfvgProximityTicks { get; set; }

    // ---- exits ------------------------------------------------------------------------------

    [InputParameter("Stop buffer beyond level (ticks)", 20, 0, 1000, 1, 0)]
    public int StopBufferTicks { get; set; }

    [InputParameter("Minimum target distance (ticks)", 21, 1, 100000, 1, 0)]
    public int MinTargetDistanceTicks { get; set; }

    [InputParameter("Fallback target if nothing qualifies ahead (ticks)", 22, 1, 100000, 1, 0)]
    public int FallbackTargetTicks { get; set; }

    // ---- risk management — same shape as directionAbsorptionScalpStrategy's own -------------

    [InputParameter("Max daily loss ($, 0=off)", 30, 0, 100000, 50, 0)]
    public int MaxDailyLoss { get; set; }

    [InputParameter("Max drawdown ($, 0=off)", 31, 0, 100000, 50, 0)]
    public int MaxDrawdown { get; set; }

    [InputParameter("Max trades per session (0=off)", 32, 0, 200, 1, 0)]
    public int MaxTradesPerSession { get; set; }

    [InputParameter("Cooldown between entries (bars)", 33, 0, 500, 1, 0)]
    public int MinBarsBetweenEntries { get; set; }

    [InputParameter("RTH only (0=24h, 1=RTH only)", 34, 0, 1, 1, 0)]
    public int RthOnly { get; set; }

    [InputParameter("RTH start hour (EST)", 35, 0, 23, 1, 0)]
    public int RthStartHour { get; set; }

    [InputParameter("RTH end hour (EST)", 36, 0, 23, 1, 0)]
    public int RthEndHour { get; set; }

    // ---- lifecycle state --------------------------------------------------------------------

    private Timer? pollTimer;
    private HistoricalData? hdm;
    private RestingOrderEngine? restingOrderEngine;
    private FairValueGapEngine? fvgEngine;
    private string? orderTypeId;
    private int chartBarsSeen;
    private int barCounter;
    private int lastEntryBarIndex;

    private bool waitOpenPosition;
    private bool waitClosePositions;

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
        this.IfvgProximityTicks = 5;

        this.StopBufferTicks = 2;
        this.MinTargetDistanceTicks = 10;
        this.FallbackTargetTicks = 40;

        this.MaxDailyLoss = 0;
        this.MaxDrawdown = 2000;
        this.MaxTradesPerSession = 10;
        this.MinBarsBetweenEntries = 5;
        this.RthOnly = 0;
        this.RthStartHour = 9;
        this.RthEndHour = 16;
    }

    // ---- lifecycle ----------------------------------------------------------------------------

    protected override void OnRun()
    {
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

        this.restingOrderEngine = new RestingOrderEngine();
        this.fvgEngine = new FairValueGapEngine();

        this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

        // Do-nothing handler, subscribed purely for the side effect: without SOME NewLevel2
        // subscriber, the platform stops maintaining live depth for this symbol and the DOM pull
        // below silently returns an empty book — discovered the hard way in Finch-Lite's own
        // indicator earlier this week (see Quantower-storage/CLAUDE.md).
        this.CurrentSymbol.NewLevel2 += this.OnLevel2;

        Core.PositionAdded += this.Core_PositionAdded;
        Core.PositionRemoved += this.Core_PositionRemoved;
        Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
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
            this.CurrentSymbol.NewLevel2 -= this.OnLevel2;

        this.hdm?.Dispose();
        this.hdm = null;
    }

    private void OnLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom)
    {
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

        // ---- 2. feed any newly-closed chart bars into the IFVG engine ----
        if (hdm.Count > 1)
        {
            var closedUpTo = hdm.Count - 1; // Count - 1 is the still-forming bar
            this.barCounter = closedUpTo;

            if (this.chartBarsSeen < 0)
                this.chartBarsSeen = Math.Max(0, closedUpTo - 500);

            for (var i = this.chartBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(hdm, i, out var bar))
                    fvg.Feed(bar);
            }

            this.chartBarsSeen = closedUpTo;
        }

        // ---- 3. evaluate entry ----
        if (double.IsNaN(midPrice) || tickSize <= 0)
            return;

        this.TryEnter(levels, fvg.Active, midPrice, tickSize);
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

    private void TryEnter(
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels,
        IReadOnlyList<FairValueGapEngine.InverseFvgZone> ifvgZones,
        double price, double tickSize)
    {
        if (this.waitOpenPosition || this.waitClosePositions) return;
        if (this.dailyLimitHit || this.drawdownLimitHit || this.tradesLimitHit) return;
        if (this.MyPositions().Any()) return;
        if (this.RthOnly == 1 && !this.IsInRth()) return;
        if (this.barCounter - this.lastEntryBarIndex < this.MinBarsBetweenEntries) return;

        var proximity = this.IfvgProximityTicks * tickSize;

        foreach (var level in levels)
        {
            if (level.Current < this.MinLevelSize) continue;
            if (level.Absorbed < this.AbsorptionStrongContracts) continue;

            var hasAlignedIfvg = ifvgZones.Any(z =>
                z.IsBullish == level.IsBid
                && level.Price >= z.Bottom - proximity
                && level.Price <= z.Top + proximity);

            if (!hasAlignedIfvg) continue;

            var side = level.IsBid ? Side.Buy : Side.Sell;
            var stopPrice = level.IsBid
                ? level.Price - (this.StopBufferTicks * tickSize)
                : level.Price + (this.StopBufferTicks * tickSize);

            var hasTarget = this.TryComputeTarget(
                levels, ifvgZones, level, price, tickSize, out var computedTarget, out var targetSource);

            var targetPrice = hasTarget
                ? computedTarget
                : level.IsBid
                    ? price + (this.FallbackTargetTicks * tickSize)
                    : price - (this.FallbackTargetTicks * tickSize);

            if (!hasTarget)
                targetSource = "fallback R:R";

            this.PlaceEntry(side, stopPrice, targetPrice, level, targetSource);
            return; // one qualifying setup per poll — never stack multiple entries from one pass
        }
    }

    /// <summary>Nearest OPPOSING resting level or IFVG zone ahead of price in the trade's own
    /// direction, past the minimum-distance floor — same `IsAhead`/min-distance/nearest-wins
    /// shape as `directionAbsorptionScalpStrategy.TryComputeTarget`, built from Finch-Lite's own
    /// two engines instead of that strategy's HH/LL/VWAP/prior-day levels.</summary>
    private bool TryComputeTarget(
        IReadOnlyList<RestingOrderEngine.RestingLevel> levels,
        IReadOnlyList<FairValueGapEngine.InverseFvgZone> ifvgZones,
        RestingOrderEngine.RestingLevel anchor, double price, double tickSize,
        out double targetPrice, out string source)
    {
        targetPrice = 0d;
        source = string.Empty;
        var isLong = anchor.IsBid;
        var minDistance = this.MinTargetDistanceTicks * tickSize;

        bool IsAhead(double candidate) => isLong ? candidate > price : candidate < price;

        var candidates = new List<(double Price, string Source)>();

        foreach (var level in levels)
        {
            if (level.IsBid == isLong) continue; // same side as the anchor — not an opposing level
            if (!IsAhead(level.Price)) continue;
            candidates.Add((level.Price, "opposing DOM/UA level"));
        }

        foreach (var zone in ifvgZones)
        {
            if (zone.IsBullish == isLong) continue; // same role as the anchor's own direction
            var edge = isLong ? zone.Bottom : zone.Top;
            if (!IsAhead(edge)) continue;
            candidates.Add((edge, "opposing IFVG zone"));
        }

        var qualified = candidates.Where(c => Math.Abs(c.Price - price) >= minDistance).ToList();
        if (qualified.Count == 0)
            return false;

        var nearest = qualified.OrderBy(c => Math.Abs(c.Price - price)).First();
        targetPrice = nearest.Price;
        source = nearest.Source;
        return true;
    }

    private bool IsInRth()
    {
        var est = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, SessionZone);
        return est.Hour >= this.RthStartHour && est.Hour < this.RthEndHour;
    }

    private void PlaceEntry(
        Side side, double stopPrice, double targetPrice, RestingOrderEngine.RestingLevel level, string targetSource)
    {
        this.waitOpenPosition = true;

        this.Log(
            $"[Signal] {side} anchor={(level.IsBid ? "BID" : "ASK")} {level.Price:0.####} "
            + $"absorbed={level.Absorbed:N0} unfinished={level.IsUnfinished} "
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
            StopLoss = SlTpHolder.CreateSL(stopPrice, PriceMeasurement.Absolute),
            TakeProfit = SlTpHolder.CreateTP(targetPrice, PriceMeasurement.Absolute),
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

    // ---- position isolation — same StrategyTag/Comment pattern as this repo's own reference ---

    private Position[] MyPositions() => Core.Instance.Positions
        .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount && x.Comment == StrategyTag)
        .ToArray();

    private Order[] MyOrders() => Core.Instance.Orders
        .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount && x.Comment == StrategyTag)
        .ToArray();

    private void Core_PositionAdded(Position obj)
    {
        if (obj.Comment != StrategyTag) return;
        if (obj.Symbol == this.CurrentSymbol && obj.Account == this.CurrentAccount)
            this.waitOpenPosition = false;
    }

    private void Core_PositionRemoved(Position obj)
    {
        if (obj.Comment != StrategyTag) return;

        if (!this.MyPositions().Any())
        {
            this.waitClosePositions = false;

            var equity = this.totalRealizedPnl;
            if (equity > this.peakEquity) this.peakEquity = equity;

            foreach (var order in this.MyOrders())
            {
                var r = order.Cancel();
                if (r.Status != TradingOperationResultStatus.Success)
                    this.Log($"[Order] failed to cancel leftover order: {r.Message}", StrategyLoggingLevel.Error);
            }
        }
    }

    private void Core_OrdersHistoryAdded(OrderHistory obj)
    {
        if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount || obj.Comment != StrategyTag) return;

        if (obj.Status == OrderStatus.Refused)
        {
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
        }
    }

    private void Core_TradeAdded(Trade obj)
    {
        if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount || obj.Comment != StrategyTag) return;

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
