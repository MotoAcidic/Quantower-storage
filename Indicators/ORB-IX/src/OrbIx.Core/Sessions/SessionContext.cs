using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Risk;

namespace OrbIx.Core.Sessions;

/// <summary>
/// Contract specifications for one tradeable instrument.
///
/// These are read from the trading platform at runtime, never compiled in. A tick value
/// baked into source is wrong the moment an exchange lists a new contract size, and this
/// system is expected to pick up a nano tier the day it starts trading without a rebuild.
/// </summary>
/// <param name="SymbolId">Platform identifier for the specific contract.</param>
/// <param name="Root">Product root the contract belongs to, such as "NQ".</param>
/// <param name="Tier">Contract tier name as configured, such as "MNQ".</param>
/// <param name="TickSize">Minimum price increment.</param>
/// <param name="TickValue">Currency value of one tick for one contract.</param>
public readonly record struct InstrumentSpec(
    string SymbolId,
    string Root,
    string Tier,
    double TickSize,
    double TickValue)
{
    /// <summary>Converts a price distance to whole ticks, rounded toward zero.</summary>
    public int TicksBetween(double from, double to)
        => this.TickSize > 0 ? (int)Math.Truncate(Math.Abs(to - from) / this.TickSize) : 0;

    /// <summary>Currency risk of holding one contract across a distance in ticks.</summary>
    public double RiskPerContract(int ticks) => Math.Abs(ticks) * this.TickValue;

    /// <summary>Rounds a price to the instrument's grid.</summary>
    public double RoundToTick(double price)
        => this.TickSize > 0 ? Math.Round(price / this.TickSize, MidpointRounding.AwayFromZero) * this.TickSize : price;

    /// <summary>
    /// The price grid alone is known: this instrument can be measured in ticks.
    ///
    /// WHY THIS IS SEPARATE FROM <see cref="IsUsable"/>. The two numbers are needed by
    /// different code and do not always arrive together. <see cref="TickValue"/> requires a
    /// reference PRICE — the platform prices a tick from a live quote or a historical close —
    /// so on a cold start, a closed market, or a stalled history feed it stays 0 long after
    /// the grid is known.
    ///
    /// MEASURED CORRECTION, 2026-08-31. An earlier draft of this comment claimed the tick size
    /// is "populated on the first read". It is not: the startup log shows `tick size NaN` at
    /// attempt 32 and a grid that took roughly fifty one-second attempts to appear. So the two
    /// numbers are not reliably ordered, and this split is not a claim that one always precedes
    /// the other — it is only a claim that code needing dollars and code needing ticks should
    /// not wait on each other. When the GRID is missing, everything price-domain genuinely must
    /// wait, and it does.
    ///
    /// Requiring both to do anything cost the operator three minutes of blank chart on
    /// 2026-08-31: 178 one-second attempts during which HH/LL, delta, zones, VWAP, the
    /// session ranges and BOTH volume profiles were withheld — none of which converts a
    /// tick to money, and none of which reads <see cref="TickValue"/> at all.
    ///
    /// So the guard is split by what the caller actually consumes. Price-domain code asks
    /// for this. Money-domain code — currency risk, fees — asks for <see cref="IsUsable"/>
    /// and waits, saying so. Nothing invents a cost it does not have.
    /// </summary>
    public bool HasPriceScale => this.TickSize > 0;

    /// <summary>
    /// Both numbers are known, so a tick can be converted to money.
    ///
    /// DELIBERATELY UNCHANGED. Every currency calculation still demands this; the split
    /// above adds a weaker predicate for price-domain callers rather than weakening this
    /// one. Widening it would let a fee or a drawdown line quietly compute against a zero.
    /// </summary>
    public bool IsUsable => this.HasPriceScale && this.TickValue > 0;

    /// <summary>
    /// Refuses construction when the price grid is unknown.
    ///
    /// ONE PLACE, BECAUSE IT WAS FOUR. This exact guard stood inline and identical in
    /// LevelGraph, FootprintEngine, RetestEngine and MicroQuality — all four demanding
    /// <see cref="IsUsable"/> while referencing <see cref="TickValue"/> zero times between
    /// them. Four copies is how they came to ask for the wrong number in unison, and how a
    /// correction would have had to be made four times to stick.
    /// </summary>
    /// <param name="paramName">The constructor parameter being validated.</param>
    /// <exception cref="ArgumentException">The tick size is unknown.</exception>
    public void RequirePriceScale(string paramName)
    {
        if (!this.HasPriceScale)
        {
            throw new ArgumentException(
                "Instrument specifications must carry a tick size.", paramName);
        }
    }
}

