using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Compile with KeyboardPassthrough.cs, KeyboardCaptureHook.cs and KeyboardPassthroughForm.cs.
// Both transport and capture hook are injected; no physical port or global hook is used.
static class ApplicationIcon
{
    public static readonly Icon Value = SystemIcons.Application;
}

class KeyboardPassthroughUiCheck
{
    static int passed;
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static object Field(object value, string name) { return value.GetType().GetField(name, PrivateInstance).GetValue(value); }
    static void SetField(object value, string name, object item) { value.GetType().GetField(name, PrivateInstance).SetValue(value, item); }
    static void FormEvent(Form form, string name) { typeof(Form).GetMethod(name, PrivateInstance).Invoke(form, new object[] { EventArgs.Empty }); }
    static bool Wait(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        do { Application.DoEvents(); if (condition()) return true; Thread.Sleep(10); } while (clock.ElapsedMilliseconds < timeout);
        Application.DoEvents(); return condition();
    }
    static bool FilterRegistered(IMessageFilter filter)
    {
        // Verify actual registration, rather than only the closing flag. This
        // suite targets the same .NET Framework Windows Forms runtime as the app.
        Type type = typeof(Application).GetNestedType("ThreadContext", BindingFlags.NonPublic);
        object context = type.GetMethod("FromCurrent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        IList filters = (IList)type.GetField("messageFilters", PrivateInstance).GetValue(context);
        return filters != null && filters.Contains(filter);
    }

    sealed class FakeCaptureHook : IKeyboardCaptureHook
    {
        public bool IsActive { get; private set; }
        public bool ThrowOnStart, Disposed;
        public int Starts, Stops;
        Action<int, IntPtr, IntPtr> callback;
        public event Action<Exception> Failed;
        public void Start(IntPtr window, Action<int, IntPtr, IntPtr> onKey) {
            Starts++;
            if (ThrowOnStart) throw new InvalidOperationException("simulated hook installation failure");
            callback = onKey; IsActive = true;
        }
        public void Stop() { Stops++; IsActive = false; callback = null; }
        public void Emit(int message, int key, int scan) {
            if (IsActive && callback != null) callback(message, new IntPtr(key), new IntPtr(1 | (scan << 16)));
        }
        public void Fail() {
            Stop(); var handler = Failed;
            if (handler != null) handler(new IOException("simulated capture failure"));
        }
        public void Dispose() { Stop(); Disposed = true; }
    }

    sealed class FakeTransport : IKeyboardPassthroughTransport
    {
        readonly object gate = new object();
        readonly Queue<string> replies = new Queue<string>();
        readonly List<string> writes = new List<string>();
        readonly AutoResetEvent incoming = new AutoResetEvent(false);
        public volatile bool Closed, Disposed, FailNextReport, HoldIdentity, IdentityRequested;
        public void Open() { }
        public void WriteLine(string command) {
            lock (gate) {
                writes.Add(command);
                if (command == "PING") {
                    IdentityRequested = true;
                    if (!HoldIdentity) replies.Enqueue("ESP32S3-TEXTKEY/1 FW=1.4 HID=1 HID_TIMEOUT=1000");
                }
                else if (command.StartsWith("HID ", StringComparison.Ordinal)) {
                    if (command != "HID OFF" && FailNextReport) { FailNextReport = false; throw new IOException("simulated UI transport failure"); }
                    replies.Enqueue("OK HID");
                } else throw new IOException("unexpected protocol command");
                incoming.Set();
            }
        }
        public string ReadLine() {
            lock (gate) if (replies.Count != 0) return replies.Dequeue();
            incoming.WaitOne(20);
            lock (gate) if (replies.Count != 0) return replies.Dequeue();
            throw new TimeoutException();
        }
        public int Count(string command) {
            lock (gate) { int count = 0; foreach (string value in writes) if (value == command) count++; return count; }
        }
        public void CompleteIdentity() {
            lock (gate) { replies.Enqueue("ESP32S3-TEXTKEY/1 FW=1.4 HID=1 HID_TIMEOUT=1000"); incoming.Set(); }
        }
        public void Close() { Closed = true; }
        public void Dispose() { Disposed = true; }
    }

    sealed class Fixture : IDisposable
    {
        public readonly FakeTransport Transport = new FakeTransport();
        public readonly FakeCaptureHook Hook = new FakeCaptureHook();
        public readonly KeyboardPassthroughSession Session;
        public readonly KeyboardPassthroughForm Form;
        public readonly KeyboardReportState Keys;
        public readonly Button Pause;
        public Fixture() {
            Session = new KeyboardPassthroughSession(Transport, 0);
            Form = new KeyboardPassthroughForm(Session, Hook);
            Keys = (KeyboardReportState)Field(Form, "keys");
            Pause = (Button)Field(Form, "pause");
            Check(((CheckBox)Field(Form, "blockComputerShortcuts")).Checked, "computer shortcut blocking must default on");
            IntPtr handle = Form.Handle;
            // Run the actual Shown handler without displaying or activating a
            // window. It registers the filter and starts the injected session.
            FormEvent(Form, "OnShown");
            Check(Wait(delegate { return (bool)Field(Form, "ready"); }, 2500), "form did not receive Ready callback");
            Check(Pause.Enabled, "Ready did not enable the pause/resume control");
            Check(FilterRegistered(Form), "Shown did not register the form message filter");
            Check(Wait(delegate { return Transport.Count("HID OFF") >= 2; }, 1000), "hidden ready form did not remain paused/released");
        }
        public void Hold() {
            KeyboardReport report; string warning;
            Check(Keys.HandleKeyMessage(0x100, new IntPtr(0x57), new IntPtr(0x00110001), out report, out warning), "fixture W key was not held");
            Check(Keys.HandleKeyMessage(0x100, new IntPtr(0x10), new IntPtr(0x002A0001), out report, out warning), "fixture Shift key was not held");
            // Simulate the state reached after foreground capture, without
            // synthesizing global input or relying on foreground focus.
            SetField(Form, "paused", false);
            Hook.Start(Form.Handle, delegate { });
            typeof(KeyboardPassthroughForm).GetMethod("ShowHeld", PrivateInstance).Invoke(Form, new object[] { report });
            Check(Session.Submit(report), "fixture held state was not submitted");
        }
        public void KeysCleared() {
            KeyboardReport report; string warning;
            Check(!Keys.HandleKeyMessage(0x101, new IntPtr(0x57), new IntPtr(0x00110001), out report, out warning), "W remained held after lifecycle release");
            Check(!Keys.HandleKeyMessage(0x101, new IntPtr(0x10), new IntPtr(0x002A0001), out report, out warning), "Shift remained held after lifecycle release");
            Check(((Label)Field(Form, "held")).Text == "当前没有按住的按键。", "held-key label was not cleared");
        }
        public void Dispose() { Form.Dispose(); }
        public void CheckDisposed() {
            Dispose();
            Check(Form.IsDisposed, "form was not disposed");
            Check(!FilterRegistered(Form), "disposed form left a global message filter registered");
            Check(!Hook.IsActive && Hook.Disposed, "disposed form left the capture hook active or undisposed");
            Check(Transport.Closed && Transport.Disposed && !Session.IsReady, "form disposal did not close/dispose its session transport");
            Check(!Session.Submit(new KeyboardReport(0, 4)), "disposed form session accepted another key");
            Dispose(); // .NET controls and session disposal must be idempotent.
        }
    }

    static void DeactivationCase()
    {
        using (var fixture = new Fixture()) {
            fixture.Hold();
            Check(Wait(delegate { return fixture.Transport.Count("HID 2 26 0 0 0 0 0") > 0; }, 1000), "held keys did not reach fake transport");
            int releases = fixture.Transport.Count("HID OFF");
            FormEvent(fixture.Form, "OnDeactivate");
            Check((bool)Field(fixture.Form, "paused"), "Deactivated did not pause capture");
            Check(!fixture.Hook.IsActive, "Deactivated did not stop keyboard capture hook");
            fixture.KeysCleared();
            Check(Wait(delegate { return fixture.Transport.Count("HID OFF") > releases; }, 1000), "Deactivated did not send a real session release");
            Check(fixture.Pause.Enabled && fixture.Pause.Text == "继续直通", "lost-focus UI did not offer manual resume");
            Message message = Message.Create(fixture.Form.Handle, 0x100, new IntPtr(0x41), new IntPtr(0x001E0001));
            Check(!fixture.Form.PreFilterMessage(ref message), "paused form consumed keyboard input");
            fixture.CheckDisposed();
        }
    }

    static void StoppedCase()
    {
        using (var fixture = new Fixture()) {
            fixture.Hold(); fixture.Session.Stop();
            Check(Wait(delegate { return fixture.Transport.Closed && !(bool)Field(fixture.Form, "ready") && !fixture.Pause.Enabled; }, 2500), "Stopped callback did not disable the UI");
            Check((bool)Field(fixture.Form, "paused"), "Stopped callback did not pause capture");
            Check(!fixture.Hook.IsActive, "Stopped callback left keyboard capture hook active");
            fixture.KeysCleared();
            Check(((Label)Field(fixture.Form, "state")).Text == "键盘直通已停止。", "Stopped status was not displayed");
            fixture.CheckDisposed();
        }
    }

    static void ErrorCase()
    {
        using (var fixture = new Fixture()) {
            fixture.Transport.FailNextReport = true; fixture.Hold();
            Check(Wait(delegate { return fixture.Transport.Closed && (bool)Field(fixture.Form, "failed") && !fixture.Pause.Enabled; }, 2500), "transport error did not reach the form");
            Check(!(bool)Field(fixture.Form, "ready") && (bool)Field(fixture.Form, "paused"), "failed form remained active");
            Check(!fixture.Hook.IsActive, "transport failure left keyboard capture hook active");
            fixture.KeysCleared();
            Label state = (Label)Field(fixture.Form, "state");
            Check(state.ForeColor == Color.Firebrick && state.Text.Contains("simulated UI transport failure"), "Stopped callback replaced the actual error status");
            Check(!String.IsNullOrWhiteSpace(((Label)Field(fixture.Form, "warning")).Text), "failed form did not explain how to reconnect");
            fixture.CheckDisposed();
        }
    }

    static void DeactivatedBeforeReadyCase()
    {
        var transport = new FakeTransport { HoldIdentity = true };
        var hook = new FakeCaptureHook();
        using (var session = new KeyboardPassthroughSession(transport, 0))
        using (var form = new KeyboardPassthroughForm(session, hook)) {
            try {
                // Display only this isolated test form. No system keystrokes
                // are synthesized, and its session uses only the fake transport.
                form.Show(); form.Activate();
                Check(Wait(delegate { return transport.IdentityRequested && GetForegroundWindow() == form.Handle; }, 1200),
                    "delayed-handshake fixture did not become the foreground window");
                Check(!(bool)Field(form, "ready"), "delayed handshake became ready before the focus transition");
                FormEvent(form, "OnDeactivate");
                form.Activate();
                Check(GetForegroundWindow() == form.Handle, "fixture did not return to the foreground before Ready");
                transport.CompleteIdentity();
                Check(Wait(delegate { return (bool)Field(form, "ready"); }, 1500), "delayed handshake did not finish");
                Check((bool)Field(form, "paused"), "Ready automatically resumed after losing focus during connection");
                Button pause = (Button)Field(form, "pause");
                Check(pause.Enabled && pause.Text == "继续直通", "Ready did not require an explicit manual resume");
                pause.PerformClick();
                Check(!(bool)Field(form, "paused") && pause.Text == "暂停直通", "manual resume did not enable foreground capture");
                Check(hook.IsActive, "manual resume did not start the injected capture hook");
                CaptureModeChecks(form, hook, transport);
            } finally { form.Close(); }
        }
        Check(transport.Closed && transport.Disposed, "delayed-handshake fixture did not close its transport");
        Check(hook.Disposed && !hook.IsActive, "delayed-handshake fixture leaked its capture hook");
    }

    static void CaptureModeChecks(KeyboardPassthroughForm form, FakeCaptureHook hook, FakeTransport transport)
    {
                // Reuse the foreground fixture: closing it and opening a second
                // one can let Windows return activation to the launching app.
                Check(GetForegroundWindow() == form.Handle, "capture-mode fixture lost foreground activation");
                Button pause = (Button)Field(form, "pause");
                if ((bool)Field(form, "paused")) pause.PerformClick();
                CheckBox block = (CheckBox)Field(form, "blockComputerShortcuts");
                var keys = (KeyboardReportState)Field(form, "keys");
                Check(block.Checked && hook.IsActive && !(bool)Field(form, "paused"), "default capture did not start the injected hook");
                Message queued = Message.Create(form.Handle, 0x100, new IntPtr(0x42), new IntPtr(0x00300001));
                Check(form.PreFilterMessage(ref queued), "hook mode did not consume a queued window key message");
                KeyboardReport report; string warning;
                Check(!keys.HandleKeyMessage(0x101, new IntPtr(0x42), new IntPtr(0x00300001), out report, out warning), "window filter forwarded a second path while hook mode was active");
                hook.Emit(0x100, 0x41, 0x1E);
                Check(Wait(delegate { return transport.Count("HID 0 4 0 0 0 0 0") > 0; }, 1000), "hook callback did not forward a key to the session");
                int releases = transport.Count("HID OFF");
                block.Checked = false;
                Check(!hook.IsActive && !(bool)Field(form, "paused"), "turning blocking off did not switch to ordinary window capture");
                Check(Wait(delegate { return transport.Count("HID OFF") > releases; }, 1000), "switching capture mode did not release held keys");
                Check(!keys.HandleKeyMessage(0x101, new IntPtr(0x41), new IntPtr(0x001E0001), out report, out warning), "capture mode switch left a key held");
                Message ordinary = Message.Create(form.Handle, 0x100, new IntPtr(0x57), new IntPtr(0x00110001));
                Check(form.PreFilterMessage(ref ordinary), "unblocked mode stopped handling window key messages");
                Check(Wait(delegate { return transport.Count("HID 0 26 0 0 0 0 0") > 0; }, 1000), "old window filter did not forward with blocking disabled");
                releases = transport.Count("HID OFF"); block.Checked = true;
                Check(hook.IsActive && !(bool)Field(form, "paused"), "turning blocking on did not restore hook capture");
                Check(Wait(delegate { return transport.Count("HID OFF") > releases; }, 1000), "restoring hook mode did not release ordinary-mode keys");

                pause.PerformClick();
                Check((bool)Field(form, "paused") && !hook.IsActive, "pause button left the hook active");
                int starts = hook.Starts;
                block.Checked = false; block.Checked = true;
                Check((bool)Field(form, "paused") && !hook.IsActive && hook.Starts == starts, "changing an option while paused resumed capture");
                hook.ThrowOnStart = true; pause.PerformClick();
                Check((bool)Field(form, "paused") && !hook.IsActive, "hook installation failure did not keep capture paused");
                Check(((Label)Field(form, "warning")).Text.Contains("simulated hook installation failure"), "hook installation failure was not displayed");
                hook.ThrowOnStart = false; pause.PerformClick();
                Check(hook.IsActive && !(bool)Field(form, "paused"), "manual retry after installation failure did not resume");
                hook.Emit(0x100, 0x41, 0x1E);
                releases = transport.Count("HID OFF"); hook.Fail();
                Check((bool)Field(form, "paused") && !hook.IsActive, "capture failure left forwarding active");
                Check(Wait(delegate { return transport.Count("HID OFF") > releases; }, 1000), "capture failure did not release the device keys");
                Check(((Label)Field(form, "warning")).Text.Contains("simulated capture failure"), "capture failure explanation was lost");
                Check(!keys.HandleKeyMessage(0x101, new IntPtr(0x41), new IntPtr(0x001E0001), out report, out warning), "capture failure retained its key state");
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string report = Path.GetFullPath(args[1]);
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            DeactivationCase(); StoppedCase(); ErrorCase(); DeactivatedBeforeReadyCase();
            File.WriteAllText(report, "PASS " + passed + " checks: lifecycle release, UI states, filter cleanup, delayed-Ready focus race, capture mode switching, no duplicate forwarding, hook failures, and cleanup. Injected fake transport and capture hook; briefly displayed isolated forms, no global hook, real keyboard input, serial port, or user script.\r\n", new UTF8Encoding(false));
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\n", new UTF8Encoding(false));
            return 1;
        }
    }
}
