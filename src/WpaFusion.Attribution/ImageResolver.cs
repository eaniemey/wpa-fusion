using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using WpaFusion.Plugin.Sep;

/// <summary>Maps an address in a process at a point in time to its ETL image and, if symbols are loaded, function.</summary>
internal sealed class ImageResolver
{
    private readonly Dictionary<uint, IProcess[]> _byPid;
    private readonly IImage[] _kernel;
    private readonly bool _symbolsLoaded;
    private readonly Dictionary<(IImage, ulong), SampleLocation> _cache = new();
    private readonly Dictionary<IProcess, IImage[]> _images = new();
    private readonly Dictionary<(IProcess, ulong), IImage[]> _rangeMatches = new();
    private readonly Dictionary<ulong, IImage[]> _kernelMatches = new();

    public ImageResolver(IEnumerable<IProcess> processes, bool symbolsLoaded)
    {
        var all = processes.ToArray();
        _byPid = all.GroupBy(p => unchecked((uint)p.Id)).ToDictionary(g => g.Key, g => g.ToArray());
        _kernel = all.Where(p => p.Id == 0 || p.Id == 4).SelectMany(p => p.Images).Distinct().ToArray();
        _symbolsLoaded = symbolsLoaded;
    }

    /// <summary>Process instances with this PID alive at <paramref name="ns"/> (more than one means a reused PID is ambiguous).</summary>
    public IProcess[] ActiveProcesses(uint pid, long ns) =>
        _byPid.TryGetValue(pid, out var ps) ? ps.Where(p => Active(p.CreateTime, p.ExitTime, ns)).ToArray() : Array.Empty<IProcess>();

    /// <summary>The module/function for <paramref name="address"/>, or null when no single image matches.</summary>
    public SampleLocation? Resolve(IProcess[] active, ulong address, long ns)
    {
        // Range matches are cached per (process, address); the time check stays per call.
        var matches = active.SelectMany(p => RangeMatches(p, address)).Where(image => Active(image.LoadTime, image.UnloadTime, ns)).Distinct().ToArray();
        if (matches.Length == 0 && (address & (1UL << 63)) != 0)
        {
            if (!_kernelMatches.TryGetValue(address, out var inRange))
            {
                inRange = _kernel.Where(image => InRange(image, address)).ToArray();
                _kernelMatches.Add(address, inRange);
            }

            matches = inRange.Where(image => Active(image.LoadTime, image.UnloadTime, ns)).Distinct().ToArray();
        }

        if (matches.Length != 1)
        {
            return null;
        }

        var image = matches[0];
        if (!_cache.TryGetValue((image, address), out var found))
        {
            found = new SampleLocation(image.Path ?? image.FileName,
                !_symbolsLoaded ? "[Symbols not loaded]" :
                image.GetSymbol(new Address(address))?.FunctionName ?? "[Unresolved function]");
            _cache.Add((image, address), found);
        }

        return found;
    }

    private static bool Active(TraceTimestamp? start, TraceTimestamp? end, long ns) =>
        (start is null || start.Value.RelativeTimestamp.Nanoseconds <= ns) &&
        (end is null || ns < end.Value.RelativeTimestamp.Nanoseconds);

    private IImage[] RangeMatches(IProcess process, ulong address)
    {
        if (!_rangeMatches.TryGetValue((process, address), out var found))
        {
            // IProcess.Images is materialized once per process; enumerating it per frame is the hot spot.
            if (!_images.TryGetValue(process, out var images))
            {
                images = process.Images.ToArray();
                _images.Add(process, images);
            }

            found = images.Where(image => InRange(image, address)).ToArray();
            _rangeMatches.Add((process, address), found);
        }

        return found;
    }

    private static bool InRange(IImage image, ulong address) =>
        address >= unchecked((ulong)image.AddressRange.BaseAddress.Value) &&
        address - unchecked((ulong)image.AddressRange.BaseAddress.Value) < (ulong)image.AddressRange.Size.Bytes;
}
