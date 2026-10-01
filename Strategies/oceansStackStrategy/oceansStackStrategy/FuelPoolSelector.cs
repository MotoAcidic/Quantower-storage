namespace oceansStackStrategy;

/// <summary>Direct port of `ocean.pine`'s own `f_fuel` — given a value-area edge, a direction
/// (+1 = look above, for the top/short zone; -1 = look below, for the bottom/long zone), and the
/// four candidate pools (weekly conditional on its own enable flag), returns the NEAREST pool whose
/// distance beyond the edge falls inside [poolMin, poolMax]. Pure function, no state.</summary>
internal static class FuelPoolSelector
{
    public static (double? Price, string Name) SelectNearest(
        double edge, int dir, double poolMin, double poolMax,
        bool useOvernight, double? overnightHigh, double? overnightLow,
        bool useAsia, double? asiaHigh, double? asiaLow,
        bool usePriorDay, double? priorDayHigh, double? priorDayLow,
        bool useWeeklyFuel, double? priorWeekHigh, double? priorWeekLow)
    {
        double? best = null;
        var name = string.Empty;
        var up = dir > 0;

        void Consider(bool enabled, double? candidate, string candidateName)
        {
            if (!enabled || candidate is not { } price) return;

            var dist = (price - edge) * dir;
            if (dist < poolMin || dist > poolMax) return;

            if (best is null || dist < (best.Value - edge) * dir)
            {
                best = price;
                name = candidateName;
            }
        }

        Consider(useOvernight, up ? overnightHigh : overnightLow, up ? "ovnH" : "ovnL");
        Consider(useAsia, up ? asiaHigh : asiaLow, up ? "asiaH" : "asiaL");
        Consider(usePriorDay, up ? priorDayHigh : priorDayLow, up ? "PDH" : "PDL");
        Consider(useWeeklyFuel, up ? priorWeekHigh : priorWeekLow, up ? "PWH" : "PWL");

        return (best, name);
    }
}
