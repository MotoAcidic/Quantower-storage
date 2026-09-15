using System;

namespace OrbIx.Core.Flow;

/// <summary>
/// Where session-cumulative statistics reset. ATAS Cluster Statistics (article
/// 72000602624) offers three modes: None ("cumulative values are calculated from the
/// beginning of the loaded chart history"), Default Session ("reset at the default session
/// boundary") and Custom Session ("reset at a user-defined session start time").
///
/// The DEFAULT session is the platform's own: the shell wraps Quantower's per-symbol
/// sessions container, so the boundary is whatever the platform says it is for that
/// instrument rather than a number recalled here. The CUSTOM session is a wall-clock time
/// in a named time zone.
/// </summary>
public interface ISessionBoundary
{
    /// <summary>
    /// The instant the session containing <paramref name="utc"/> began. Two instants in
    /// the same session return the same value; that equality is the whole contract.
    /// </summary>
    DateTime SessionStartUtc(DateTime utc);
}

/// <summary>ATAS "None": one session spanning the whole loaded history.</summary>
public sealed class NoSessionBoundary : ISessionBoundary
{
    public static readonly NoSessionBoundary Instance = new();

    public DateTime SessionStartUtc(DateTime utc) => DateTime.MinValue;
}

/// <summary>
/// ATAS "Custom Session": a fixed wall-clock start in a time zone, once per calendar day
/// of that zone. Daylight-saving transitions are handled by the zone, not by arithmetic.
/// </summary>
public sealed class DailySessionBoundary : ISessionBoundary
{
    private readonly TimeOnly startLocal;
    private readonly TimeZoneInfo zone;

    public DailySessionBoundary(TimeOnly startLocal, TimeZoneInfo zone)
    {
        this.startLocal = startLocal;
        this.zone = zone ?? throw new ArgumentNullException(nameof(zone));
    }

    public TimeOnly StartLocal => this.startLocal;

    public TimeZoneInfo Zone => this.zone;

    public DateTime SessionStartUtc(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, this.zone);
        var candidateLocal = local.Date.Add(this.startLocal.ToTimeSpan());

        if (candidateLocal > local)
            candidateLocal = candidateLocal.AddDays(-1);

        return ToUtc(candidateLocal);
    }

    private DateTime ToUtc(DateTime local)
    {
        // A start time that falls in a DST gap does not exist on that day; the zone's own
        // adjustment rule decides, and the first valid instant after the gap is used.
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (this.zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, this.zone);
    }
}

/// <summary>
/// A boundary supplied by the host platform's own session data. The shell passes a
/// function so this assembly stays free of platform types; the function answers the same
/// question as <see cref="ISessionBoundary.SessionStartUtc"/>.
/// </summary>
public sealed class DelegateSessionBoundary : ISessionBoundary
{
    private readonly Func<DateTime, DateTime> resolver;

    public DelegateSessionBoundary(Func<DateTime, DateTime> resolver)
        => this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public DateTime SessionStartUtc(DateTime utc) => this.resolver(utc);
}
