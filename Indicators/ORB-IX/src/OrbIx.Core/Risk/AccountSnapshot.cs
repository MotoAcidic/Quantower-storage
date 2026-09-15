using System;

namespace OrbIx.Core.Risk;

/// <summary>
/// The day's money, derived from what the platform supplies.
/// </summary>
/// <param name="Realised">
/// Profit banked today. NULL means "cannot tell yet" — never zero. The difference is the
/// whole point of this type: a daily-loss guard fed a confident zero when it should have
/// been fed nothing will let an account keep trading past its limit.
/// </param>
/// <param name="Open">Unrealised profit on positions currently held.</param>
/// <param name="Equity">Balance plus what is open, or null when either is unknown.</param>
public readonly record struct AccountReading(double? Realised, double? Open, double? Equity);

/// <summary>
/// Turns a platform balance and open profit into the day's realised, open and equity.
///
/// WHY THIS IS SHARED. The Strategy has carried this arithmetic since it was written, and
/// carried it correctly. The INDICATOR — the thing actually on screen while a position is
/// held — has never read the account at all: its daily-loss line is computed from the
/// STATIC configured limit and prints "assumes $0 realized today". That assumption is true
/// at the open and further from the truth after every losing trade, in the direction that
/// puts the drawn line further away than where the day really ends.
///
/// Two hosts computing a trader's loss limit from two implementations is a defect waiting
/// to happen, so there is one implementation and both call it.
///
/// NO PLATFORM TYPES HERE (§11). The shells read <c>Account.Balance</c> and
/// <c>Position.NetPnL.Value</c> and pass doubles in, which is what makes the arithmetic
/// testable against a roll-over, a missing snapshot, or a broker that reports nothing.
/// </summary>
public static class AccountSnapshot
{
    /// <summary>
    /// Derives the day's figures.
    ///
    /// REALISED IS A DIFFERENCE, NOT A RELABELLING. <c>Account.Balance</c> is documented by
    /// the vendor as "current balance of the account" — a balance, not a day's profit — so
    /// the day's realised figure is the balance now minus the balance when the session
    /// started. That is a real measurement; calling the balance itself "profit" would not be.
    ///
    /// WITHOUT A STARTING SNAPSHOT, REALISED IS NULL AND STAYS NULL. Callers must treat
    /// that as "block and say so", never as zero — <see cref="KillSwitches"/> already reads
    /// a null observation as <c>Unknown</c> and blocks, which is the behaviour this
    /// preserves.
    /// </summary>
    /// <param name="balance">Current account balance, or null when unavailable.</param>
    /// <param name="startingBalance">
    /// Balance captured when this session began. Null until it has been taken.
    /// </param>
    /// <param name="openPnl">
    /// Unrealised profit across open positions, net of fees, or null when it could not be
    /// read. Zero and null are DIFFERENT: zero means flat, null means unknown.
    /// </param>
    public static AccountReading Derive(double? balance, double? startingBalance, double? openPnl)
    {
        double? realised = balance is { } now && startingBalance is { } start
            ? now - start
            : null;

        double? equity = balance is { } cash && openPnl is { } unrealised
            ? cash + unrealised
            : balance;

        return new AccountReading(realised, openPnl, equity);
    }

    /// <summary>
    /// What the operator is told about the figures behind the risk lines.
    ///
    /// The chart previously said "assumes $0 realized today" whether or not that was true.
    /// It now says which of the two situations it is in, because a drawn loss limit that
    /// cannot be trusted must announce itself rather than look identical to one that can.
    /// </summary>
    public static string Describe(in AccountReading reading)
    {
        if (reading.Realised is not { } realised)
            return "realised P&L unknown — no starting balance yet";

        var open = reading.Open is { } o
            ? $", open {o:C}"
            : string.Empty;

        return $"realised {realised:C} today{open}";
    }

    // WHAT IS DELIBERATELY NOT HERE: "how much of the daily loss limit is left".
    //
    // It needs a rule nobody has written down, and the first version of this file invented
    // one — that intraday profit does not enlarge the allowance. That is CONTRADICTED by
    // this project's own precedent: phase1/one prop firm_account.py computes
    // `today_net = today_realized + unrealized` with no floor at zero, so profit does
    // offset within the day there.
    //
    // And that precedent itself rests on a documented gap. docs/prop-rules/one prop firm.md
    // records it verbatim: "GAP, flagged: whether the DLL counts unrealized P&L is not
    // stated (the article says only 'Net P&L')". The account actually configured here is
    // a second firm's evaluation account, whose rules file does not establish a daily-loss structure at all.
    //
    // So the remaining-allowance figure is not derivable from anything measured. It belongs
    // where the prop rules are READ — against the machine-readable params file those docs
    // require code to load — not invented in a display helper. Until that is settled, this
    // type reports what the platform actually said and nothing more.
}
