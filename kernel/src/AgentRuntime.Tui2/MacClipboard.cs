using System.Runtime.InteropServices;

namespace AgentRuntime.Tui2;

/// <summary>
/// macOS 原生剪贴板（NSPasteboard）—— **不起进程**（R2：运行时只有工具面可以起进程）。
/// <para>OSC 52 在多数终端可用；NSPasteboard 为 Terminal.app / iTerm 等提供确定的本地路径。</para>
/// <para>只在 macOS 调用；其它平台返回 false，由调用方退回 OSC 52。</para>
/// </summary>
internal static class MacClipboard
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    private static bool _init;
    private static IntPtr _stringType;

    public static bool TrySet(string text)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(text)) return false;

        try
        {
            if (!_init)
            {
                dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 2 /* RTLD_NOW */);
                _stringType = NsString("public.utf8-plain-text");
                _init = true;
            }

            var pasteboard = objc_msgSend(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));
            objc_msgSend(pasteboard, sel_registerName("clearContents"));
            objc_msgSend(pasteboard, sel_registerName("setString:forType:"), NsString(text), _stringType);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IntPtr NsString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return objc_msgSend(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
