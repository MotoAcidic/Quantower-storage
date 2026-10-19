using System;
using System.Collections.Generic;
using System.Drawing;

namespace SwingLevels;

/// <summary>One session's high/low, confirmed the instant the session window closes — extends
/// forward as an untested level until a later bar trades through it. Same "untested until touched"
/// semantics as <see cref="SwingLevel"/>; kept as a separate type because a session level also
/// carries which session produced it (for the label) and because its lifecycle (accumulate while
/// the window is open, finalize once it closes) is nothing like a fractal pivot.</summary>
internal sealed class SessionLevel
{
    public double Price { get; }
    public bool IsHigh { get; }
    public string SessionName { get; }
    public DateTime SinceUtc { get; }
    public DateTime? TouchedUtc { get; private set; }

    public SessionLevel(double price, bool isHigh, string sessionName, DateTime sinceUtc)
    {
        this.Price = price;
        this.IsHigh = isHigh;
        this.SessionName = sessionName;
        this.SinceUtc = sinceUtc;
    }

    public void MarkTouched(DateTime utc) => this.TouchedUtc ??= utc;
}

/// <summary>One configured session window (operator's own `untestedLevels.pine` ["Sessions Suite"]
/// defaults, all in ET per that script's own `tz = "America/New_York"`): Asia 18:00-00:00, London
/// 00:00-06:00, NY AM 06:00-12:00, NY PM 12:00-18:00. Deliberately NOT the same hours as
/// `srmAutoStrategy`'s own Asia/London/NY split (18:00-03:00/03:00-09:30/09:30-16:00) — that split
/// gates real-money order placement around the actual futures open; this one just reproduces the
/// pine script's own even quartering of the ET day for visual session ranges. Different purpose,
/// different numbers, both correct for their own job.</summary>
internal sealed class SessionDef
{
    public required string Name;
    public int StartHour;
    public int StartMinute;
    public int EndHour;
    public int EndMinute;
    public bool Enabled;
    public Color Color;
}

/// <summary>Live accumulator for one session's current (still-open) window — the box the overlay
/// draws while the session is running; finalized into two <see cref="SessionLevel"/>s the instant
/// the window closes.</summary>
internal sealed class SessionState
{
    public bool Active;
    public double High;
    public double Low;
    public DateTime HighTimeUtc;
    public DateTime LowTimeUtc;
    public DateTime StartTimeUtc;
}

/// <summary>
/// Session range tracker ported from the operator's own `TradingView/untestedLevels.pine`
/// ("Sessions Suite [claysul]") — their literal ask: "i have my untestedLevels.pine file that i use
/// to mark out untested session levels i want added into this as well with the settings options."
/// Scoped via AskUserQuestion to the session range boxes specifically (Asia/London/NY AM/NY PM),
/// not the file's prior-day/week/month H/L or opening-gap features.
///
/// Mechanics mirror the pine script exactly: while a session window is open, track its running
/// high/low (the live box); the instant the window closes, the high and low each become their own
/// untested level — extended forward, same as a confirmed swing — until a later bar's high/low
/// trades through that exact price, at which point it's "tested" (pine's own "mitigated"). Minute-
/// precision wraparound-safe window check (`IsInWindow`) is the same helper already used by
/// `srmAutoStrategy`/`oceansStackStrategy` for session gating, reimplemented fresh here per this
/// codebase's own established precedent of not sharing this exact helper across files.
///
/// Fed from its OWN bar stream (typically 5-minute), independent of the swing engine's own
/// (typically 1-hour) timeframe — a session's actual high/low can only be captured accurately at a
/// resolution finer than the window itself.
/// </summary>
internal sealed class SessionLevelEngine
{
    private readonly int maxLevelsKept;
    private readonly Dictionary<string, SessionState> states = new();

    public SessionLevelEngine(int maxLevelsKept)
    {
        this.maxLevelsKept = Math.Max(2, maxLevelsKept);
    }

    public List<SessionLevel> Levels { get; } = new();

    public IReadOnlyDictionary<string, SessionState> States => this.states;

    public void Reset()
    {
        this.states.Clear();
        this.Levels.Clear();
    }

    public void FeedBar(DateTime timeUtc, DateTime localEt, double high, double low, IReadOnlyList<SessionDef> defs)
    {
        foreach (var lvl in this.Levels)
        {
            if (lvl.TouchedUtc is null && high >= lvl.Price && low <= lvl.Price)
                lvl.MarkTouched(timeUtc);
        }

        foreach (var def in defs)
        {
            if (!def.Enabled) continue;

            if (!this.states.TryGetValue(def.Name, out var st))
            {
                st = new SessionState();
                this.states[def.Name] = st;
            }

            var inWindow = IsInWindow(localEt, def.StartHour, def.StartMinute, def.EndHour, def.EndMinute);

            if (inWindow && !st.Active)
            {
                st.Active = true;
                st.High = high;
                st.Low = low;
                st.HighTimeUtc = timeUtc;
                st.LowTimeUtc = timeUtc;
                st.StartTimeUtc = timeUtc;
            }
            else if (inWindow)
            {
                if (high > st.High) { st.High = high; st.HighTimeUtc = timeUtc; }
                if (low < st.Low) { st.Low = low; st.LowTimeUtc = timeUtc; }
            }
            else if (st.Active)
            {
                st.Active = false;
                this.Levels.Add(new SessionLevel(st.High, true, def.Name, st.HighTimeUtc));
                this.Levels.Add(new SessionLevel(st.Low, false, def.Name, st.LowTimeUtc));
            }
        }

        while (this.Levels.Count > this.maxLevelsKept)
            this.Levels.RemoveAt(0);
    }

    /// <summary>Same minute-precision, midnight-wraparound-safe window check already established
    /// elsewhere in this codebase (`srmAutoStrategy`, `oceansStackStrategy`) — reimplemented fresh
    /// here per this codebase's own convention of not sharing this exact helper across files.</summary>
    public static bool IsInWindow(DateTime local, int startHour, int startMinute, int endHour, int endMinute)
    {
        var start = (startHour * 60) + startMinute;
        var end = (endHour * 60) + endMinute;
        var now = (local.Hour * 60) + local.Minute;

        return start <= end
            ? now >= start && now < end
            : now >= start || now < end;
    }
}
