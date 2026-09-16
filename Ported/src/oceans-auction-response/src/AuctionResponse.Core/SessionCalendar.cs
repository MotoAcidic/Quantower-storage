using System.Globalization;

namespace AuctionResponse.Core;

/// <summary>
/// Session eligibility and the minute offset used to pick a baseline bucket.
///
/// The session is anchored on the exchange definition supplied in configuration. The
/// bucket offset is measured in minutes FROM SESSION START, so it is timezone-agnostic:
/// a 30-minute segment is the same segment whichever zone names it.
///
/// Blackout intervals are user-supplied only. Nothing here claims to know about news.
/// </summary>
public sealed class SessionCalendar
{
    private readonly TimeZoneInfo _zone;
    private readonly TimeSpan _start;
    private readonly TimeSpan _endExclusive;
    private readonly List<(DateTime From, DateTime To)> _blackouts = new();

    public SessionCalendar(SessionConfig config)
    {
        TimezoneId = config.Timezone;
        _zone = ResolveZone(config.Timezone, config.WindowsTimezoneEquivalent);
        _start = TimeSpan.Parse(config.StartLocal, CultureInfo.InvariantCulture);
        _endExclusive = TimeSpan.Parse(config.EndLocalExclusive, CultureInfo.InvariantCulture);

        foreach (var raw in config.BlackoutIntervalsUtc)
        {
            var parts = raw.Split('/');
            if (parts.Length != 2) throw new FormatException("Blackout interval must be 'from/to' in UTC: " + raw);
            _blackouts.Add((
                DateTime.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTime.Parse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)));
        }
    }

    public string TimezoneId { get; }
    public TimeZoneInfo Zone => _zone;

    private static TimeZoneInfo ResolveZone(string ianaId, string windowsId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
    }

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    /// <summary>Eligible when inside the session window and outside every declared blackout.</summary>
    public bool IsEligible(DateTime utc, out string reason)
    {
        foreach (var (from, to) in _blackouts)
            if (utc >= from && utc < to) { reason = "inside a user-declared blackout interval"; return false; }

        var local = ToLocal(utc);
        var tod = local.TimeOfDay;
        if (tod < _start) { reason = "before session start"; return false; }
        if (tod >= _endExclusive) { reason = "after session end"; return false; }

        reason = "";
        return true;
    }

    /// <summary>Minutes elapsed since session start, or null outside the session.</summary>
    public int? MinutesFromStart(DateTime utc)
    {
        var local = ToLocal(utc);
        var tod = local.TimeOfDay;
        if (tod < _start || tod >= _endExclusive) return null;
        return (int)(tod - _start).TotalMinutes;
    }

    /// <summary>Stable session identifier: the local calendar date of the session start.</summary>
    public string SessionId(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Local wall-clock label, in the configured session zone. One zone, always.</summary>
    public string Label(DateTime utc) => ToLocal(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string LabelWithZone(DateTime utc) => Label(utc) + " " + Abbreviation(utc);

    public string Abbreviation(DateTime utc)
    {
        var isDst = _zone.IsDaylightSavingTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return TimezoneId switch
        {
            "America/Chicago" or "Central Standard Time" => isDst ? "CDT" : "CST",
            "America/New_York" or "Eastern Standard Time" => isDst ? "EDT" : "EST",
            _ => isDst ? _zone.DaylightName : _zone.StandardName
        };
    }
}
