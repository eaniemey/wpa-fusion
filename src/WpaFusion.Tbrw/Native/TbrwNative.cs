using System.Runtime.InteropServices;

namespace WpaFusion.Tbrw.Native;

// Layouts ported from SEP 5.52 include/tbrw_types.h (x64). Sizes are asserted in tests
// against the header's TBRW_VERIFY_SIZEOF values. All pointers inside these structs point
// into buffers owned by TBRW (callbacks) or by the caller (TBRW_get_system); see each import.

/// <summary>Top-level sections in a TBRW result file.</summary>
internal enum TbrwSection : uint
{
    Hardware = 0,
    Software = 1,
    Process = 2,
    Module = 3,
    UserDefinedGlobal = 4,
    VersionInfo = 5,
    Thread = 6,
    GlobalDescriptor = 7,
    Auxiliary = 8,
}

/// <summary>Sections that make up an individual TBRW data stream.</summary>
internal enum TbrwStreamSection : uint
{
    EventDescription = 0,
    DataDescription = 1,
    Data = 2,
    UserDefinedStream = 3,
    StreamInfo = 4,
}

/// <summary>Sample record descriptor types (ST_*) used by WPA-Fusion.</summary>
internal enum TbrwDescType : ushort
{
    None = 0,
    LegacySampleRecord = 1,
    TimeStamp = 7,
    BranchTrace = 12,
    DescriptorId = 31,
}

/// <summary>Native TBRW file access flags passed when opening a result.</summary>
internal static class TbrwAccess
{
    public const uint FileRead = 0x1;
    public const uint FileWrite = 0x2;
}

/// <summary>Native union-like value used to refer to an interned string by unique ID.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TbrwStringOrId
{
    public ulong UniqueId;
}

/// <summary>IA-32 CPUID input/output values stored in a native CPU architecture record.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TbrwCpuIdIa32
{
    public uint EaxInput;
    public uint Eax;
    public uint Ebx;
    public uint Ecx;
    public uint Edx;
    public uint Reserved;
}

/// <summary>CPU architecture metadata and its CPUID result array.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TbrwCpuArch
{
    public uint Size;
    public ushort ArchType;
    public ushort NumCpuId;
    public TbrwCpuIdIa32* CpuIdIa32Array;
}

/// <summary>Native processor descriptor, including topology, clocks, and architecture records.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TbrwCpu
{
    public uint Size;
    public uint CpuNumber;
    public uint NativeArchType;
    public uint IntelProcessorNumber;
    public uint SpeedMhz;
    public uint FsbMhz;
    public uint CacheL2;
    public uint CacheL3;
    public long TscOffset;
    public ushort PackageNum;
    public ushort CoreNum;
    public ushort HardwareThreadNum;
    public ushort ThreadsPerCore;
    public ushort NumCpuArch;
    public ushort CoreType;
    public ushort ModuleId;
    public ushort NumModules;
    public TbrwCpuArch* CpuArchArray;
}

/// <summary>NUMA node descriptor containing its processor list and memory information.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TbrwNode
{
    public uint Size;
    public uint TypeFromShell;
    public uint NodeId;
    public uint NumAvailable;
    public uint NumUsed;
    public uint Reserved;
    public ulong PhysicalMemory;
    public TbrwCpu* CpuArray;
}

/// <summary>Native system topology returned by TBRW_get_system.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TbrwSystem
{
    public uint Size;
    public uint NumNodes;
    public uint EnergyMultiplier;
    public uint VmmBits;
    public ulong Reserved2a;
    public ulong Reserved2b;
    public ulong Reserved2c;
    public TbrwNode* NodeArray;
}

/// <summary>Process metadata record enumerated from a TBRW result.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TbrwPid
{
    public uint Size;
    public uint PidId;
    public uint Flags;
    public uint OsId;
    public ulong EventWhen;
    public uint ReservedForLegacy;
    public int ParentPidIndex;
    public TbrwStringOrId Path;
    public TbrwStringOrId Name;
}

/// <summary>Thread metadata record, including the owning process reference.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TbrwTid
{
    public uint Size;
    public uint AssociatedPid;
    public uint TidId;
    public uint Flags;
    public ulong EventWhen;
    public uint ReservedForLegacy;
    public uint OsId;
    public TbrwStringOrId Name;
}

/// <summary>Native SEP event metadata, including the event's sample-after value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TbrwEvent
{
    public uint Size;
    public ushort GroupId;
    public ushort EventId;
    public uint NumDetails;
    public uint Flags;
    public ulong SampleAfterValue;
    public TbrwStringOrId Name;
    public TbrwStringOrId ArchName;
    public void* DetailArray;
}

