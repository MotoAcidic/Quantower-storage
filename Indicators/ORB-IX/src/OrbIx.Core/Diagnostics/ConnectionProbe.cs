using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OrbIx.Core.Diagnostics;

/// <summary>
/// One field the broker publishes about an account, reduced to text.
/// </summary>
/// <param name="Id">The platform's key for the field.</param>
/// <param name="NameKey">Its display key, which is often the only human-readable part.</param>
/// <param name="Value">
/// Rendered to text HERE rather than carried as <c>object</c>. The probe's whole purpose is
/// to find out what these fields ARE; a typed model would have to assume that first.
/// </param>
public readonly record struct ProbeField(string Id, string NameKey, string Value);

/// <summary>
/// One account as the platform is describing it, reduced to what the probe records.
///
/// A PROJECTION, NOT THE PLATFORM TYPE — the same rule <see cref="Features.SymbolCandidate"/>
/// follows, and for the same reason: OrbIx.Core has no reference to the trading platform, so
/// the shell reads <c>Core.Accounts</c> and fills these in.
///
/// IDENTITY IS FINGERPRINTED, NEVER WRITTEN. On these brokers the account name is the
/// account holder's surname and the id is a live account number; this file exists to
/// answer "does the balance move" and "what fields does the broker publish", and neither
/// question needs to know whose account it is. <see cref="ConnectionProbe.Fingerprint"/>
/// gives a stable short hash instead, so two samples of the same account still line up
/// while the file carries nothing that identifies anyone.
/// </summary>
public readonly record struct AccountProbe(
    string AccountId,
    string AccountName,
    string ConnectionId,
    string ConnectionName,
    double Balance,
    string Currency,
    IReadOnlyList<ProbeField> AdditionalInfo);

/// <summary>
/// The research dump behind Wave 0: what do these connections actually tell us about the
/// account we are trading?
///
/// WHY IT EXISTS. The chart's daily-loss line is computed from a STATIC configured limit and
/// prints "assumes $0 realized today" — so on an evaluation account it drifts further from
/// the truth with every losing trade, in the direction that gets an account failed. The
/// Strategy already derives the real figure from <c>Account.Balance</c> minus a start-up
/// snapshot; the Indicator does not read the account at all.
///
/// Before that arithmetic is moved onto the chart, TWO THINGS MUST BE MEASURED rather than
/// assumed, and neither is answerable by reading code:
///
///   1. Does Balance move intraday on these connections, and does it move on REALISED
///      trades only? `balance - start` double-counts if it already includes open profit.
///   2. What is in AdditionalInfo? A prop firm publishing its own daily-loss and trailing
///      figures there would beat our hand-maintained account-params.json outright.
///
/// THIS TYPE DECIDES NOTHING AND DRAWS NOTHING. It renders observations to NDJSON so a
/// session can be read back afterwards. Every conclusion comes from the file, not from here.
/// </summary>
public static class ConnectionProbe
{
    /// <summary>
    /// One NDJSON line describing one account at one instant.
    ///
    /// Hand-rolled rather than serialized through a library because OrbIx.Core takes no
    /// serializer dependency, and because the escaping needed here is small and testable.
    /// </summary>
    public static string AccountLine(DateTime atUtc, AccountProbe account)
    {
        var text = new StringBuilder();

        text.Append("{\"atUtc\":\"")
            .Append(atUtc.ToString("o", CultureInfo.InvariantCulture))
            .Append("\",\"kind\":\"account\",\"account\":")
            .Append(Quote(Fingerprint(account.AccountId, account.AccountName)))
            .Append(",\"connectionId\":").Append(Quote(account.ConnectionId))
            .Append(",\"connectionName\":").Append(Quote(account.ConnectionName))
            .Append(",\"balance\":")
            .Append(account.Balance.ToString("R", CultureInfo.InvariantCulture))
            .Append(",\"currency\":").Append(Quote(account.Currency))
            .Append(",\"additionalInfo\":[");

        for (var i = 0; i < account.AdditionalInfo.Count; i++)
        {
            var field = account.AdditionalInfo[i];

            if (i > 0)
                text.Append(',');

            text.Append("{\"id\":").Append(Quote(field.Id))
                .Append(",\"nameKey\":").Append(Quote(field.NameKey))
                .Append(",\"value\":").Append(Quote(field.Value)).Append('}');
        }

        return text.Append("]}").ToString();
    }

