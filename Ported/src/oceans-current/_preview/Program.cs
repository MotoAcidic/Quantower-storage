using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Draws the panel with the indicator's own DrawPanel, through ATAS's own GDI+ RenderContext,
// into a PNG. The point is to see what the chart will show -- or the exception it would have
// swallowed -- without restarting the platform.
//
//   dotnet run -c Release -- [out.png]
//
// Main touches no ATAS type, for the same reason as _smoke: the resolver has to be attached first.
static class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            var path = Path.Combine(AtasDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        return Run(args.Length > 0 ? args[0] : "panel_preview.png");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string outPath)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        try
        {
            var indicator = new OceansCurrent.OceansCurrentIndicator();
            var t = indicator.GetType();

            t.GetMethod("EnsureFonts", Any).Invoke(indicator, null);
            var cfg = (OceansCurrent.FactorConfig)t.GetMethod("BuildFactors", Any).Invoke(indicator, null);

            // Tonight's 20:50 row from the log: VWAP and Delta voting overnight, short gamma.
            var subs = new[]
            {
                OceansCurrent.Subscore.At(-12.0m), OceansCurrent.Subscore.At(-11.8m),
                OceansCurrent.Subscore.Missing("not built (v1.1)"), OceansCurrent.Subscore.At(-55.7m),
                OceansCurrent.Subscore.At(0m), OceansCurrent.Subscore.Missing("not built (v1.1)")
            };

            var gex = OceansCurrent.GexSnapshot.FromLevels(DateTime.UtcNow, 29110.75m,
                                                           713m, 715m, 705m, 41.12m, 0.10m);

            var record = new OceansCurrent.BiasRecord
            {
                Close = 29110.75m,
                Subs = subs,
                Regime = OceansCurrent.RegimeMode.Negative,
                Score = -12.6m,
                ScoreKnown = true,
                State = OceansCurrent.BiasState.Neutral,
                Confidence = 38m,
                Flip = OceansCurrent.Level.None
            };

            var model = OceansCurrent.PanelModel.Build(record, cfg, true, -14.5m, gex, true, false, 0, 0.25m);

            // Representative of a real scan tonight: NEUTRAL, short gamma, dwell served.
            var trig = new OceansCurrent.TriggerReport
            {
                State = OceansCurrent.BiasState.Neutral,
                Close = 29110.75m,
                Range = 582m,
                DeltaVoting = true,
                GammaFlip = gex.GammaFlip,
                Below = new OceansCurrent.Trigger { Found = true, Kind = OceansCurrent.TriggerKind.CloseBelow, Price = 29094.25m, To = OceansCurrent.BiasState.Short },
                Above = new OceansCurrent.Trigger { Found = true, Kind = OceansCurrent.TriggerKind.CloseAbove, Price = 29168.00m, To = OceansCurrent.BiasState.Long },
                HoldAbove = new OceansCurrent.Trigger { Found = true, Kind = OceansCurrent.TriggerKind.HoldAbove, Price = 29151.50m, To = OceansCurrent.BiasState.Long },
                SellDelta = new OceansCurrent.Trigger { Found = true, Kind = OceansCurrent.TriggerKind.SellDelta, Delta = -1850m, To = OceansCurrent.BiasState.Short },
                BuyDelta = new OceansCurrent.Trigger { Found = false, Kind = OceansCurrent.TriggerKind.BuyDelta },
                SweepHigh = new OceansCurrent.Trigger { Found = true, Kind = OceansCurrent.TriggerKind.SweepHigh, Price = 29210m, Reference = "ONH", To = OceansCurrent.BiasState.Short },
                SweepLow = new OceansCurrent.Trigger { Found = true, Kind = OceansCurrent.TriggerKind.SweepLow, Price = 29042.5m, Reference = "PDL", To = OceansCurrent.BiasState.Neutral }
            };
            model.AddTriggers(trig, 0.25m);

            var cpi = new OceansCurrent.MacroEvent { Title = "Core CPI m/m", WhenUtc = DateTime.UtcNow.AddHours(10).AddMinutes(26), AlsoAtSameTime = 3 };
            model.AddEvent(cpi, null, DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"), true);

            model.AddTrack(new OceansCurrent.Scorecard
            {
                Calls = 16, Right = 10, NetPoints = 312m, TodayCalls = 2, TodayPoints = 41.5m,
                OpenCall = false
            });

            using (var bmp = new Bitmap(1800, 1560))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(16, 20, 28));

                var ctx = new Rendering.GDIPlus.Context.GDIPlusRenderContext(bmp);
                var region = new Rectangle(0, 0, bmp.Width, bmp.Height);

                t.GetMethod("DrawPanel", Any).Invoke(indicator, new object[] { ctx, region, model });

                ((object)ctx as IDisposable)?.Dispose();
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            Console.WriteLine("Rendered " + Path.GetFullPath(outPath));
            return 0;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Console.WriteLine("RENDER FAILED: " + inner);
            return 1;
        }
    }
}
