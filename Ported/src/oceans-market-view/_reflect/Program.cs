using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Dumps public/protected surface of ATAS types so the indicator is written against
// what the installed DLLs actually expose, not against documentation.
//
//   dotnet run -- <TypeName> [TypeName...]
//
// Every ATAS assembly is scanned, so type names need no assembly qualifier.
class Program
{
    static void Main(string[] args)
    {
        var atas = @"C:\Program Files (x86)\ATAS Platform";
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var wpfRef = @"C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref\10.0.0\ref\net10.0";

        var paths = new List<string>();
        paths.AddRange(Directory.GetFiles(atas, "*.dll"));
        paths.AddRange(Directory.GetFiles(runtime, "*.dll"));
        if (Directory.Exists(wpfRef))
        {
            var have = new HashSet<string>(paths.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            paths.AddRange(Directory.GetFiles(wpfRef, "*.dll").Where(f => !have.Contains(Path.GetFileName(f))));
        }

        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver);

        // Only the assemblies an indicator can reference.
        var wanted = new[]
        {
            "ATAS.Indicators.dll", "OFT.Rendering.dll", "OFT.Attributes.dll",
            "OFT.Core.dll", "ATAS.DataFeedsCore.dll", "Utils.Common.dll"
        };

        var asms = new List<Assembly>();
        foreach (var f in wanted)
        {
            try { asms.Add(mlc.LoadFromAssemblyPath(Path.Combine(atas, f))); }
            catch (Exception ex) { Console.WriteLine($"!! could not load {f}: {ex.Message}"); }
        }

        foreach (var want in args)
        {
            // "~foo" lists type names containing foo instead of dumping members.
            if (want.StartsWith("~"))
            {
                var needle = want.Substring(1);
                foreach (var asm in asms)
                    foreach (var t in Types(asm).Where(t => t.IsPublic &&
                             t.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                        Console.WriteLine($"    {t.FullName}   [{asm.GetName().Name}]");
                Console.WriteLine();
                continue;
            }

            var hit = false;
            foreach (var asm in asms)
            {
                foreach (var t in Types(asm).Where(t => t.IsPublic && t.Name == want))
                {
                    hit = true;
                    Console.WriteLine($"--- {t.FullName}{(t.IsEnum ? " (enum)" : "")}   [{asm.GetName().Name}] ---");
                    if (t.IsEnum)
                        foreach (var f in t.GetFields().Where(f => f.IsStatic))
                            Console.WriteLine("    E  " + f.Name);
                    else
                    {
                        for (var c = t; c != null && c.FullName != "System.Object"; c = SafeBase(c))
                        {
                            if (c != t) Console.WriteLine("  : inherited from " + c.FullName);
                            Dump(c);
                        }
                        foreach (var i in Safe(() => t.GetInterfaces()))
                        {
                            Console.WriteLine("  : interface " + i.Name);
                            Dump(i);
                        }
                    }
                    Console.WriteLine();
                }
            }
            if (!hit) Console.WriteLine($"!! type not found: {want}\n");
        }
    }

    static Type SafeBase(Type t) { try { return t.BaseType; } catch { return null; } }

    static IEnumerable<Type> Types(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    static IEnumerable<T> Safe<T>(Func<T[]> f)
    {
        try { return f() ?? Array.Empty<T>(); }
        catch { return Array.Empty<T>(); }
    }

    static void Dump(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.Static |
                               BindingFlags.DeclaredOnly;

        foreach (var p in Safe(() => t.GetProperties(F)))
        {
            try
            {
                var g = p.GetGetMethod(true);
                if (g != null && (g.IsPublic || g.IsFamily))
                    Console.WriteLine($"    P  {Nm(p.PropertyType)} {p.Name}");
            }
            catch { }
        }
        foreach (var m in Safe(() => t.GetMethods(F)))
        {
            try
            {
                if (m.IsSpecialName) continue;
                if (!(m.IsPublic || m.IsFamily)) continue;
                var ps = string.Join(", ", m.GetParameters().Select(x => Nm(x.ParameterType) + " " + x.Name));
                var mod = m.IsVirtual ? (m.IsAbstract ? "abstract " : "virtual ") : "";
                Console.WriteLine($"    M  {mod}{Nm(m.ReturnType)} {m.Name}({ps})");
            }
            catch { }
        }
        foreach (var c in Safe(() => t.GetConstructors(F)))
        {
            try
            {
                if (!(c.IsPublic || c.IsFamily)) continue;
                var ps = string.Join(", ", c.GetParameters().Select(x => Nm(x.ParameterType) + " " + x.Name));
                Console.WriteLine($"    C  .ctor({ps})");
            }
            catch { }
        }
    }

    // Full name for the ambiguous ones (Color especially: System.Drawing vs System.Windows.Media).
    static string Nm(Type t)
    {
        if (t == null) return "?";
        var n = t.Name;
        return n == "Color" || n == "Point" || n == "Rectangle" || n == "Size" || n == "Pen"
            ? t.FullName
            : n;
    }
}
