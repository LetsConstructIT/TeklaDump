using System;
using System.Collections.Generic;
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
/// <para>
/// <b>Why this does not filter on <c>Tekla.</c></b> The Open API assemblies are not the only thing
/// the remoting stack needs. Connecting pulls in BCL-shaped dependencies at versions Tekla ships in
/// its own <c>bin</c> and redirects to in <c>TeklaStructures.exe.config</c> —
/// <c>System.Runtime.CompilerServices.Unsafe</c>, <c>System.ValueTuple</c>, the Grpc assemblies.
/// A <c>Tekla.</c>-only handler lets those fail, and the failure is not a clean "no session": it is
/// a <c>TypeInitializationException</c> out of <c>Trimble.Remoting</c> that reads like a bug.
/// </para>
/// <para>
/// The other half is <b>version</b>. Those binds ask for the reference-assembly version
/// (<c>Unsafe 4.0.4.1</c>) while the bin holds the one Tekla actually runs (<c>6.0.0.0</c>), which
/// is what the config's <c>bindingRedirect</c> entries exist to bridge. We cannot use that config —
/// its <c>codeBase</c> hrefs assume the process's <c>ApplicationBase</c> is Tekla's <c>bin</c>, and
/// ours is wherever the exe was downloaded to. Copying it in gets you one failure further and then
/// dies on the next assembly that has a redirect but no <c>codeBase</c>. So this handler matches on
/// the <b>simple name only</b> and returns whatever version the bin holds — which is by definition
/// the version the running Tekla is using, and the only one that can talk to it.
/// </para>
/// <para>
/// That stays narrow because the probe is the Tekla bin and nothing else: a file has to exist there
/// under the requested simple name. An unrelated missing assembly is still reported as itself.
/// </para>
/// </remarks>
internal static class TeklaAssemblyResolver
{
    private static string? _overrideDirectory;
    private static bool _installed;

    /// <summary>
    /// Cached because <see cref="Resolve"/> now runs for every failed bind, not just a handful of
    /// Tekla ones, and the lookup enumerates processes. Null means "asked, found none".
    /// </summary>
    private static string? _runningBinDirectory;
    private static bool _runningBinDirectoryKnown;

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
        if (_runningBinDirectoryKnown) return _runningBinDirectory;

        _runningBinDirectory = FindRunningTeklaBinDirectory();
        _runningBinDirectoryKnown = true;
        return _runningBinDirectory;
    }

    private static string? FindRunningTeklaBinDirectory()
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
        if (string.IsNullOrEmpty(name)) return null;

        // Satellite assemblies are a localization miss, never a Tekla binding problem, and probing
        // for them on every failed bind is pure noise.
        if (name!.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var directory in new[] { _overrideDirectory, RunningTeklaBinDirectory() })
        {
            if (string.IsNullOrEmpty(directory)) continue;

            foreach (var folder in ProbeFolders(directory!))
            {
                try
                {
                    // Simple name, any version: see the class remarks. The bin's copy is the one the
                    // running Tekla loaded, so it is the only version that can talk to it anyway.
                    var candidate = Path.Combine(folder, name + ".dll");
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                catch (Exception)
                {
                    // A bad path or an unloadable file is not the end — try the next source and
                    // then let the CLR report the original binding failure.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The bin folder and the private-probing folders Tekla declares under it.
    /// </summary>
    /// <remarks>
    /// <c>TeklaStructures.exe.config</c> carries
    /// <c>&lt;probing privatePath="ExternalDeps/Teigha;ExternalDeps/Grpc;…"/&gt;</c>, which the CLR
    /// honours only for the process's own <c>ApplicationBase</c> — not ours. Without these the
    /// remoting stack cannot find <c>Grpc.Core</c>, and the symptom is not an exception but a
    /// <b>hang</b>: the channel keeps waiting for a transport that will never load. Grpc's native
    /// half (<c>grpc_csharp_ext.x64.dll</c>) sits beside the managed one and is found relative to
    /// it, so loading from this folder is what makes it work.
    /// </remarks>
    private static IEnumerable<string> ProbeFolders(string binDirectory)
    {
        yield return binDirectory;
        yield return Path.Combine(binDirectory, "ExternalDeps", "Grpc");
        yield return Path.Combine(binDirectory, "ExternalDeps", "Teigha");
        yield return Path.Combine(binDirectory, "ExternalDeps", "OpenCascade");
    }
}
