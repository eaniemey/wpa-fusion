using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WpaFusion.Capture.Clocks;

/// <summary>
/// Raw clock reads for sync points. .NET has no rdtsc intrinsic, so a 12-byte x64 stub is placed
/// in executable memory once and called through a function pointer.
/// Kernel32 clock interop lives here; TBRW interop lives in WpaFusion.Tbrw.
/// </summary>
internal static unsafe class NativeClock
{
    // rdtsc; shl rdx, 32; or rax, rdx; ret
    private static readonly byte[] RdtscStub = { 0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0xC3 };

    private static readonly delegate* unmanaged<ulong> Rdtsc = CreateRdtsc();

    public static long QpcFrequency => Stopwatch.Frequency;

    /// <summary>Reads the TSC of the current CPU.</summary>
    public static ulong ReadTsc() => Rdtsc();

    /// <summary>QueryPerformanceCounter (Stopwatch uses it on Windows).</summary>
    public static long ReadQpc() => Stopwatch.GetTimestamp();

    /// <summary>GetSystemTimePreciseAsFileTime, in UTC FILETIME ticks.</summary>
    public static long ReadFileTimeUtc()
    {
        GetSystemTimePreciseAsFileTime(out var ft);
        return ft;
    }

    private static delegate* unmanaged<ulong> CreateRdtsc()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("WPA-Fusion capture requires x64.");
        }

        // Ownership: this page is allocated once for the process lifetime and never freed.
        var mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommit | MemReserve, PageReadWrite);
        if (mem == IntPtr.Zero)
        {
            throw new InvalidOperationException($"VirtualAlloc failed: {Marshal.GetLastWin32Error()}.");
        }

        Marshal.Copy(RdtscStub, 0, mem, RdtscStub.Length);
        if (!VirtualProtect(mem, (UIntPtr)4096, PageExecuteRead, out _))
        {
            throw new InvalidOperationException($"VirtualProtect failed: {Marshal.GetLastWin32Error()}.");
        }

        FlushInstructionCache(GetCurrentProcess(), mem, (UIntPtr)RdtscStub.Length);
        return (delegate* unmanaged<ulong>)mem;
    }

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteRead = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
}
