using System.Runtime.InteropServices;
using WpaFusion.Tbrw.Native;

namespace WpaFusion.Tbrw;

/// <summary>A TBRW call returned a non-zero status code.</summary>
public sealed class TbrwException : Exception
{
    public TbrwException(string operation, uint code)
        : base($"{operation} failed with TBRW status {code}: {Describe(code)}")
    {
        Operation = operation;
        Code = code;
    }

    public TbrwException(string message)
        : base(message)
    {
        Operation = string.Empty;
    }

    public TbrwException(string message, Exception inner)
        : base(message, inner)
    {
        Operation = string.Empty;
    }

    public string Operation { get; }

    public uint Code { get; }

    internal static void Check(uint code, string operation)
    {
        if (code != 0)
        {
            throw new TbrwException(operation, code);
        }
    }

    private static string Describe(uint code)
    {
        try
        {
            if (TbrwNative.TBRW_error_string(code, out var text) == 0 && text != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(text) ?? "unknown error";
            }
        }
        catch (DllNotFoundException)
        {
            return "unknown error (TBRW not loaded)";
        }
        catch (EntryPointNotFoundException)
        {
            return "unknown error (TBRW_error_string missing)";
        }

        return "unknown error";
    }
}
