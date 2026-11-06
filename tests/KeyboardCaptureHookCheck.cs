using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Compile with src/KeyboardCaptureHook.cs. FakeNative is the only backend used;
// this executable never installs a Windows hook or generates system key input.
class KeyboardCaptureHookCheck
{
    static int passed;
    static readonly IntPtr Target = new IntPtr(101), Other = new IntPtr(202);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }

    sealed class KeyEvent
    {
        public int Message, VirtualKey;
        public long Flags;
        public KeyEvent(int message, IntPtr virtualKey, IntPtr flags) { Message = message; VirtualKey = virtualKey.ToInt32(); Flags = flags.ToInt64(); }
    }

    sealed class FakeNative : IKeyboardCaptureNative
    {
        public IntPtr Foreground = Target, InstalledHandle = new IntPtr(303), NextResult = new IntPtr(404);
        public readonly HashSet<int> Held = new HashSet<int>();
        public readonly HashSet<int> ToggleOnly = new HashSet<int>();
        public KeyboardHookProcedure Callback;
        public int Installs, Uninstalls, NextCalls;
        public bool ThrowInstall, ThrowForeground;
        public IntPtr ForegroundWindow { get { if (ThrowForeground) throw new InvalidOperationException("simulated foreground lookup failure"); return Foreground; } }
        public int LastError { get { return 5; } }
        public short GetAsyncKeyState(int virtualKey) { return Held.Contains(virtualKey) ? unchecked((short)0x8000) : (short)(ToggleOnly.Contains(virtualKey) ? 1 : 0); }
        public IntPtr Install(KeyboardHookProcedure callback) {
            Installs++; if (ThrowInstall) throw new InvalidOperationException("simulated hook install failure");
            Callback = callback; return InstalledHandle;
        }
        public bool Uninstall(IntPtr hook) { Uninstalls++; Check(hook == InstalledHandle, "native unhook received the wrong handle"); return true; }
        public IntPtr CallNext(IntPtr hook, int code, IntPtr wParam, IntPtr lParam) { NextCalls++; return NextResult; }
        public IntPtr Emit(int message, uint key, uint scan, uint flags) {
            var data = new KeyboardHookData { VirtualKey = key, ScanCode = scan, Flags = flags, Time = 123, ExtraInfo = UIntPtr.Zero };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(KeyboardHookData)));
            try {
                Marshal.StructureToPtr(data, pointer, false);
                return Callback(0, new IntPtr(message), pointer);
            } finally { Marshal.FreeHGlobal(pointer); }
        }
    }

    static void ForegroundAndFlags()
    {
        var native = new FakeNative(); var events = new List<KeyEvent>();
        using (var hook = new KeyboardCaptureHook(native)) {
            hook.Start(Target, delegate(int message, IntPtr key, IntPtr flags) { events.Add(new KeyEvent(message, key, flags)); });
            Check(hook.IsActive && native.Installs == 1, "Start did not install and activate one hook");
            Check(native.Callback(-1, new IntPtr(0x100), IntPtr.Zero) == native.NextResult && events.Count == 0, "negative hook code was not passed onward");
            Check(native.Callback(0, new IntPtr(0x200), IntPtr.Zero) == native.NextResult && events.Count == 0, "non-keyboard hook message was processed");
            Check(native.Emit(0x100, 0x41, 0x1E, 0) == new IntPtr(1), "foreground key-down was not blocked on the computer");
            Check(events.Count == 1 && events[0].Message == 0x100 && events[0].VirtualKey == 0x41, "foreground key-down was not forwarded once");
            Check(((events[0].Flags >> 16) & 255) == 0x1E && (events[0].Flags & 0x41000000L) == 0, "initial key-down scan/repeat/extended flags are incorrect");
            Check(native.Emit(0x100, 0x41, 0x1E, 0) == new IntPtr(1), "repeat escaped the active foreground hook");
            Check(events.Count == 2 && (events[1].Flags & 0x40000000L) != 0, "repeat was not marked for the state mapper");
            Check(native.Emit(0x101, 0x41, 0x1E, 0x80) == new IntPtr(1), "captured key-up was not blocked");
            Check(events.Count == 3 && (events[2].Flags & 0x80000000L) != 0, "key-up flag was not preserved");
            Check(native.Emit(0x101, 0x41, 0x1E, 0x80) == native.NextResult && events.Count == 3, "unknown key-up was consumed or replayed");

            native.Emit(0x100, 0xA2, 0x1D, 0); // left Ctrl
            native.Emit(0x100, 0xA3, 0x1D, 1); // right Ctrl
            Check(events[3].VirtualKey == 0xA2 && (events[3].Flags & 0x01000000L) == 0, "left Ctrl was changed into right Ctrl");
            Check(events[4].VirtualKey == 0xA3 && (events[4].Flags & 0x01000000L) != 0, "right Ctrl lost its extended flag");
            native.Emit(0x101, 0xA2, 0x1D, 0x80);
            native.Emit(0x101, 0xA3, 0x1D, 0x81);
            native.Emit(0x104, 0xA5, 0x38, 0x21); // right Alt, system context
            KeyEvent alt = events[events.Count - 1];
            Check(alt.Message == 0x104 && alt.VirtualKey == 0xA5 && (alt.Flags & 0x21000000L) == 0x21000000L, "system Alt/extended flags were lost");
            Check(native.Emit(0x104, 0x09, 0x0F, 0x20) == new IntPtr(1), "foreground Alt+Tab key was not captured");
            native.Emit(0x105, 0x09, 0x0F, 0xA0); native.Emit(0x105, 0xA5, 0x38, 0x81);
            Check(native.Emit(0x100, 0x5B, 0x5B, 1) == new IntPtr(1), "foreground Windows key was not captured");
            native.Emit(0x101, 0x5B, 0x5B, 0x81);

            int before = events.Count; native.Foreground = Other;
            Check(native.Emit(0x100, 0x42, 0x30, 0) == native.NextResult, "background key-down was blocked");
            Check(native.Emit(0x101, 0x42, 0x30, 0x80) == native.NextResult && events.Count == before, "background key was forwarded to the device");
            hook.Stop(); Check(!hook.IsActive && native.Uninstalls == 1, "Stop did not uninstall the hook");
            native.Foreground = Target;
            Check(native.Emit(0x100, 0x43, 0x2E, 0) == native.NextResult && events.Count == before, "callback after Stop still captured a key");
            hook.Stop(); Check(native.Uninstalls == 1, "repeated Stop uninstalled twice");
        }
        Check(native.Uninstalls == 1, "Dispose after Stop repeated native cleanup");
    }

    static void InitiallyHeldKeys()
    {
        var native = new FakeNative(); var events = new List<KeyEvent>();
        native.Held.Add(0x41); native.Held.Add(0xA2); native.ToggleOnly.Add(0x42);
        using (var hook = new KeyboardCaptureHook(native)) {
            hook.Start(Target, delegate(int message, IntPtr key, IntPtr flags) { events.Add(new KeyEvent(message, key, flags)); });
            Check(native.Emit(0x100, 0x41, 0x1E, 0) == native.NextResult && events.Count == 0, "pre-existing held ordinary key was replayed");
            Check(native.Emit(0x100, 0xA2, 0x1D, 0) == native.NextResult && events.Count == 0, "pre-existing held modifier was replayed");
            Check(native.Emit(0x101, 0x41, 0x1E, 0x80) == native.NextResult, "pre-existing key-up was blocked");
            Check(native.Emit(0x101, 0xA2, 0x1D, 0x80) == native.NextResult && events.Count == 0, "pre-existing modifier release was forwarded");
            Check(native.Emit(0x100, 0x41, 0x1E, 0) == new IntPtr(1) && events.Count == 1, "fresh press after initial key-up was not accepted");
            Check((events[0].Flags & 0x40000000L) == 0, "fresh press retained the old repeat state");
            Check(native.Emit(0x100, 0x42, 0x30, 0) == new IntPtr(1) && events.Count == 2, "low toggle bit was mistaken for a held key");
        }
        Check(native.Uninstalls == 1, "active Dispose did not release native hook");
    }

    static void FailuresAndCleanup()
    {
        var rejected = new FakeNative { InstalledHandle = IntPtr.Zero };
        using (var hook = new KeyboardCaptureHook(rejected)) {
            bool threw = false;
            try { hook.Start(Target, delegate { }); } catch (Win32Exception) { threw = true; }
            Check(threw && !hook.IsActive && rejected.Uninstalls == 0, "native installation failure was hidden or left an active hook");
            rejected.InstalledHandle = new IntPtr(303); hook.Start(Target, delegate { });
            Check(hook.IsActive, "installation failure prevented a later retry");
        }
        Check(rejected.Uninstalls == 1, "successful retry was not cleaned up");

        var throwsInstall = new FakeNative { ThrowInstall = true };
        using (var hook = new KeyboardCaptureHook(throwsInstall)) {
            bool threw = false;
            try { hook.Start(Target, delegate { }); } catch (InvalidOperationException) { threw = true; }
            Check(threw && !hook.IsActive && throwsInstall.Uninstalls == 0, "thrown installation error left hook state active");
        }

        var callbackFailure = new FakeNative(); int failures = 0; string message = null;
        using (var hook = new KeyboardCaptureHook(callbackFailure)) {
            hook.Failed += delegate(Exception error) { failures++; message = error.Message; };
            hook.Start(Target, delegate { throw new IOException("simulated consumer failure"); });
            callbackFailure.Emit(0x100, 0x41, 0x1E, 0);
            Check(!hook.IsActive && callbackFailure.Uninstalls == 1, "callback exception did not uninstall before returning to Windows");
            Check(failures == 1 && message == "simulated consumer failure", "callback exception was not reported exactly once");
            Check(callbackFailure.Emit(0x101, 0x41, 0x1E, 0x80) == callbackFailure.NextResult && failures == 1, "failed hook kept intercepting or re-reporting failure");
        }
        var nativeFailure = new FakeNative(); failures = 0;
        using (var hook = new KeyboardCaptureHook(nativeFailure)) {
            hook.Failed += delegate { failures++; };
            hook.Start(Target, delegate { }); nativeFailure.ThrowForeground = true;
            nativeFailure.Emit(0x100, 0x41, 0x1E, 0);
            Check(!hook.IsActive && failures == 1 && nativeFailure.Uninstalls == 1, "native callback-path exception escaped cleanup");
        }
    }

    static void HeldAcrossPause()
    {
        // A swallowed down may not appear in GetAsyncKeyState. A restarted
        // foreground hook must swallow its old repeat/up until a fresh press.
        var native = new FakeNative(); var events = new List<KeyEvent>();
        Action<int, IntPtr, IntPtr> receive = delegate(int message, IntPtr key, IntPtr flags) { events.Add(new KeyEvent(message, key, flags)); };
        using (var hook = new KeyboardCaptureHook(native)) {
            hook.Start(Target, receive);
            native.Emit(0x100, 0x41, 0x1E, 0);
            native.Emit(0x100, 0x5B, 0x5B, 1);
            Check(events.Count == 2, "pause fixture did not capture its initial ordinary/Windows keys");
            hook.Stop(); hook.Start(Target, receive);
            Check(native.Held.Count == 0, "pause fixture must simulate swallowed keys absent from async state");
            int onward = native.NextCalls;
            Check(native.Emit(0x100, 0x41, 0x1E, 0) == new IntPtr(1), "old ordinary held key escaped to Windows after resume");
            Check(native.Emit(0x100, 0x5B, 0x5B, 1) == new IntPtr(1), "old Windows-key repeat escaped after resume");
            Check(native.Emit(0x101, 0x41, 0x1E, 0x80) == new IntPtr(1), "old ordinary key-up escaped after resume");
            Check(native.Emit(0x101, 0x5B, 0x5B, 0x81) == new IntPtr(1), "old Windows-key release could activate the Start menu");
            Check(events.Count == 2 && native.NextCalls == onward, "pre-pause keys were replayed or passed onward after resume");
            Check(native.Emit(0x100, 0x41, 0x1E, 0) == new IntPtr(1) && events.Count == 3, "fresh ordinary press was blocked after old key-up");
            Check((events[2].Flags & 0x40000000L) == 0, "fresh resumed press was marked as auto-repeat");
            Check(native.Emit(0x100, 0x5B, 0x5B, 1) == new IntPtr(1) && events.Count == 4, "fresh Windows-key press did not capture after old release");
            native.Emit(0x101, 0x41, 0x1E, 0x80); native.Emit(0x101, 0x5B, 0x5B, 0x81);
        }

        var sided = new FakeNative(); events.Clear();
        sided.Held.Add(0x11); sided.Held.Add(0xA3);
        using (var hook = new KeyboardCaptureHook(sided)) {
            hook.Start(Target, receive);
            Check(sided.Emit(0x100, 0x11, 0x1D, 0) == new IntPtr(1) && events.Count == 1, "pre-held right Ctrl incorrectly suppressed newly pressed left Ctrl");
            Check(sided.Emit(0x100, 0x11, 0x1D, 1) == sided.NextResult && events.Count == 1, "generic VK right Ctrl did not match its initially-held side");
            Check(sided.Emit(0x101, 0x11, 0x1D, 0x81) == sided.NextResult, "initially-held right Ctrl release was swallowed");
            Check(sided.Emit(0x100, 0x11, 0x1D, 1) == new IntPtr(1) && events.Count == 2, "right Ctrl did not start independently after its old release");
            sided.Emit(0x101, 0x11, 0x1D, 0x80); sided.Emit(0x101, 0x11, 0x1D, 0x81);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string report = Path.GetFullPath(args[1]);
        try {
            ForegroundAndFlags(); InitiallyHeldKeys(); FailuresAndCleanup(); HeldAcrossPause();
            File.WriteAllText(report, "PASS " + passed + " checks: foreground interception, background pass-through, pre-held suppression, held-key pause/resume, independent modifiers, extended/repeat flags, installation failure, callback failure, Stop and Dispose. Fake native backend only; no Windows hook or system key input.\r\n", new UTF8Encoding(false));
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\n", new UTF8Encoding(false));
            return 1;
        }
    }
}
