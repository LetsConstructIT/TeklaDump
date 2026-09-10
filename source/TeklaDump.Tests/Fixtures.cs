using System;
using System.IO;

namespace TeklaDump.Tests;

/// <summary>
/// Locates the repository's committed fixture files.
/// </summary>
/// <remarks>
/// Tests read the files in <c>fixtures/</c> rather than an installed Tekla environment. The
/// fixtures are real files copied from an installation, so the parsers are exercised against the
/// format Tekla actually writes — but a suite that only passes on a machine with the right Tekla
/// and the right environment installed is not a suite anyone can rely on.
/// </remarks>
internal static class Fixtures
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts)
    {
        var path = System.IO.Path.Combine(Root, "fixtures");
        foreach (var part in parts) path = System.IO.Path.Combine(path, part);
        return path;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "TeklaDump.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root above " + AppDomain.CurrentDomain.BaseDirectory);
    }
}

/// <summary>A throwaway directory tree for one test.</summary>
internal sealed class TempTree : IDisposable
{
    private readonly string _root;

    public TempTree()
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TeklaDumpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Writes a file, creating intermediate folders. Returns its full path.</summary>
    public string Write(string relativePath, string contents)
    {
        var full = System.IO.Path.Combine(_root, relativePath);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>Writes raw bytes, for tests that pin down encoding handling.</summary>
    public string WriteBytes(string relativePath, byte[] contents)
    {
        var full = System.IO.Path.Combine(_root, relativePath);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp folder that outlives the run is not a test failure */ }
    }
}
