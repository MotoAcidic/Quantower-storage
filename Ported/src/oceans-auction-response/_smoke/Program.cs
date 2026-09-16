using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using AuctionResponse.Atas;

namespace AuctionResponse.Smoke;

/// <summary>
/// A constructor that throws is INVISIBLE inside ATAS: the indicator simply never appears in
/// the list. This harness constructs and disposes it out here, where the exception can be
/// seen, and checks the structural promises that matter before a DLL is allowed near the
/// platform. deploy.ps1 refuses to copy anything if this fails.
/// </summary>
internal static class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    /// <summary>
    /// Installed as a module initializer, not inside Main: the runtime resolves a method's
    /// dependencies when it JITs that method, so a handler registered in Main is already too
    /// late for the ATAS types Main itself touches.
    /// </summary>
    [ModuleInitializer]
    internal static void InstallAssemblyResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var simple = new AssemblyName(e.Name).Name;
            var path = Path.Combine(AtasDir, simple + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var indicator = new AuctionResponseMonitor();

        // Defaults must be safe: nothing records, nothing sounds, nothing arms.
        if (indicator.RecordingApproved) return Fail("recording is approved by default");
        if (!string.IsNullOrEmpty(indicator.RecordingDirectory)) return Fail("a recording directory is set by default");
        if (indicator.AudibleAlerts) return Fail("audible alerts are on by default");
        if (!string.IsNullOrEmpty(indicator.BaselineArtifactPath)) return Fail("a baseline path is set by default");
        if (indicator.SessionTimezone != "America/Chicago") return Fail("session timezone is not Central by default");
        if (!string.IsNullOrEmpty(indicator.ExtraLevels)) return Fail("levels are pre-declared by default");
        if (!indicator.UseDrawnLines) return Fail("drawn lines are ignored by default; the whole point is that they are not");

        // There must be no reachable order-submission surface on the indicator itself.
        var orderSurface = typeof(AuctionResponseMonitor)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.Contains("Order", StringComparison.OrdinalIgnoreCase) &&
                       (n.StartsWith("Open") || n.StartsWith("Place") || n.StartsWith("Submit") ||
                        n.StartsWith("Cancel") || n.StartsWith("Modify")))
            .ToList();
        if (orderSurface.Count > 0) return Fail("order surface present: " + string.Join(", ", orderSurface));

        ((IDisposable)indicator).Dispose();

        Console.WriteLine("SMOKE OK: AuctionResponseMonitor constructs and disposes, defaults are safe, no order path.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("SMOKE FAILED: " + message);
        return 1;
    }
}