/// <summary>
/// Everything a module or playbook needs to know about the session it is looking at.
///
/// Mutated only by the engine on the fold thread, and handed to modules as a read-only
/// view. Nothing here is read from a market-data handler.
/// </summary>
public sealed class SessionContext
{
    public SessionContext(
        SessionWindow window,
        InstrumentSpec instrument,
        SymbolConfig symbolConfig)
    {
        this.Window = window ?? throw new ArgumentNullException(nameof(window));
        this.Instrument = instrument;
        this.SymbolConfig = symbolConfig ?? throw new ArgumentNullException(nameof(symbolConfig));

        if (!instrument.IsUsable)
        {
            throw new ArgumentException(
                $"Instrument '{instrument.SymbolId}' has tick size {instrument.TickSize} and tick value "
                + $"{instrument.TickValue}; both must be positive and are read from the platform, not assumed.",
                nameof(instrument));
        }
    }

    public SessionWindow Window { get; }

    public InstrumentSpec Instrument { get; }

    public SymbolConfig SymbolConfig { get; }

    public string SessionName => this.Window.Definition.Name;

    public string SymbolRoot => this.Instrument.Root;

    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;

    /// <summary>
    /// The closed opening range, or null while it is still building. Downstream code that
    /// needs a range must handle null rather than assume the range exists: during
    /// <see cref="SessionPhase.OrForming"/> it genuinely does not.
    /// </summary>
    public OrSnapshot? Range { get; private set; }

    /// <summary>Entries this session has produced, against its budget.</summary>
    public int EntriesTaken { get; private set; }

    /// <summary>Consecutive losses in this session, which drive the cooldown rule.</summary>
    public int ConsecutiveLosses { get; private set; }

    /// <summary>When the cooldown expires, or null when none is running.</summary>
    public DateTime? CooldownUntilUtc { get; private set; }

    /// <summary>Which data tiers are actually delivering, set by the engine from the live feed.</summary>
    public DataTierAvailability Tiers { get; private set; } = DataTierAvailability.None;

    /// <summary>
    /// Whether this session may still produce an entry, and why not when it may not.
    /// </summary>
    /// <summary>
    /// The account- and feed-level guards, or null when none are attached.
    ///
    /// Held here so that <see cref="CanEnter"/> — the one gate every entry path already goes
    /// through — consults them by construction. Putting the check anywhere else would mean a
    /// new entry path could be added without one.
    /// </summary>
    public KillSwitches? Kill { get; private set; }

    /// <summary>The most recent kill-switch readings, for the panel and the journal.</summary>
    public IReadOnlyList<KillReading> KillReadings { get; private set; } = Array.Empty<KillReading>();

    /// <summary>Attaches the guards. Called once when the session context is built.</summary>
    public void UseKillSwitches(KillSwitches switches)
        => this.Kill = switches ?? throw new ArgumentNullException(nameof(switches));

    /// <summary>
    /// Re-evaluates the guards against fresh observations and keeps the result.
    /// </summary>
    public IReadOnlyList<KillReading> EvaluateKill(in KillObservations observed)
    {
        this.KillReadings = this.Kill is null
            ? Array.Empty<KillReading>()
            : this.Kill.Evaluate(observed);

        return this.KillReadings;
    }

