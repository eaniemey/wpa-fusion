using System.Runtime.InteropServices;

namespace WpaFusion.Capture.Clocks;

/// <summary>Temporarily pins the calling thread to the processor it is running on.</summary>
internal sealed class ThreadAffinity : IDisposable
{
    private readonly IntPtr _thread;
    private readonly GroupAffinity _previous;
    private bool _disposed;

    private ThreadAffinity(IntPtr thread, GroupAffinity previous)
    {
        _thread = thread;
        _previous = previous;
    }

    /// <summary>Flat logical CPU index (processors of earlier groups first) the thread is pinned to.</summary>
    public int Cpu { get; private init; }

    /// <summary>Flat logical CPU index of the calling thread, numbered like SEP: 0..N-1 across groups.</summary>
    public static int CurrentCpu()
    {
        GetCurrentProcessorNumberEx(out var n);
        return FlatIndex(n.Group, n.Number);
    }

    private static int FlatIndex(ushort group, byte number)
    {
        var index = (int)number;
        for (ushort g = 0; g < group; g++)
        {
            index += (int)GetActiveProcessorCount(g);
        }

        return index;
    }

    /// <summary>All active logical processors as (group, number within group, flat index).</summary>
    public static IEnumerable<(ushort Group, byte Number, int Flat)> AllProcessors()
    {
        var groups = GetActiveProcessorGroupCount();
        var flat = 0;
        for (ushort g = 0; g < groups; g++)
        {
            var count = GetActiveProcessorCount(g);
            for (var n = 0; n < count; n++)
            {
                yield return (g, (byte)n, flat++);
            }
        }
    }

    /// <summary>Pins the calling thread to a specific processor and yields so it runs there.</summary>
    public static ThreadAffinity PinTo(ushort group, byte number, int flat)
    {
        Thread.BeginThreadAffinity();
        var thread = GetCurrentThread();
        var target = new GroupAffinity { Mask = (UIntPtr)(1UL << number), Group = group };
        if (!SetThreadGroupAffinity(thread, ref target, out var previous))
        {
            Thread.EndThreadAffinity();
            throw new InvalidOperationException($"SetThreadGroupAffinity({group}:{number}) failed: {Marshal.GetLastWin32Error()}.");
        }

        // The new affinity takes effect at the next reschedule; yield until we run on the target.
        var spins = 0;
        while (CurrentCpu() != flat)
        {
            Thread.Sleep(spins++ < 10 ? 0 : 1);
        }

        return new ThreadAffinity(thread, previous) { Cpu = flat };
    }

    public static ThreadAffinity PinToCurrentProcessor()
    {
        Thread.BeginThreadAffinity();
        GetCurrentProcessorNumberEx(out var current);
        var thread = GetCurrentThread();
        var target = new GroupAffinity { Mask = (UIntPtr)(1UL << current.Number), Group = current.Group };
        if (!SetThreadGroupAffinity(thread, ref target, out var previous))
        {
            Thread.EndThreadAffinity();
            throw new InvalidOperationException($"SetThreadGroupAffinity failed: {Marshal.GetLastWin32Error()}.");
        }

        return new ThreadAffinity(thread, previous) { Cpu = FlatIndex(current.Group, current.Number) };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var previous = _previous;
        SetThreadGroupAffinity(_thread, ref previous, out _);
        Thread.EndThreadAffinity();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);
}
