using Microsoft.Win32.SafeHandles;
using WpaFusion.Tbrw.Native;

namespace WpaFusion.Tbrw;

/// <summary>Owns a TBRW_PTR returned by TBRW_open; releases it with TBRW_close.</summary>
internal sealed class SafeTbrwHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeTbrwHandle()
        : base(ownsHandle: true)
    {
    }

    internal static SafeTbrwHandle Open(string path, uint access)
    {
        TbrwLibrary.EnsureLoaded();
        var code = TbrwNative.TBRW_open(out var raw, path, access);
        var handle = new SafeTbrwHandle();
        handle.SetHandle(raw);
        if (code != 0)
        {
            handle.SetHandleAsInvalid();
            throw new TbrwException($"TBRW_open('{path}')", code);
        }

        return handle;
    }

    protected override bool ReleaseHandle()
    {
        return TbrwNative.TBRW_close(handle) == 0;
    }
}
