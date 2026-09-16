using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator the way ATAS does, outside ATAS. A constructor that throws is
// invisible in the platform -- the indicator simply never appears in the list -- so it is worth
// catching here, where the exception is actually readable.
//
// It also prints every setting with its group and default, because the settings dialog is the
// only other place to see them and it needs a restart of ATAS to look.
//
// Main must not touch an ATAS type itself: the JIT resolves a method's types when the method is
// entered, which would happen before the resolver below is attached.
static class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            var path = Path.Combine(AtasDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        return Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run()
    {
        try
        {
            var indicator = new OceansCurrent.OceansCurrentIndicator();
            Console.WriteLine("Constructed OK.");

            var ind = (ATAS.Indicators.Indicator)indicator;

            Console.WriteLine("Panel: '" + ind.Panel + "'");
            Console.WriteLine("DrawAbovePrice: " + ind.DrawAbovePrice);
            Console.WriteLine("EnableCustomDrawing: " + ind.EnableCustomDrawing);
            Console.WriteLine("DataSeries count: " + ind.DataSeries.Count);

            for (var i = 0; i < ind.DataSeries.Count; i++)
            {
                var v = ind.DataSeries[i] as ATAS.Indicators.ValueDataSeries;
                if (v == null) continue;

                // The inherited series must be invisible: this indicator plots nothing per bar,
                // and a stray zero line across the price panel would be its own bug report.
                Console.WriteLine("  [" + i + "] IsHidden=" + v.IsHidden +
                                  "  VisualType=" + v.VisualType +
                                  "  ScaleIt=" + v.ScaleIt);
            }

            // The defaults have to survive a round trip through the model, because a default
            // that cannot build an engine is a chart that shows nothing and says nothing.
            var engine = new OceansCurrent.BiasEngine(
                new OceansCurrent.SessionConfig(),
                new OceansCurrent.FactorConfig(),
                new OceansCurrent.StateConfig(),
                0.25m);

            Console.WriteLine("Engine built on the defaults. Next bar: " + engine.NextBar);

            var failures = 0;

            // The two things that make this indicator draw NOTHING, both of them invisible at
            // runtime: no exception, no log line, no badge. Asserted here because deploy.ps1 runs
            // this, and because both have now happened.
            failures += CheckSubscription(ind);
            failures += CheckFont(indicator.BadgeFont);

            // The GEX path and the log folder are written to and read from at runtime, where a
            // bad path is a silent no-op. Their parents are checked here instead.
            failures += CheckFolder("GEX CSV", Path.GetDirectoryName(indicator.GexCsvPath));
            failures += CheckFolder("log folder", indicator.LogFolder);

            Console.WriteLine();
            Console.WriteLine("Settings as ATAS will show them:");

            var group = "";
            foreach (var property in indicator.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var display = property.GetCustomAttribute<DisplayAttribute>();
                if (display == null) continue;
                if (property.DeclaringType != typeof(OceansCurrent.OceansCurrentIndicator)) continue;

                if (display.GroupName != group)
                {
                    group = display.GroupName;
                    Console.WriteLine("  " + group);
                }

                var value = property.CanRead ? property.GetValue(indicator) : null;
                Console.WriteLine("      " + display.Name.PadRight(34) + " = " + value);
            }

            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("CONSTRUCTOR THREW: " + ex.GetType().Name);
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Every working indicator in this suite subscribes to Final alone. A combined mask stores
    /// fine and reads back fine, and if the platform tests it for equality rather than as flags
    /// then OnRender is simply never raised -- which looks exactly like the indicator not being
    /// on the chart. Read the field back off the base class and insist on Final.
    /// </summary>
    static int CheckSubscription(ATAS.Indicators.Indicator ind)
    {
        var field = typeof(ATAS.Indicators.ExtendedIndicator)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(f => f.FieldType == typeof(ATAS.Indicators.DrawingLayouts));

        if (field == null)
        {
            Console.WriteLine("PROBLEM: could not find the drawing-layout field to check.");
            return 1;
        }

        var value = (ATAS.Indicators.DrawingLayouts)field.GetValue(ind);
        Console.WriteLine("Drawing layout: " + value);

        if (value != ATAS.Indicators.DrawingLayouts.Final)
        {
            Console.WriteLine("PROBLEM: subscribed to '" + value + "', not Final. Nothing will draw.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// RenderFont does NOT throw on a family nothing matches -- it builds happily from any
    /// nonsense string -- so a font nobody has gets measured and drawn with, and a badge measured
    /// at zero is an invisible one. The default has to name a font this machine actually has.
    /// </summary>
    static int CheckFont(string family)
    {
        try
        {
            using (new System.Drawing.FontFamily(family))
            {
                Console.WriteLine("Badge font: " + family + " (installed)");
                return 0;
            }
        }
        catch
        {
            Console.WriteLine("PROBLEM: the default badge font '" + family + "' is not installed.");
            return 1;
        }
    }

    /// <summary>
    /// A default path whose parent does not exist is not an error -- the logger creates its own
    /// folder and a missing GEX file is a stated state -- but it IS worth printing, because
    /// "the regime cell says no feed" and "the path is wrong" look identical on the chart.
    /// </summary>
    static int CheckFolder(string what, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            Console.WriteLine("PROBLEM: the " + what + " path is empty.");
            return 1;
        }

        Console.WriteLine((Directory.Exists(folder) ? "Found  " : "Absent ") + what + ": " + folder);
        return 0;
    }
}