    public bool CanEnter(DateTime utcNow, out string reason)
    {
        // The account-level guards come FIRST. A session can be perfectly armed with a range,
        // a budget and no cooldown while the account is past its daily loss — and in that
        // order the reason reported is the one that actually matters.
        if (this.Kill is { } kill && kill.Blocks(this.KillReadings, out var killReason))
        {
            reason = "Kill switch: " + killReason;
            return false;
        }

        if (!this.Window.Definition.EntriesAllowed)
        {
            reason = $"{this.SessionName} does not permit entries; it supplies context only.";
            return false;
        }

        if (this.Phase != SessionPhase.Armed)
        {
            reason = $"{this.SessionName} is {this.Phase}, not Armed.";
            return false;
        }

        if (this.Range is null)
        {
            reason = "The opening range has not closed.";
            return false;
        }

        if (this.Window.Definition.Budget is { } budget && this.EntriesTaken >= budget)
        {
            reason = $"{this.SessionName} trade budget exhausted ({this.EntriesTaken} of {budget}).";
            return false;
        }

        if (this.CooldownUntilUtc is { } until && utcNow < until)
        {
            reason = $"Cooldown active until {until:HH:mm:ss} UTC after consecutive losses.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // The mutators below are the engine's, not a module's. They are public rather than
    // internal because the platform adapter that drives them lives in a different assembly:
    // OrbIx.Core deliberately has no reference to Quantower, so the adapter is on the other
    // side of an assembly boundary and "internal" would have made this type undrivable.
    // Modules receive the context read-only and must not call these.

    /// <summary>Engine only. Advances the session phase.</summary>
    public void SetPhase(SessionPhase phase) => this.Phase = phase;

    /// <summary>Engine only. Publishes the closed opening range.</summary>
    public void SetRange(OrSnapshot range) => this.Range = range ?? throw new ArgumentNullException(nameof(range));

    /// <summary>Engine only. Records which data tiers are delivering.</summary>
    public void SetTiers(DataTierAvailability tiers) => this.Tiers = tiers;

    /// <summary>Engine only. Counts an entry against the session budget.</summary>
    public void RecordEntry() => this.EntriesTaken++;

    /// <summary>Engine only. Records a closed trade's outcome and applies the cooldown rule.</summary>
    public void RecordResult(bool wasLoss, DateTime utcNow, RiskConfig risk)
    {
        if (risk is null)
            throw new ArgumentNullException(nameof(risk));

        if (!wasLoss)
        {
            this.ConsecutiveLosses = 0;
            return;
        }

        this.ConsecutiveLosses++;

        if (this.ConsecutiveLosses >= 2)
            this.CooldownUntilUtc = utcNow.AddMinutes(risk.CooldownMinAfterTwoLosses);
    }

    /// <summary>Engine only. Clears per-session state at a session boundary.</summary>
    public void ResetForNewSession()
    {
        this.Phase = SessionPhase.Idle;
        this.Range = null;
        this.EntriesTaken = 0;
        this.ConsecutiveLosses = 0;
        this.CooldownUntilUtc = null;
    }
}

/// <summary>
/// Which data tiers are delivering. A module whose tier is absent is disabled and its
/// weight redistributed; it is never fed a substituted value.
/// </summary>
public readonly record struct DataTierAvailability(
    bool Bars, bool Quotes, bool ClassifiedTrades, bool Depth, bool OrderLevel, bool Options)
{
    public static DataTierAvailability None => new(false, false, false, false, false, false);

    public bool Has(DataTier tier) => tier switch
    {
        DataTier.T0 => this.Bars,
        DataTier.T1 => this.Quotes,
        DataTier.T2 => this.ClassifiedTrades,
        DataTier.T3 => this.Depth,
        DataTier.T4 => this.OrderLevel,
        DataTier.T5 => this.Options,
        _ => false,
    };

    /// <summary>The highest contiguous tier available from T0 upward.</summary>
    public DataTier Highest
    {
        get
        {
            if (!this.Bars) return DataTier.T0;
            if (!this.Quotes) return DataTier.T0;
            if (!this.ClassifiedTrades) return DataTier.T1;
            if (!this.Depth) return DataTier.T2;
            if (!this.OrderLevel) return DataTier.T3;
            return this.Options ? DataTier.T5 : DataTier.T4;
        }
    }

    public bool Meets(DataTier required) => this.Highest >= required;
}
