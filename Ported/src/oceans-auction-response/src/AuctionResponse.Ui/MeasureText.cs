using System.Globalization;
using AuctionResponse.Core;

namespace AuctionResponse.Ui;

/// <summary>
/// Turns a <see cref="Measure"/> into display text.
///
/// An unavailable value is ALWAYS "N/A" and never a zero, at any width. When the column is
/// too narrow for units the number is shortened, but availability is never traded away for
/// space: the distinction between "zero" and "we do not know" is the whole point.
/// </summary>
public static class MeasureText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Unavailable = "N/A";

    public static string Short(Measure m)
    {
        if (!m.IsAvailable) return Unavailable;

        var v = m.Value!.Value;
        return m.Unit switch
        {
            "qty" => Compact(v),
            "ticks" => Trim(v, 2) + "t",
            "ms" => Trim(v, 0) + "ms",
            "ratio" or "fraction" => Trim(v, 3),
            "qty/s" => Compact(v) + "/s",
            "z" => Trim(v, 2),
            _ => Trim(v, 3)
        };
    }

    /// <summary>The reason an unavailable value is unavailable. Never empty when it matters.</summary>
    public static string Reason(Measure m) => m.IsAvailable ? "" : m.Reason ?? "unavailable";

    private static string Compact(double v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000) return Trim(v / 1_000_000, 1) + "M";
        if (abs >= 10_000) return Trim(v / 1_000, 1) + "k";
        return Trim(v, 2);
    }

    private static string Trim(double v, int decimals)
    {
        var text = Math.Round(v, decimals).ToString("0." + new string('#', Math.Max(decimals, 0)), Inv);
        return text == "-0" ? "0" : text;
    }

    public static string Label(DescriptiveLabel label) => label switch
    {
        DescriptiveLabel.BuyingAdvances => "Buying advances",
        DescriptiveLabel.SellingAdvances => "Selling advances",
        DescriptiveLabel.Mixed => "Mixed",
        DescriptiveLabel.Neutral => "Neutral",
        _ => "Unavailable"
    };

    public static string Price(decimal price) => price.ToString("0.##", Inv);
}
