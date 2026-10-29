using System.Runtime.InteropServices;

namespace QuietSelection.App.Platform;

internal static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetCurrentPackageFullName(ref uint length, nint fullName);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PlaySound(nint sound, nint module, uint flags);
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qs_voices(out nint result);
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qs_ocr_languages(out nint result, out uint maxDimension);
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int qs_synthesize(string voiceId, string text, double rate, out nint audio, out uint length);
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int qs_sapi_synthesize(string voiceId, string text, int speed, out nint audio, out uint length);
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int qs_recognize(nint pixels, int width, int height, string language, out nint text);
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void qs_free(nint data);

    internal static string TakeString(nint pointer)
    {
        try { return Marshal.PtrToStringUni(pointer) ?? ""; }
        finally { qs_free(pointer); }
    }

    internal static void Check(int result, ErrorCode failure)
    {
        if (result < 0) throw new QuietException(result == unchecked((int)0x80070005) ? ErrorCode.AccessDenied : failure);
    }
}
