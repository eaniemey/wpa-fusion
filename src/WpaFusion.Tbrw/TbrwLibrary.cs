using System.Reflection;
using System.Runtime.InteropServices;
using WpaFusion.Tbrw.Native;

namespace WpaFusion.Tbrw;

/// <summary>
/// Locates and loads SEP's versioned TBRW DLL (amplxe_tbrw_*.dll). Call <see cref="Initialize"/>
/// once with SEP's bin64 directory before using <see cref="TbrwFile"/>.
/// </summary>
public static class TbrwLibrary
{
    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string? _loadedPath;

    /// <summary>Full path of the loaded TBRW DLL, or null before initialization.</summary>
    public static string? LoadedPath => _loadedPath;

    /// <summary>
    /// Loads the newest amplxe_tbrw_*.dll from <paramref name="sepBin64Directory"/>. Dependencies
    /// (sampling_utils.dll, MSVC runtime) resolve from the same directory. Idempotent for the same
    /// directory; throws if a different DLL was already loaded.
    /// </summary>
    public static void Initialize(string sepBin64Directory)
    {
        var path = FindTbrwDll(sepBin64Directory);
        lock (Gate)
        {
            if (_loadedPath is not null)
            {
                if (!string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"TBRW already loaded from '{_loadedPath}', cannot switch to '{path}'.");
                }
                return;
            }

            _handle = NativeLibrary.Load(path);
            _loadedPath = path;
            NativeLibrary.SetDllImportResolver(typeof(TbrwLibrary).Assembly, Resolve);
        }
    }

    /// <summary>Returns the path of the TBRW DLL in a SEP bin64 directory, choosing the highest version.</summary>
    public static string FindTbrwDll(string sepBin64Directory)
    {
        if (!Directory.Exists(sepBin64Directory))
        {
            throw new DirectoryNotFoundException($"SEP bin64 directory not found: '{sepBin64Directory}'.");
        }

        var candidates = Directory.GetFiles(sepBin64Directory, "amplxe_tbrw_*.dll")
            .OrderByDescending(p => ParseVersion(Path.GetFileNameWithoutExtension(p)))
            .ToList();

        return candidates.Count > 0
            ? candidates[0]
            : throw new FileNotFoundException($"No amplxe_tbrw_*.dll in '{sepBin64Directory}'.");
    }

    private static Version ParseVersion(string fileNameWithoutExtension)
    {
        var suffix = fileNameWithoutExtension.Substring("amplxe_tbrw_".Length);
        return Version.TryParse(suffix, out var v) ? v : new Version(0, 0);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return libraryName == TbrwNative.Library ? _handle : IntPtr.Zero;
    }

    internal static void EnsureLoaded()
    {
        if (_loadedPath is null)
        {
            throw new InvalidOperationException("TbrwLibrary.Initialize must be called before opening .tb7 files.");
        }
    }
}
