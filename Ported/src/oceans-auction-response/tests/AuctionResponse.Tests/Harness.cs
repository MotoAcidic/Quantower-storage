using System.Globalization;

namespace AuctionResponse.Tests;

/// <summary>
/// Zero-dependency test harness. Deliberately not a third-party framework: this project
/// must build and gate deploys on a machine with no package restore, and the deploy script
/// keys off the process exit code.
/// </summary>
public static class Harness
{
    private static readonly List<string> Failures = new();
    private static int _passed;
    private static string _section = "";

    public static void Section(string name)
    {
        _section = name;
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }

    public static void Check(string name, bool condition, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine("  PASS  " + name); }
        else
        {
            var line = _section + " / " + name + (detail.Length > 0 ? " -- " + detail : "");
            Failures.Add(line);
            Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? "  -- " + detail : ""));
        }
    }

    public static void Equal(string name, double expected, double? actual, double tolerance = 1e-9)
    {
        if (actual is null) { Check(name, false, "expected " + F(expected) + ", got null"); return; }
        var ok = Math.Abs(expected - actual.Value) <= tolerance;
        Check(name, ok, ok ? "" : "expected " + F(expected) + ", got " + F(actual.Value));
    }

    public static void Equal(string name, decimal expected, decimal actual)
    {
        var ok = expected == actual;
        Check(name, ok, ok ? "" : "expected " + expected + ", got " + actual);
    }

    public static void Equal(string name, long expected, long actual)
    {
        var ok = expected == actual;
        Check(name, ok, ok ? "" : "expected " + expected + ", got " + actual);
    }

    public static void Equal<T>(string name, T expected, T actual) where T : notnull
    {
        var ok = expected.Equals(actual);
        Check(name, ok, ok ? "" : "expected " + expected + ", got " + actual);
    }

    public static void Null(string name, double? actual)
        => Check(name, actual is null, actual is null ? "" : "expected null, got " + F(actual.Value));

    public static void Throws<TException>(string name, Action action) where TException : Exception
    {
        try { action(); Check(name, false, "expected " + typeof(TException).Name + ", nothing was thrown"); }
        catch (TException) { Check(name, true); }
        catch (Exception ex) { Check(name, false, "expected " + typeof(TException).Name + ", got " + ex.GetType().Name); }
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static int Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 70));
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL TESTS PASSED  (" + _passed + " checks)");
            return 0;
        }

        Console.WriteLine(Failures.Count + " FAILED of " + (_passed + Failures.Count) + " checks:");
        foreach (var f in Failures) Console.WriteLine("  * " + f);
        return 1;
    }
}
