namespace AuctionResponse.Core;

/// <summary>
/// Orientation: +1 at resistance, where buying is the attacking flow; -1 at support, where
/// selling attacks. Multiplying a boundary by o returns the actual midpoint threshold.
/// </summary>
public enum LevelSide { Resistance = 1, Support = -1 }

/// <summary>
/// A level declared BEFORE the candidate window begins (Section 11). The baseline supports
/// manually supplied levels only: automatic PDH/PDL and opening ranges need a separately
/// specified session convention, which this build does not invent.
/// </summary>
public sealed record LevelDefinition
{
    public required string LevelId { get; init; }
    public required LevelSide Side { get; init; }

    /// <summary>Level price as an exact integer tick index.</summary>
    public required long PriceTicks { get; init; }

    /// <summary>Elapsed time at which the level became effective, in the declaring epoch.</summary>
    public required long EffectiveNs { get; init; }
    public required int ConnectionEpoch { get; init; }

    /// <summary>Elapsed time at which the level expires. Null means no scheduled expiry.</summary>
    public long? ExpiresNs { get; init; }

    /// <summary>Ordinal used to break display ties deterministically.</summary>
    public int Ordinal { get; init; }

    public string? Source { get; init; }

    public int Orientation => (int)Side;

    /// <summary>Frozen zone Z = [L - h, L + h], inclusive, in ticks.</summary>
    public (long Low, long High) Zone(int halfWidthTicks) => (PriceTicks - halfWidthTicks, PriceTicks + halfWidthTicks);

    /// <summary>
    /// The level must have existed before the candidate window opened — a level created
    /// during its own window is not evidence of anything.
    /// </summary>
    public bool ExistedBefore(long ns) => EffectiveNs < ns;

    public bool IsExpiredAt(long ns) => ExpiresNs is { } e && ns >= e;
}

public enum LevelChangeKind { Added, Removed, Expired, Replaced }

public sealed record LevelChange(LevelChangeKind Kind, LevelDefinition Level, string Reason);

/// <summary>
/// Holds the declared levels. Multiple levels keep independent state; the manager itself
/// owns no candidate state, only the definitions and their lifetimes.
/// </summary>
public sealed class LevelManager
{
    private readonly Dictionary<string, LevelDefinition> _levels = new(StringComparer.Ordinal);
    private int _nextOrdinal;

    public IReadOnlyCollection<LevelDefinition> Levels => _levels.Values;
    public int Count => _levels.Count;

    public LevelChange Add(LevelDefinition level)
    {
        var replaced = _levels.ContainsKey(level.LevelId);
        var stored = level with { Ordinal = replaced ? _levels[level.LevelId].Ordinal : _nextOrdinal++ };
        _levels[level.LevelId] = stored;
        return new LevelChange(replaced ? LevelChangeKind.Replaced : LevelChangeKind.Added, stored,
            replaced ? "level redefined" : "level declared");
    }

    public LevelChange? Remove(string levelId, string reason)
    {
        if (!_levels.Remove(levelId, out var level)) return null;
        return new LevelChange(LevelChangeKind.Removed, level, reason);
    }

    public LevelDefinition? Get(string levelId) => _levels.TryGetValue(levelId, out var l) ? l : null;

    /// <summary>Removes levels whose declared expiry has passed, reporting each removal.</summary>
    public List<LevelChange> ExpireDue(long ns)
    {
        var changes = new List<LevelChange>();
        foreach (var level in _levels.Values.Where(l => l.IsExpiredAt(ns)).ToList())
        {
            _levels.Remove(level.LevelId);
            changes.Add(new LevelChange(LevelChangeKind.Expired, level, "level expiry reached"));
        }
        return changes;
    }

    /// <summary>Levels valid in this epoch, in declaration order.</summary>
    public IEnumerable<LevelDefinition> ActiveIn(int epoch, long ns)
        => _levels.Values.Where(l => l.ConnectionEpoch <= epoch && !l.IsExpiredAt(ns)).OrderBy(l => l.Ordinal);

    public void Clear() { _levels.Clear(); _nextOrdinal = 0; }
}

/// <summary>
/// Per-level cooldown (Section 12). Starts at ANY terminal transition and is keyed by level
/// id, so one level cooling down never suppresses another.
/// </summary>
public sealed class CooldownRegistry
{
    private readonly Dictionary<string, long> _until = new(StringComparer.Ordinal);
    private readonly long _cooldownNs;

    public CooldownRegistry(int cooldownMs) { _cooldownNs = (long)cooldownMs * 1_000_000L; }

    public void Start(string levelId, long ns) => _until[levelId] = ns + _cooldownNs;

    public bool IsCoolingDown(string levelId, long ns) => _until.TryGetValue(levelId, out var until) && ns < until;

    public long? RemainingNs(string levelId, long ns)
        => _until.TryGetValue(levelId, out var until) && ns < until ? until - ns : null;

    public void Clear() => _until.Clear();
}
