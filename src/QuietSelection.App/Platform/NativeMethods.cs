using System.Runtime.InteropServices;

namespace QuietSelection.App.Platform;

internal static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, nint fullName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetCurrentPackageFullName")]
    private static extern int GetCurrentPackageFullNameBuffer(ref uint length, [Out] char[] fullName);
    private const int AppModelErrorNoPackage = 15700;
    internal const int ErrorNotFoundHResult = unchecked((int)0x80070490);

    /// <summary>True when running with MSIX package identity (required for system OCR).</summary>
    internal static bool IsPackaged { get { uint length = 0; return GetCurrentPackageFullName(ref length, 0) != AppModelErrorNoPackage; } }

    // Full name: Name_Version_Architecture_ResourceId_PublisherId.
    internal static string? PackageVersion
    {
        get
        {
            uint length = 0;
            if (GetCurrentPackageFullName(ref length, 0) == AppModelErrorNoPackage || length == 0) return null;
            var buffer = new char[length];
            if (GetCurrentPackageFullNameBuffer(ref length, buffer) != 0) return null;
            var parts = new string(buffer, 0, (int)length - 1).Split('_');
            return parts.Length > 1 ? parts[1] : null;
        }
    }
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
    // Returns a status (0 = text found); see SelectionUia.cpp. Both strings are freed with qs_free.
    [DllImport("QuietSelection.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qs_read_selection(nint source, uint maximum, out nint text, out nint diagnostic);

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
