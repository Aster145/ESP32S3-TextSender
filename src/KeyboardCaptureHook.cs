using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

internal interface IKeyboardCaptureHook : IDisposable
{
    bool IsActive { get; }
    event Action<Exception> Failed;
    void Start(IntPtr window, Action<int, IntPtr, IntPtr> onKey);
    void Stop();
}

internal delegate IntPtr KeyboardHookProcedure(int code, IntPtr wParam, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardHookData
{
    public uint VirtualKey, ScanCode, Flags, Time;
    public UIntPtr ExtraInfo;
}

// A thin native boundary permits offline tests without installing a global hook.
internal interface IKeyboardCaptureNative
{
    IntPtr ForegroundWindow { get; }
    short GetAsyncKeyState(int virtualKey);
    IntPtr Install(KeyboardHookProcedure callback);
    bool Uninstall(IntPtr hook);
    IntPtr CallNext(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    int LastError { get; }
}

internal sealed class WindowsKeyboardCaptureNative : IKeyboardCaptureNative
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    static extern short NativeGetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int kind, KeyboardHookProcedure callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    public IntPtr ForegroundWindow { get { return GetForegroundWindow(); } }
    public int LastError { get { return Marshal.GetLastWin32Error(); } }
    public short GetAsyncKeyState(int virtualKey) { return NativeGetAsyncKeyState(virtualKey); }
    public IntPtr Install(KeyboardHookProcedure callback) { return SetWindowsHookEx(13, callback, GetModuleHandle(null), 0); }
    public bool Uninstall(IntPtr hook) { return UnhookWindowsHookEx(hook); }
    public IntPtr CallNext(IntPtr hook, int code, IntPtr wParam, IntPtr lParam) { return CallNextHookEx(hook, code, wParam, lParam); }
}

// Start, Stop, and Dispose belong to the UI thread that pumps the hook messages.
// onKey must only update UI/state and enqueue a report; never perform serial I/O here.
internal sealed class KeyboardCaptureHook : IKeyboardCaptureHook
{
    sealed class Registration
    {
        public IntPtr Handle, Window;
        public KeyboardHookProcedure Callback;
        public Action<int, IntPtr, IntPtr> OnKey;
        public readonly HashSet<int> InitiallyDown = new HashSet<int>();
        public readonly Dictionary<int, int> CapturedDown = new Dictionary<int, int>();
    }

    readonly IKeyboardCaptureNative native;
    readonly HashSet<int> awaitingRelease = new HashSet<int>();
    Registration current;
    bool disposed;
    // If native unhooking fails, keep its delegate alive. The inactive registration
    // only forwards messages, even if this object later starts a fresh registration.
    static readonly List<KeyboardHookProcedure> failedUnhooks = new List<KeyboardHookProcedure>();

    public event Action<Exception> Failed;
    public bool IsActive { get { return current != null && current.Handle != IntPtr.Zero; } }

    public KeyboardCaptureHook() : this(new WindowsKeyboardCaptureNative()) { }
    internal KeyboardCaptureHook(IKeyboardCaptureNative native)
    {
        if (native == null) throw new ArgumentNullException("native");
        this.native = native;
    }

    public void Start(IntPtr window, Action<int, IntPtr, IntPtr> onKey)
    {
        if (disposed) throw new ObjectDisposedException("KeyboardCaptureHook");
        if (window == IntPtr.Zero) throw new ArgumentException("需要有效的直通窗口。", "window");
        if (onKey == null) throw new ArgumentNullException("onKey");
        Stop();
        var registration = new Registration { Window = window, OnKey = onKey };
        // The side-specific modifier VKs are queried separately. Using generic
        // VK_CONTROL here would also suppress a newly pressed opposite Ctrl key.
        for (int key = 1; key < 255; key++) {
            if (key == 0x10 || key == 0x11 || key == 0x12 || awaitingRelease.Contains(key)) continue;
            if ((native.GetAsyncKeyState(key) & 0x8000) != 0) registration.InitiallyDown.Add(key);
        }
        registration.Callback = delegate(int code, IntPtr wParam, IntPtr lParam) {
            return Process(registration, code, wParam, lParam);
        };
        registration.Handle = native.Install(registration.Callback);
        if (registration.Handle == IntPtr.Zero)
            throw new Win32Exception(native.LastError, "无法启用本机快捷键屏蔽，请重新打开键盘直通窗口。");
        current = registration;
    }

    public void Stop()
    {
        Registration registration = current;
        if (registration == null) return;
        // Disable first: an unhook failure must never leave local input swallowed.
        current = null;
        registration.OnKey = null;
        foreach (int key in registration.CapturedDown.Values) awaitingRelease.Add(key);
        registration.InitiallyDown.Clear(); registration.CapturedDown.Clear();
        bool removed = false;
        try { removed = native.Uninstall(registration.Handle); }
        catch { }
        if (!removed) {
            lock (failedUnhooks) failedUnhooks.Add(registration.Callback);
        }
    }

    IntPtr Process(Registration registration, int code, IntPtr wParam, IntPtr lParam)
    {
        try {
            if (code < 0 || current != registration || registration.Handle == IntPtr.Zero)
                return Next(registration, code, wParam, lParam);
            int message = unchecked((int)wParam.ToInt64());
            bool pressed = message == 0x100 || message == 0x104;
            bool released = message == 0x101 || message == 0x105;
            if ((!pressed && !released) || native.ForegroundWindow != registration.Window)
                return Next(registration, code, wParam, lParam);

            var data = (KeyboardHookData)Marshal.PtrToStructure(lParam, typeof(KeyboardHookData));
            int key = SideSpecificKey(data);
            // These downs were suppressed in our previous capture, unlike keys
            // Windows already held before capture began. Keep suppressing their
            // entire first cycle, including up, so a resumed Win/Alt key cannot
            // accidentally become a local shortcut. Never replay it to the target.
            if (awaitingRelease.Contains(key)) {
                if (released) awaitingRelease.Remove(key);
                return new IntPtr(1);
            }
            if (registration.InitiallyDown.Contains(key)) {
                if (released) {
                    registration.InitiallyDown.Remove(key);
                }
                return Next(registration, code, wParam, lParam);
            }
            int identity = PhysicalIdentity(data, key);
            bool repeated = registration.CapturedDown.ContainsKey(identity);
            if (released && !repeated)
                return Next(registration, code, wParam, lParam);
            if (pressed) registration.CapturedDown[identity] = key;
            else registration.CapturedDown.Remove(identity);

            // Match WM_(SYS)KEY message lParam flags used by KeyboardReportState.
            uint flags = 1U | ((data.ScanCode & 255U) << 16);
            if ((data.Flags & 1U) != 0) flags |= 0x01000000U; // LLKHF_EXTENDED
            if ((data.Flags & 0x20U) != 0) flags |= 0x20000000U; // LLKHF_ALTDOWN
            if (repeated || released) flags |= 0x40000000U;
            if (released || (data.Flags & 0x80U) != 0) flags |= 0x80000000U; // LLKHF_UP
            registration.OnKey(message, new IntPtr((long)data.VirtualKey), new IntPtr(unchecked((int)flags)));
            return new IntPtr(1);
        } catch (Exception failure) {
            // An exception must not cross the unmanaged callback boundary.
            if (current == registration) {
                Stop();
                var handler = Failed;
                if (handler != null) { try { handler(failure); } catch { } }
            }
            return Next(registration, code, wParam, lParam);
        }
    }

    static int SideSpecificKey(KeyboardHookData data)
    {
        int key = (int)data.VirtualKey;
        bool extended = (data.Flags & 1U) != 0;
        if (key == 0x10) return data.ScanCode == 0x36 ? 0xA1 : 0xA0;
        if (key == 0x11) return extended ? 0xA3 : 0xA2;
        if (key == 0x12) return extended ? 0xA5 : 0xA4;
        return key;
    }

    static int PhysicalIdentity(KeyboardHookData data, int key)
    {
        if (key == 0x13 || key == 0x90) return 0x20000 + key;
        if (data.ScanCode == 0) return 0x10000 + key;
        return (int)(data.ScanCode & 255U) | ((data.Flags & 1U) != 0 ? 0x100 : 0);
    }

    IntPtr Next(Registration registration, int code, IntPtr wParam, IntPtr lParam)
    {
        try { return native.CallNext(registration.Handle, code, wParam, lParam); }
        catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop(); awaitingRelease.Clear(); disposed = true;
    }
}
