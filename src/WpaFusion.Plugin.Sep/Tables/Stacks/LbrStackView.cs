using System.Globalization;

namespace WpaFusion.Plugin.Sep.Tables;

/// <summary>
/// Per-sample view of the LBR call stacks for table projections: frames outermost first, the full stack text
/// and the leaf. Works without a sidecar (every cell then says LBR was not collected). Stack text is built on
/// demand because distinct stacks can number in the hundreds of thousands.
/// </summary>
internal sealed class LbrStackView
{
    public const int FrameDepth = 16;
    public const string NotCollected = "[No LBR stacks: capture with --sep-lbr, then run open-run.ps1]";

    private readonly LbrStackAttribution? _map;
    private readonly string[] _frameLabel;
    private readonly string?[] _stackText;

    public LbrStackView(LbrStackAttribution? map)
    {
        _map = map;
        _frameLabel = map?.Frames.Select(f => string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(f.Module)}!{f.Function}")).ToArray()
            ?? Array.Empty<string>();
        _stackText = new string?[map?.Stacks.Length ?? 0];
    }

    public bool HasStacks => _map is not null;

    /// <summary>Frame at <paramref name="depth"/> counted from the outermost recorded caller (0 = Frame 1).</summary>
    public string FrameAt(int sample, int depth)
    {
        if (_map is null)
        {
            return depth == 0 ? NotCollected : string.Empty;
        }

        var stack = _map.Stacks[_map.StackId[sample]];
        var k = stack.Length - 1 - depth;
        return k >= 0 ? _frameLabel[stack[k]] : string.Empty;
    }

    /// <summary>The full stack, leaf first.</summary>
    public string Stack(int sample)
    {
        if (_map is null)
        {
            return NotCollected;
        }

        var id = _map.StackId[sample];
        return _stackText[id] ??= string.Join(" <- ", _map.Stacks[id].Select(f => _frameLabel[f]));
    }

    public string LeafModule(int sample) => _map is null ? NotCollected : _map.Frames[_map.Stacks[_map.StackId[sample]][0]].Module;

    public string LeafFunction(int sample) => _map is null ? NotCollected : _map.Frames[_map.Stacks[_map.StackId[sample]][0]].Function;
}