    /// <summary>
    /// One line describing a connection's identity and what it was observed to serve.
    ///
    /// A connection is NOT fingerprinted the way an account is: its name is the broker's
    /// ("one connection", "a second prop firm"), which is the very thing this file exists to
    /// distinguish, and it identifies a venue rather than a person.
    /// </summary>
    /// <param name="atUtc">When the observation was taken.</param>
    /// <param name="connectionId">The platform's opaque identifier for the connection.</param>
    /// <param name="connectionName">The broker's own name for it, as shown to the operator.</param>
    /// <param name="state">The platform's own word for the connection state.</param>
    /// <param name="servesTickHistory">
    /// Measured, not declared — the same capability check the profile source already uses.
    /// </param>
    /// <param name="symbolsOffered">
    /// How many symbols this connection was offering when sampled.0 early in a session is
    /// a known transient, not an absence, so the count is recorded rather than judged.
    /// </param>
    public static string ConnectionLine(
        DateTime atUtc, string connectionId, string connectionName, string state,
        bool servesTickHistory, int symbolsOffered)
        => "{\"atUtc\":\"" + atUtc.ToString("o", CultureInfo.InvariantCulture)
           + "\",\"kind\":\"connection\",\"connectionId\":" + Quote(connectionId)
           + ",\"connectionName\":" + Quote(connectionName)
           + ",\"state\":" + Quote(state)
           + ",\"servesTickHistory\":" + (servesTickHistory ? "true" : "false")
           + ",\"symbolsOffered\":" + symbolsOffered.ToString(CultureInfo.InvariantCulture)
           + "}";

    /// <summary>
    /// What changed in an account between two observations, in words.
    ///
    /// THE QUESTION THE PROBE EXISTS TO ANSWER, computed rather than eyeballed from a file
    /// of thousands of lines. A balance that never moves all session answers it as loudly as
    /// one that moves on every fill, and neither is visible without differencing.
    /// </summary>
    public static string DescribeBalanceMovement(IReadOnlyList<AccountProbe> samples)
    {
        if (samples.Count == 0)
            return "no samples taken";

        var balances = samples.Select(s => s.Balance).ToList();
        var first = balances[0];
        var moves = balances.Where(b => b != first).ToList();

        if (moves.Count == 0)
        {
            return $"balance held at {first.ToString("N2", CultureInfo.InvariantCulture)} "
                + $"across {samples.Count} sample(s) — no intraday movement observed";
        }

        var low = balances.Min();
        var high = balances.Max();
        var last = balances[^1];

        return $"balance moved {moves.Count} time(s) across {samples.Count} sample(s): "
            + $"opened {first.ToString("N2", CultureInfo.InvariantCulture)}, "
            + $"range {low.ToString("N2", CultureInfo.InvariantCulture)}"
            + $"–{high.ToString("N2", CultureInfo.InvariantCulture)}, "
            + $"last {last.ToString("N2", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// A stable, short, non-reversible label for one account.
    ///
    /// SHA-256 over id and name, truncated to 12 hex characters. Stable across samples and
    /// across sessions, so the same account is recognisably the same account in the file —
    /// and reveals neither the account number nor the holder's surname, which is what these
    /// two fields actually contain on the brokers in use.
    ///
    /// Truncation is not a weakness here: this labels at most a handful of accounts on one
    /// machine, and the property required is distinctness within that file, not resistance
    /// to a search for collisions.
    /// </summary>
    public static string Fingerprint(string? accountId, string? accountName)
    {
        var material = Encoding.UTF8.GetBytes(
            (accountId ?? string.Empty) + "\u0000" + (accountName ?? string.Empty));

        return Convert.ToHexString(SHA256.HashData(material))[..12].ToLowerInvariant();
    }

    /// <summary>
    /// JSON string escaping, covering what a broker's free-text field can actually contain.
    ///
    /// Control characters are escaped to their four-hex-digit form rather than dropped: a
    /// field that arrives
    /// with an embedded newline would otherwise split one NDJSON record into two, and the
    /// reader would see a truncated object followed by a line that parses as nothing.
    /// </summary>
    private static string Quote(string? raw)
    {
        if (raw is null)
            return "null";

        var text = new StringBuilder("\"");

        foreach (var c in raw)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < ' ')
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        text.Append(c);
                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
