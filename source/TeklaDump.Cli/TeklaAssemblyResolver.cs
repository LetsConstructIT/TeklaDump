using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace TeklaDump.Cli;

/// <summary>
/// Finds the Tekla Open API assemblies for a standalone exe that may be running on a machine we
/// have never seen.
/// </summary>
/// <remarks>
/// <para>
/// The exe is compiled against the Open API but ships none of it, so binding has three layers:
/// </para>
/// <list type="number">
/// <item>the <b>GAC</b>, where a Tekla installation registers its Open API assemblies — this is
/// what makes a downloaded exe work with no configuration at all;</item>
/// <item>this <b>AssemblyResolve</b> fallback, which loads them from the <c>bin</c> folder of the
/// running <c>TeklaStructures.exe</c>. The CLI already requires a running session, so that folder
/// exists whenever the tool can do anything useful;</item>
/// <item><c>--tekla-bin &lt;dir&gt;</c>, for the case where both fail and the user knows better.</item>
/// </list>
/// <para>
/// What is deliberately NOT a layer: baking a path into the exe.config at build time.
/// <c>TSAppConfigPatcherTask</c> reads the build machine's registry, which is fine for F5 and
/// useless — actively wrong — for a release asset.
/// </para>
/// </remarks>
internal static class TeklaAssemblyResolver
{
    private static string? _overrideDirectory;
    private static bool _installed;

    /// <summary>
    /// Installs the handler. Call this before ANY Tekla type is touched — including indirectly, by
    /// entering a method whose body mentions one, since the JIT resolves types on method entry.
    /// </summary>
    public static void Install(string? teklaBinDirectory)
    {
        _overrideDirectory = teklaBinDirectory;
        if (_installed) return;

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        _installed = true;
    }

    /// <summary>The bin folder of the running Tekla, or null when none is running.</summary>
    public static string? RunningTeklaBinDirectory()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("TeklaStructures"))
            {
                using (process)
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) return Path.GetDirectoryName(path);
                }
            }
        }
        catch (Exception)
        {
            // Reading another process's module list needs rights we may not have (a Tekla started
            // elevated, for instance). Not fatal: the GAC bind is the normal path anyway.
        }

        return null;
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name is null || !name.StartsWith("Tekla.", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var directory in new[] { _overrideDirectory, RunningTeklaBinDirectory() })
        {
            if (string.IsNullOrEmpty(directory)) continue;

            try
            {
                var candidate = Path.Combine(directory!, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            catch (Exception)
            {
                // A bad path or an unloadable file is not the end — try the next source and then
                // let the CLR report the original binding failure.
            }
        }

        return null;
    }
}
