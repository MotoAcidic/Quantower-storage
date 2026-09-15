using System;

namespace OrbIx.Core.Abstractions;

/// <summary>
/// Turns whatever a data vendor calls a contract into the root this engine configures.
///
/// THE SAME INSTRUMENT ARRIVES UNDER AT LEAST FOUR NAMES, all of them observed on this
/// machine rather than imagined:
///
///   MNQU6           one data vendor's history files and the capture recorder
///   MNQU26          Quantower, on a plain connection
///   /MNQU26:XCME    Quantower against My Funded Futures — a LEADING SLASH and an exchange
///   /MNQ:XCME       what Symbol.Root returns on that same connection
///
/// OBSERVED 2026-08-23 22:03Z and for the hour after: the broker's symbols changed shape when
/// the feed reconnected at the Sunday open, `Symbol.Root` began returning "/MNQ:XCME", and the
/// product lookup — which matches against the configured "MNQ" — stopped matching anything. The
/// indicator went Fatal on twelve consecutive loads and never subscribed, so a fully wired
/// engine sat on a live chart and evaluated nothing. Nothing was broken except the name.
///
/// Configuration is keyed by the bare root because that is what a person writes. Normalising
/// here means a new vendor prefix costs one test rather than a silent day of Fatal loads.
/// </summary>
public static class ContractRoot
{
    /// <summary>
    /// Strips vendor decoration from a symbol or root, leaving the bare text.
    ///
    /// Removes a leading slash and everything from the first colon, because those are the two
    /// decorations observed. It does NOT try to be clever about anything else: a name this does
    /// not recognise is returned as it arrived, so an unmatched product reports the real string
    /// the platform supplied and stays diagnosable.
    /// </summary>
    public static string Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();

        // The exchange qualifier. Taken from the FIRST colon: everything after it identifies
        // the venue, never the product.
        var colon = text.IndexOf(':');

        if (colon >= 0)
            text = text[..colon];

        return text.TrimStart('/').Trim();
    }

    /// <summary>
    /// The product root behind a full contract name — <c>/MNQU26:XCME</c> becomes <c>MNQ</c>.
    ///
    /// Removes the expiry: the trailing year digits, then the single month letter in front of
    /// them. THE MONTH LETTER IS ONLY DROPPED WHEN DIGITS WERE ACTUALLY FOUND, because a root
    /// supplied without an expiry — which is what <c>Symbol.Root</c> gives — would otherwise
    /// lose its last real character and turn MNQ into MN.
    /// </summary>
    public static string FromContractName(string? raw)
    {
        var text = Normalise(raw);

        if (text.Length == 0)
            return string.Empty;

        var withoutYear = text.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        if (withoutYear.Length == text.Length)
            return text;

        return withoutYear.Length > 1 ? withoutYear[..^1] : withoutYear;
    }
}