/// <summary>Descriptor for a field in a variable-layout TBRW sample record.</summary>
[StructLayout(LayoutKind.Sequential, Size = 72)]
internal struct TbrwSampRecDescEntry
{
    public uint Size;
    public uint Offset;
    public ushort Type;
    public ushort Subtype;
    public uint DataSize;
    public ulong AccessOffset;
    public byte AccessType;
    public byte AccessMethod;
    public byte SampleFlag;
    public byte Reserved0;
    public uint Reserved1;
    public TbrwStringOrId Name;
    // 32 bytes of padding follow; covered by Size = 72.
}

/// <summary>TBRW_SampleRecordPC (legacy sample record), 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TbrwSampleRecordPc
{
    public ulong Iip;        // 64-bit IP when IA64PC bit is set; else low 32 bits hold eip.
    public ulong Ipsr;
    public ushort Cs;
    public ushort CpuAndOs;  // cpuNum : 12, notVmid0 : 1, codeMode : 2, uncore_valid : 1
    public uint Tid;
    public uint PidRecIndex;
    public uint BitFields2;  // mrIndex : 20, eventIndex : 8, tidIsRaw : 1, IA64PC : 1, pidRecIndexRaw : 1, mrIndexNone : 1

    public const int CpuOffset = 18;
    public const int TidOffset = 20;
    public const int PidOffset = 24;
    public const int BitFieldsOffset = 28;
}

/// <summary>
/// P/Invoke declarations for the TBRW library. The library name "tbrw" is resolved at runtime
/// by <see cref="TbrwLibrary"/> to SEP's versioned DLL. All functions return 0 on success.
/// Strings are UTF-16; buffer sizes are in bytes.
/// </summary>
internal static unsafe class TbrwNative
{
    public const string Library = "tbrw";

    // Ownership: on success *tbrwPtr is a TBRW-owned handle; release with TBRW_close exactly once.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_open(out IntPtr tbrwPtr, [MarshalAs(UnmanagedType.LPWStr)] string filename, uint accessMode);

    // Ownership: releases the handle. The handle must not be used afterwards.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_close(IntPtr tbrwPtr);

    // Ownership: *errorString points to a static TBRW string; do not free.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_error_string(uint errorCode, out IntPtr errorString);

    // Ownership: caller owns buffer (sizeOfBuffer bytes). Pass buffer = null to query the size.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_convert_uniqueid_to_string(IntPtr tbrwPtr, uint sizeOfBuffer, TbrwStringOrId* stringId, char* buffer, uint* sizeBufferNeeded);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_is_bound(IntPtr tbrwPtr, uint dataStream, out uint isBound);

    // Requires a handle opened with read and write access. Modifies the file in place.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_dobind(IntPtr tbrwPtr, uint dataStream);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_reading_section(IntPtr tbrwPtr, TbrwSection section);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_done_section(IntPtr tbrwPtr, TbrwSection section);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_reading_stream(IntPtr tbrwPtr, uint stream);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_done_stream(IntPtr tbrwPtr, uint stream);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_reading_stream_section(IntPtr tbrwPtr, uint stream, TbrwStreamSection section);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_done_stream_section(IntPtr tbrwPtr, uint stream, TbrwStreamSection section);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_get_number_data_streams(IntPtr tbrwPtr, out uint numStreams);

    // Ownership: caller owns buf (sizeOfBuffer bytes). TBRW lays out nodes, CPUs and CPUID arrays
    // inside buf; the embedded pointers are valid only while buf is alive. Set buf->Size first.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_get_system(IntPtr tbrwPtr, uint sizeOfBuffer, TbrwSystem* buf, uint* sizeBufferUsed);

    // The enumerate functions take a POINTER TO a callback pointer (e.g. TBRW_DATA_CALLBACK *).
    // Callback arguments point into TBRW-owned memory, valid only for the duration of the callback.
    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_enumerate_processes(IntPtr tbrwPtr, void** callback, void* userPtr, ulong startIndex);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_enumerate_threads(IntPtr tbrwPtr, void** callback, void* userPtr, ulong startIndex);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_enumerate_events(IntPtr tbrwPtr, uint stream, void** callback, void* userPtr, ulong startIndex);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_enumerate_data_descriptor_entries(IntPtr tbrwPtr, uint stream, void** callback, void* userPtr);

    [DllImport(Library, ExactSpelling = true)]
    public static extern uint TBRW_enumerate_data(IntPtr tbrwPtr, uint stream, void** callback, void* userPtr, ulong startIndex);
}
