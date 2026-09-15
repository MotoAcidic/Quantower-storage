using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OrbIx.Core.Config;

/// <summary>
/// The account's loss-limit dollars, read from a pushed
/// <c>account-params.json</c> generated from the canonical
/// provenance-backed source (the research repository one prop firm-50k params) — never
/// typed here. An absent or malformed file is a stated status, and the
/// consumers that need these numbers show nothing rather than a guess.
/// </summary>
public sealed class AccountRuntimeParams
{
    private AccountRuntimeParams(
        bool available, string status,
        double dailyLossLimitUsd, double maximumLossLimitUsd, string source,
        int? maxMicros, bool dailyLossIsSoftPause, string maxLossBasis)
    {
        this.DailyLossIsSoftPause = dailyLossIsSoftPause;
        this.MaxLossBasis = maxLossBasis;
        this.Available = available;
        this.Status = status;
        this.DailyLossLimitUsd = dailyLossLimitUsd;
        this.MaximumLossLimitUsd = maximumLossLimitUsd;
        this.Source = source;
        this.MaxMicros = maxMicros;
    }

    public bool Available { get; }

    public string Status { get; }

    public double DailyLossLimitUsd { get; }

    public double MaximumLossLimitUsd { get; }

    public string Source { get; }

    /// <summary>
    /// The operator's own position cap in micros, or NULL when the file does not carry one.
    ///
    /// NULLABLE ON PURPOSE, and never defaulted. This is the operator's standing rule, not a
    /// firm limit — one prop firm's own maximum for this account is 5 contracts / 50 micros — so
    /// there is no safe number to fall back to. A consumer with no cap must say it has none
    /// rather than invent one, which is the same posture the loss limits already take.
    ///
    /// A non-positive value is refused for the same reason: a cap of zero would silence the
    /// rule it exists to enforce while looking configured.
    /// </summary>
    public int? MaxMicros { get; }

    /// <summary>
    /// Whether breaching the daily limit merely PAUSES new entries for the day, rather than
    /// ending the account.
    ///
    /// FALSE WHEN THE FIELD IS ABSENT, and that direction is deliberate. A soft pause is the
    /// weaker claim; asserting it where the firm never said so would describe a hard limit as
    /// survivable, to somebody sizing a position against the line on the chart. The firms
    /// genuinely differ here — one prop firm's daily limit ends the day's account, a second prop firm'
    /// Builder plan states "$1,000 - soft pause" — so this cannot be a constant.
    /// </summary>
    public bool DailyLossIsSoftPause { get; }

    /// <summary>
    /// What the maximum-loss limit is measured against, in the firm's own word — or an
    /// explicit statement that the firm did not say.
    ///
    /// Never blank: an empty string reads as "no basis", which is a different claim from
    /// "the source does not state one".
    /// </summary>
    public string MaxLossBasis { get; }

    /// <summary>Never throws; the failure is the status.</summary>
    public static AccountRuntimeParams Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AccountRuntimeParams(
                    false, $"account params: file absent ({Path.GetFileName(path)})",
                    0, 0, string.Empty, null, false, "unread");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            double dll = root.GetProperty("dailyLossLimitUsd").GetDouble();
            double mll = root.GetProperty("maximumLossLimitUsd").GetDouble();
            string source = root.TryGetProperty("source", out var s)
                ? s.GetString() ?? string.Empty : string.Empty;

            int? maxMicros = root.TryGetProperty("maxMicros", out var m)
                             && m.ValueKind == JsonValueKind.Number
                             && m.TryGetInt32(out var micros)
                             && micros > 0
                ? micros
                : null;

            if (dll <= 0 || mll <= 0)
            {
                return new AccountRuntimeParams(
                    false, "account params: non-positive limits refused",
                    0, 0, source, null, false, "unread");
            }

            var capText = maxMicros is { } cap
                ? $", cap {cap} micro(s)"
                : ", no position cap configured";

            var softPause = root.TryGetProperty("dailyLossIsSoftPause", out var sp)
                            && sp.ValueKind == JsonValueKind.True;

            // Never blank — see MaxLossBasis. A missing field is reported as missing.
            var basis = root.TryGetProperty("maxLossBasis", out var b)
                        && b.ValueKind == JsonValueKind.String
                        && b.GetString() is { Length: > 0 } stated
                ? stated
                : "basis not stated";

            return new AccountRuntimeParams(
                true,
                $"account params: DLL ${dll:N0}{(softPause ? " (soft pause)" : string.Empty)}, "
                + $"MLL ${mll:N0} ({basis}){capText}",
                dll, mll, source, maxMicros, softPause, basis);
        }
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException or KeyNotFoundException
            or InvalidOperationException)
        {
            return new AccountRuntimeParams(
                false, $"account params: unreadable ({ex.GetType().Name})",
                0, 0, string.Empty, null, false, "unread");
        }
    }
}

/// <summary>One symbol root's measured execution economics.</summary>
/// <param name="RoundTurnUsd">Measured round-turn fee; null = unmeasured, shown as such.</param>
/// <param name="OptimalRestSeconds">Measured rest-then-cross optimum; null = no trial produced one.</param>
/// <param name="Provenance">Which measurement each number came from.</param>
public sealed record SymbolEconomics(
    double? RoundTurnUsd, double? OptimalRestSeconds, string Provenance);

/// <summary>
/// Measured fees and execution guidance from a pushed <c>fees.json</c>
/// generated from the canonical measured sources (own-fill fee history,
/// the optimal-wait trials). Same posture as the account params: absent
/// or unmeasured is stated, never defaulted.
/// </summary>
public sealed class FeesRuntime
{
    private FeesRuntime(
        bool available, string status,
        IReadOnlyDictionary<string, SymbolEconomics> symbols)
    {
        this.Available = available;
        this.Status = status;
        this.Symbols = symbols;
    }

    public bool Available { get; }

    public string Status { get; }

    public IReadOnlyDictionary<string, SymbolEconomics> Symbols { get; }

    public static FeesRuntime Load(string path)
    {
        var empty = new Dictionary<string, SymbolEconomics>();
        try
        {
            if (!File.Exists(path))
            {
                return new FeesRuntime(
                    false, $"fees: file absent ({Path.GetFileName(path)})", empty);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var symbols = new Dictionary<string, SymbolEconomics>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in document.RootElement
                         .GetProperty("symbols").EnumerateObject())
            {
                double? fee = entry.Value.TryGetProperty("roundTurnUsd", out var f)
                              && f.ValueKind == JsonValueKind.Number
                    ? f.GetDouble() : null;
                double? rest = entry.Value.TryGetProperty("optimalRestSeconds", out var r)
                               && r.ValueKind == JsonValueKind.Number
                    ? r.GetDouble() : null;
                string provenance = entry.Value.TryGetProperty("provenance", out var p)
                    ? p.GetString() ?? string.Empty : string.Empty;
                symbols[entry.Name] = new SymbolEconomics(fee, rest, provenance);
            }

            return new FeesRuntime(
                true, $"fees: {symbols.Count} symbol(s) loaded", symbols);
        }
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException or KeyNotFoundException
            or InvalidOperationException)
        {
            return new FeesRuntime(
                false, $"fees: unreadable ({ex.GetType().Name})", empty);
        }
    }
}
