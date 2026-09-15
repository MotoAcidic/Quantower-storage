using System;
using System.Collections.Generic;
using System.Linq;
using OrbIx.Core.Config;

namespace OrbIx.Core.Sessions;

/// <summary>
/// How an opening range is closed.
/// </summary>
public enum OrLengthKind
{
    /// <summary>A fixed wall-clock duration.</summary>
    Fixed,

    /// <summary>
    /// Closed by participation rather than the clock: whichever of the volume threshold,
    /// the hard time cap, or the spent-range condition arrives first.
    /// </summary>
    Adaptive,
}

/// <summary>
/// One configured session, resolved from configuration into the form the clock uses.
/// Immutable, and validated at construction so an impossible window cannot exist.
/// </summary>
public sealed class SessionDefinition
{
    public SessionDefinition(
        string name,
        TimeSpan openLocalTime,
        OrLengthKind orKind,
        TimeSpan orLength,
        bool enabled,
        int? budget,
        bool entriesAllowed,
        IReadOnlyList<string> symbolRoots)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A session must be named.", nameof(name));

        if (openLocalTime < TimeSpan.Zero || openLocalTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(openLocalTime), openLocalTime, "A session opens at a time of day.");
        }

        if (orKind == OrLengthKind.Fixed && orLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orLength), orLength, "A fixed opening range must have a positive length.");
        }

        this.Name = name;
        this.OpenLocalTime = openLocalTime;
        this.OrKind = orKind;
        this.OrLength = orLength;
        this.Enabled = enabled;
        this.Budget = budget;
        this.EntriesAllowed = entriesAllowed;
        this.SymbolRoots = symbolRoots ?? throw new ArgumentNullException(nameof(symbolRoots));
    }

    public string Name { get; }

    /// <summary>Open as a wall-clock time in the configured session time zone.</summary>
    public TimeSpan OpenLocalTime { get; }

    public OrLengthKind OrKind { get; }

    /// <summary>
    /// Fixed range length. For an adaptive range this is the hard time cap, taken from
    /// <see cref="AdaptiveOrConfig.MaxMin"/>.
    /// </summary>
    public TimeSpan OrLength { get; }

    public bool Enabled { get; }

    /// <summary>Maximum entries permitted. Null means the session imposes no count limit.</summary>
    public int? Budget { get; }

    /// <summary>
    /// Whether the session may produce entries at all. The initial-balance window supplies
    /// extension targets and a day-type read without ever being traded.
    /// </summary>
    public bool EntriesAllowed { get; }

    /// <summary>Product roots this session applies to. Empty means every configured product.</summary>
    public IReadOnlyList<string> SymbolRoots { get; }

    public bool AppliesTo(string symbolRoot)
        => this.SymbolRoots.Count == 0
           || this.SymbolRoots.Contains(symbolRoot, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves every configured session — the eight named windows and any custom ones —
    /// into definitions, ordered by open time so the clock can reason about adjacency.
    /// </summary>
    public static IReadOnlyList<SessionDefinition> ResolveAll(OrbIxConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        var resolved = new List<SessionDefinition>();

        foreach (var (name, session) in config.Sessions.Named)
            resolved.Add(Resolve(name, session, config));

        foreach (var custom in config.Sessions.Custom)
            resolved.Add(Resolve(custom.Name, custom, config));

        return resolved
            .OrderBy(s => s.OpenLocalTime)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static SessionDefinition Resolve(string name, SessionConfig session, OrbIxConfig config)
    {
        if (!OrbIxConfigLoader.TryParseWallClock(session.Open, out var open))
        {
            throw new ArgumentException(
                $"Session '{name}' has open time '{session.Open}', which is not HH:mm.", nameof(session));
        }

        if (OrbIxConfigLoader.IsAdaptive(session.Or))
        {
            return new SessionDefinition(
                name, open, OrLengthKind.Adaptive,
                TimeSpan.FromMinutes(config.Or.Adaptive.MaxMin),
                session.Enabled, session.Budget, session.EntriesAllowed, session.Symbols);
        }

        if (!Duration.TryParse(session.Or, out var length))
        {
            throw new ArgumentException(
                $"Session '{name}' has opening range '{session.Or}', which is neither a duration nor 'adaptive'.",
                nameof(session));
        }

        return new SessionDefinition(
            name, open, OrLengthKind.Fixed, length,
            session.Enabled, session.Budget, session.EntriesAllowed, session.Symbols);
    }
}
