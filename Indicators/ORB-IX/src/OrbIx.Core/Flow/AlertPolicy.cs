using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Flow;

/// <summary>
/// A feature's alert switches. ATAS offers "Signal alert" (a level appeared) and
/// "Approximation alert" (price approached a level) on Stacked Imbalance and Absorption,
/// and "Use alerts" on the rest; the approach distance is this project's input because the
/// articles name the alert but not its distance.
/// </summary>
/// <param name="OnSignal">Alert when a level or hit appears.</param>
/// <param name="ApproachTicks">Alert when price comes within this many ticks of an untouched level; 0 = off.</param>
public readonly record struct AlertRule(bool OnSignal, int ApproachTicks)
{
    public bool Any => this.OnSignal || this.ApproachTicks > 0;
}

/// <summary>What to raise. <see cref="Key"/> is what makes it raise once.</summary>
public readonly record struct AlertMessage(string Key, string Text);

/// <summary>
/// Decides which alerts to raise and guarantees each is raised once. The platform call
/// is the shell's; this only says what and whether.
///
/// ONCE PER LEVEL, PER KIND. A level that stays near price for an hour would otherwise
/// alert on every fold, and an operator who has been told once has been told.
/// </summary>
public sealed class AlertPolicy
{
    private readonly HashSet<string> raised = new(StringComparer.Ordinal);
    private readonly int maxRemembered;

    public AlertPolicy(int maxRemembered = 20000)
    {
        if (maxRemembered < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRemembered), maxRemembered, "At least one raised alert must be remembered.");

        this.maxRemembered = maxRemembered;
    }

    /// <summary>Signal alerts for levels that just appeared.</summary>
    public IReadOnlyList<AlertMessage> OnNewLevels(IEnumerable<FlowLevel> newLevels, AlertRule rule, string featureName)
    {
        ArgumentNullException.ThrowIfNull(newLevels);
        ArgumentNullException.ThrowIfNull(featureName);

        var messages = new List<AlertMessage>();

        if (!rule.OnSignal)
            return messages;

        foreach (var level in newLevels)
        {
            var key = "signal:" + level.Id;

            if (!this.Remember(key))
                continue;

            messages.Add(new AlertMessage(
                key,
                string.Create(CultureInfo.InvariantCulture,
                    $"{featureName}: {level.Side} {level.Label} at {level.Price}")));
        }

        return messages;
    }

    /// <summary>Approach alerts for untouched levels within the rule's distance of the last price.</summary>
    public IReadOnlyList<AlertMessage> OnPrice(
        IEnumerable<FlowLevel> activeLevels, double lastPrice, double tickSize, AlertRule rule, string featureName)
    {
        ArgumentNullException.ThrowIfNull(activeLevels);
        ArgumentNullException.ThrowIfNull(featureName);

        var messages = new List<AlertMessage>();

        if (rule.ApproachTicks <= 0 || !double.IsFinite(lastPrice) || !(tickSize > 0))
            return messages;

        var reach = rule.ApproachTicks * tickSize;

        foreach (var level in activeLevels)
        {
            if (level.IsTouched || Math.Abs(level.Price - lastPrice) > reach)
                continue;

            var key = "approach:" + level.Id;

            if (!this.Remember(key))
                continue;

            messages.Add(new AlertMessage(
                key,
                string.Create(CultureInfo.InvariantCulture,
                    $"{featureName}: price {lastPrice} within {rule.ApproachTicks} ticks of {level.Side} {level.Label} at {level.Price}")));
        }

        return messages;
    }

    /// <summary>
    /// Cluster Statistics alerts: "triggered when the configured filter value is reached or
    /// exceeded" (article 72000602624). Judged on the value's magnitude, once per bar and row.
    /// </summary>
    public IReadOnlyList<AlertMessage> OnStatistics(
        in StatColumn column, IReadOnlyList<(StatRow Row, double Threshold)> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        var messages = new List<AlertMessage>();

        foreach (var (row, threshold) in thresholds)
        {
            if (!(threshold > 0) || !column.IsMeasured(row))
                continue;

            var value = column.Value(row);

            if (Math.Abs(value) < threshold)
                continue;

            var key = string.Create(CultureInfo.InvariantCulture, $"stat:{column.OpenUtc:O}:{row}");

            if (!this.Remember(key))
                continue;

            messages.Add(new AlertMessage(
                key,
                string.Create(CultureInfo.InvariantCulture,
                    $"Cluster statistics: {row} {value:N0} reached {threshold:N0} on the {column.OpenUtc:HH:mm} bar")));
        }

        return messages;
    }

    public void Reset() => this.raised.Clear();

    private bool Remember(string key)
    {
        if (!this.raised.Add(key))
            return false;

        // A set that grew all week would be a slow leak; when it fills, forgetting the lot
        // costs at worst one repeated alert per level, which is the cheaper failure.
        if (this.raised.Count > this.maxRemembered)
        {
            this.raised.Clear();
            this.raised.Add(key);
        }

        return true;
    }
}
