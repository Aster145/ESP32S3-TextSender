using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// Compile with src/KeyboardPassthrough.cs. Every session below uses FakeTransport;
// no physical port, global keyboard hook, or user script is used by this suite.
class KeyboardPassthroughCheck
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static bool Wait(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        do { if (condition()) return true; Thread.Sleep(10); } while (clock.ElapsedMilliseconds < timeout);
        return condition();
    }
    static IntPtr KeyData(int scan, bool extended, bool repeat)
    {
        return new IntPtr(1 | (scan << 16) | (extended ? 1 << 24 : 0) | (repeat ? 1 << 30 : 0));
    }
    static KeyboardReport Key(KeyboardReportState state, int key, int scan, bool extended, bool down, bool expectedChange, bool repeat)
    {
        KeyboardReport report; string warning;
        bool changed = state.HandleKeyMessage(down ? 0x100 : 0x101, new IntPtr(key), KeyData(scan, extended, repeat), out report, out warning);
        Check(changed == expectedChange, "unexpected state change for VK " + key + (down ? " down" : " up"));
        return report;
    }
    static KeyboardReport Down(KeyboardReportState state, int key, int scan, bool extended)
    {
        return Key(state, key, scan, extended, true, true, false);
    }
    static KeyboardReport Up(KeyboardReportState state, int key, int scan, bool extended)
    {
        return Key(state, key, scan, extended, false, true, false);
    }
    static void Report(KeyboardReport report, byte modifiers, params byte[] expected)
    {
        Check(report != null, "changed key state did not return a report");
        Check(report.Modifiers == modifiers, "modifier bits differ from physically held keys");
        byte[] keys = report.Keys;
        Check(keys.Length == 6, "HID report must have exactly six ordinary key slots");
        var actual = new List<byte>();
        foreach (byte key in keys) if (key != 0) actual.Add(key);
        actual.Sort(); var wanted = new List<byte>(expected); wanted.Sort();
        Check(actual.Count == wanted.Count, "ordinary key count differs");
        for (int i = 0; i < wanted.Count; i++) Check(actual[i] == wanted[i], "ordinary HID usage differs");
        Check(report.IsEmpty == (modifiers == 0 && expected.Length == 0), "IsEmpty does not include modifiers");
    }
    static void TapMapping(int virtualKey, int scan, bool extended, byte usage)
    {
        var state = new KeyboardReportState();
        Report(Down(state, virtualKey, scan, extended), 0, usage);
        Report(Up(state, virtualKey, scan, extended), 0);
    }
    static void StateChecks()
    {
        var state = new KeyboardReportState();
        Report(Down(state, 0x57, 0x11, false), 0, 0x1A); // W
        Report(Down(state, 0x10, 0x2A, false), 2, 0x1A); // left Shift
        Report(Down(state, 0x20, 0x39, false), 2, 0x1A, 0x2C);
        Report(Up(state, 0x20, 0x39, false), 2, 0x1A); // Space up preserves W/Shift
        Report(Up(state, 0x10, 0x2A, false), 0, 0x1A);
        Report(Up(state, 0x57, 0x11, false), 0);

        Report(Down(state, 0x11, 0x1D, false), 1);
        Report(Down(state, 0x11, 0x1D, true), 0x11);
        Report(Up(state, 0x11, 0x1D, false), 0x10);
        Report(Up(state, 0x11, 0x1D, true), 0);
        int[] modifiers = { 0x10, 0x10, 0x11, 0x11, 0x12, 0x12, 0x5B, 0x5C };
        int[] scans = { 0x2A, 0x36, 0x1D, 0x1D, 0x38, 0x38, 0x5B, 0x5C };
        bool[] extended = { false, false, false, true, false, true, true, true };
        byte[] masks = { 2, 0x20, 1, 0x10, 4, 0x40, 8, 0x80 };
        byte held = 0;
        for (int i = 0; i < modifiers.Length; i++) {
            held |= masks[i]; Report(Down(state, modifiers[i], scans[i], extended[i]), held);
        }
        for (int i = 0; i < modifiers.Length; i++) {
            held &= (byte)~masks[i]; Report(Up(state, modifiers[i], scans[i], extended[i]), held);
        }

        // System-key messages must use the same state machine as ordinary keys.
        KeyboardReport report; string warning;
        Check(state.HandleKeyMessage(0x104, new IntPtr(0x12), KeyData(0x38, false, false), out report, out warning), "WM_SYSKEYDOWN ignored");
        Report(report, 4);
        Check(state.HandleKeyMessage(0x105, new IntPtr(0x12), KeyData(0x38, false, false), out report, out warning), "WM_SYSKEYUP ignored");
        Report(report, 0);

        TapMapping(0x25, 0x4B, true, 0x50); TapMapping(0x26, 0x48, true, 0x52);
        TapMapping(0x27, 0x4D, true, 0x4F); TapMapping(0x28, 0x50, true, 0x51);
        TapMapping(0x09, 0x0F, false, 0x2B); TapMapping(0x0D, 0x1C, false, 0x28);
        TapMapping(0x1B, 0x01, false, 0x29); TapMapping(0x08, 0x0E, false, 0x2A);
        TapMapping(0x31, 0x02, false, 0x1E); TapMapping(0x30, 0x0B, false, 0x27);
        TapMapping(0x61, 0x4F, false, 0x59); TapMapping(0x60, 0x52, false, 0x62);
        TapMapping(0x0D, 0x1C, true, 0x58); TapMapping(0x6E, 0x53, false, 0x63);
        TapMapping(0x6F, 0x35, true, 0x54); TapMapping(0x6A, 0x37, false, 0x55);
        TapMapping(0x6D, 0x4A, false, 0x56); TapMapping(0x6B, 0x4E, false, 0x57);
        // Physical scan codes keep numpad identity even when NumLock changes VK.
        TapMapping(0x23, 0x4F, false, 0x59); TapMapping(0x2D, 0x52, false, 0x62);
        TapMapping(0x23, 0x4F, true, 0x4D); TapMapping(0x2D, 0x52, true, 0x49);

        Report(Down(state, 0x41, 0x1E, false), 0, 4);
        Key(state, 0x41, 0x1E, false, true, false, true);
        Key(state, 0x41, 0x1E, false, true, false, false);
        Report(state.Clear(), 0);
        // Returning focus with a held key must not recreate it from auto-repeat.
        Key(state, 0x41, 0x1E, false, true, false, true);
        Key(state, 0x41, 0x1E, false, false, false, false);
        Report(Down(state, 0x41, 0x1E, false), 0, 4);
        Report(Up(state, 0x41, 0x1E, false), 0);
        Check(!state.HandleKeyMessage(0x100, new IntPtr(0xFF), IntPtr.Zero, out report, out warning), "unsupported VK was accepted");
        Check(!state.HandleKeyMessage(0x102, new IntPtr('x'), IntPtr.Zero, out report, out warning), "WM_CHAR was treated as a physical key");

        Down(state, 0x11, 0x1D, false);
        for (int key = 0x41; key <= 0x46; key++) Down(state, key, 0, false);
        Check(!state.HandleKeyMessage(0x100, new IntPtr(0x47), KeyData(0, false, false), out report, out warning), "seventh ordinary key was accepted");
        Check(!String.IsNullOrWhiteSpace(warning), "six-key limit did not provide a warning");
        Report(Up(state, 0x41, 0, false), 1, 5, 6, 7, 8, 9);
        Key(state, 0x47, 0, false, true, false, true);
        Key(state, 0x47, 0, false, false, false, false);
        Report(Down(state, 0x47, 0, false), 1, 5, 6, 7, 8, 9, 10);
        Report(Up(state, 0x47, 0, false), 1, 5, 6, 7, 8, 9);
        Report(state.Clear(), 0);
        for (int key = 0x42; key <= 0x46; key++) Key(state, key, 0, false, false, false, false);
        Key(state, 0x11, 0x1D, false, false, false, false);
        Report(state.Clear(), 0);

        byte[] original = { 4, 5 };
        var immutable = new KeyboardReport(2, original);
        original[0] = 9; byte[] returned = immutable.Keys; returned[1] = 10;
        Report(immutable, 2, 4, 5);
    }

    sealed class FakeTransport : IKeyboardPassthroughTransport
    {
        readonly object sync = new object();
        readonly Queue<string> replies = new Queue<string>();
        readonly List<string> writes = new List<string>();
        readonly AutoResetEvent available = new AutoResetEvent(false);
        public readonly ManualResetEvent Held = new ManualResetEvent(false);
        public string Identity = "ESP32S3-TEXTKEY/1 FW=1.4 HID=1 HID_TIMEOUT=1000";
        public volatile bool HoldNextReport, FailNextReport;
        public volatile bool Opened, Closed, Disposed;
        public volatile int Reads;
        public void Open() { lock (sync) Opened = true; }
        public void WriteLine(string command)
        {
            lock (sync) {
                writes.Add(command);
                if (command == "PING") { replies.Enqueue(Identity); available.Set(); return; }
                if (command.StartsWith("HID ", StringComparison.Ordinal)) {
                    if (command != "HID OFF" && FailNextReport) { FailNextReport = false; throw new IOException("simulated transport write failure"); }
                    if (command != "HID OFF" && HoldNextReport) { HoldNextReport = false; Held.Set(); return; }
                    replies.Enqueue("OK HID"); available.Set(); return;
                }
                throw new IOException("unexpected command: " + command);
            }
        }
        public string ReadLine()
        {
            lock (sync) { Reads++; if (replies.Count != 0) return replies.Dequeue(); }
            available.WaitOne(20);
            lock (sync) { if (replies.Count != 0) return replies.Dequeue(); }
            throw new TimeoutException();
        }
        public void Acknowledge() { lock (sync) { replies.Enqueue("OK HID"); available.Set(); } }
        public string[] Writes { get { lock (sync) return writes.ToArray(); } }
        public int Count(string command) { int count = 0; foreach (string item in Writes) if (item == command) count++; return count; }
        public void Close() { lock (sync) Closed = true; available.Set(); }
        public void Dispose() { lock (sync) Disposed = true; }
    }

    sealed class SessionFixture : IDisposable
    {
        public readonly FakeTransport Transport = new FakeTransport();
        public readonly KeyboardPassthroughSession Session;
        public readonly ManualResetEvent Ready = new ManualResetEvent(false), Stopped = new ManualResetEvent(false), Failed = new ManualResetEvent(false);
        public int ReadyCount;
        public string Error;
        public SessionFixture() {
            Session = new KeyboardPassthroughSession(Transport, 0);
            Session.Ready += delegate { Interlocked.Increment(ref ReadyCount); Ready.Set(); };
            Session.Stopped += delegate { Stopped.Set(); };
            Session.Error += delegate(object sender, KeyboardPassthroughErrorEventArgs args) { Error = args.Message; Failed.Set(); };
        }
        public void Start() {
            Session.Start(); Check(Ready.WaitOne(2500), "new firmware session did not become ready");
            Check(Session.IsReady, "Ready event preceded usable session");
            Check(Transport.Opened, "session did not open its injected transport");
            string[] writes = Transport.Writes;
            Check(writes.Length >= 2 && writes[0] == "PING" && writes[1] == "HID OFF", "handshake did not clear keys before readiness");
        }
        public void Stop() {
            Session.Stop(); Check(Stopped.WaitOne(2500), "Stop did not complete the worker");
            Check(!Session.IsReady && Transport.Closed, "stopped session remained ready/open");
            Check(!Session.Submit(new KeyboardReport(0, 4)), "stopped session accepted a key report");
            string[] writes = Transport.Writes;
            Check(writes.Length != 0 && writes[writes.Length - 1] == "HID OFF", "Stop did not finish with release");
            int count = writes.Length, reads = Transport.Reads;
            Thread.Sleep(250);
            Check(Transport.Writes.Length == count && Transport.Reads == reads, "worker kept performing I/O after Stop");
        }
        public void Dispose() { Session.Dispose(); }
    }

    static string Wire(byte modifiers, params byte[] keys)
    {
        var line = new StringBuilder("HID " + modifiers);
        for (int index = 0; index < 6; index++) line.Append(' ').Append(index < keys.Length ? keys[index] : 0);
        return line.ToString();
    }
    static void SessionChecks()
    {
        using (var old = new SessionFixture()) {
            old.Transport.Identity = "ESP32S3-TEXTKEY/1 FW=1.3 TYPE2=1";
            old.Session.Start();
            Check(old.Failed.WaitOne(2500) && old.Stopped.WaitOne(2500), "old firmware was not rejected and stopped");
            Check(old.ReadyCount == 0 && !old.Session.IsReady, "old firmware became ready");
            Check(!String.IsNullOrWhiteSpace(old.Error), "old firmware rejection had no explanation");
            foreach (string line in old.Transport.Writes) Check(line == "PING" || line == "HID OFF", "old firmware received a key report");
            Check(old.Transport.Closed, "rejected session leaked its transport");
        }
        using (var wrongCapability = new SessionFixture()) {
            wrongCapability.Transport.Identity = "ESP32S3-TEXTKEY/1 FW=1.4 HID=10";
            wrongCapability.Session.Start();
            Check(wrongCapability.Failed.WaitOne(2500) && wrongCapability.Stopped.WaitOne(2500), "HID=10 incorrectly satisfied HID=1");
            Check(wrongCapability.ReadyCount == 0, "inexact capability was accepted");
        }
        using (var fixture = new SessionFixture()) {
            fixture.Start();
            Check(fixture.Session.Submit(new KeyboardReport(0, 4)), "quick key-down rejected");
            Check(fixture.Session.Submit(new KeyboardReport(0)), "quick key-up rejected");
            Check(Wait(delegate { return fixture.Transport.Count(Wire(0)) > 0; }, 1500), "quick key-up was lost");
            string[] writes = fixture.Transport.Writes;
            Check(Array.IndexOf(writes, Wire(0, 4)) >= 0 && Array.IndexOf(writes, Wire(0, 4)) < Array.IndexOf(writes, Wire(0)), "down/up were merged or reordered");
            Check(fixture.Session.Submit(new KeyboardReport(2, 0x1A)), "held multi-key report rejected");
            Check(Wait(delegate { return fixture.Transport.Count(Wire(2, 0x1A)) >= 3; }, 1500), "held keys were not maintained by heartbeat");
            fixture.Session.ReleaseAll();
            Check(Wait(delegate { return fixture.Transport.Count("HID OFF") >= 2; }, 1000), "ReleaseAll did not emit OFF");
            int heldCount = fixture.Transport.Count(Wire(2, 0x1A)); Thread.Sleep(350);
            Check(fixture.Transport.Count(Wire(2, 0x1A)) == heldCount, "heartbeat resurrected released keys");
            fixture.Stop();
        }
        using (var fixture = new SessionFixture()) {
            fixture.Start(); fixture.Transport.HoldNextReport = true;
            fixture.Session.Submit(new KeyboardReport(0, 4));
            Check(fixture.Transport.Held.WaitOne(1000), "fake did not block the in-flight report");
            Check(fixture.Session.Submit(new KeyboardReport(0, 5)), "queued key report rejected");
            Check(fixture.Session.Submit(new KeyboardReport(0)), "queued key-up rejected");
            fixture.Session.ReleaseAll(); fixture.Transport.Acknowledge();
            Check(Wait(delegate { return fixture.Transport.Count("HID OFF") >= 2; }, 1000), "focus loss did not release after in-flight ACK");
            Check(fixture.Transport.Count(Wire(0, 5)) == 0 && fixture.Transport.Count(Wire(0)) == 0, "focus loss retained pending reports");
            Check(fixture.Session.Submit(new KeyboardReport(0, 6)), "session did not accept a fresh key after focus release");
            Check(Wait(delegate { return fixture.Transport.Count(Wire(0, 6)) != 0; }, 1000), "fresh key after release was not sent");
            fixture.Stop();
        }
        using (var fixture = new SessionFixture()) {
            fixture.Start(); fixture.Transport.FailNextReport = true;
            fixture.Session.Submit(new KeyboardReport(0, 4));
            Check(fixture.Failed.WaitOne(2000) && fixture.Stopped.WaitOne(2000), "transport exception did not stop session");
            Check(!fixture.Session.IsReady && fixture.Transport.Closed, "failed session leaked transport/ready state");
            Check(fixture.Transport.Count("HID OFF") >= 2, "exception did not attempt release");
            Check(!String.IsNullOrWhiteSpace(fixture.Error), "exception did not provide status");
        }
        using (var fixture = new SessionFixture()) {
            fixture.Start(); fixture.Transport.HoldNextReport = true;
            fixture.Session.Submit(new KeyboardReport(0, 4));
            Check(fixture.Transport.Held.WaitOne(1000), "timeout fixture did not send its report");
            Check(fixture.Failed.WaitOne(2500) && fixture.Stopped.WaitOne(1000), "missing ACK did not cause a bounded failure");
            Check(fixture.Transport.Count("HID OFF") >= 2 && fixture.Transport.Closed, "ACK timeout did not release and close");
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string report = Path.GetFullPath(args[1]);
        try {
            StateChecks(); SessionChecks();
            File.WriteAllText(report, "PASS " + passed + " checks: physical key mappings, independent modifiers, simultaneous keys, repeats, six-key rollover, focus-clear, firmware capability negotiation, queued transitions, heartbeat, release, timeout, and transport failure. Fake transport only; no real serial or user scripts.\r\n", new UTF8Encoding(false));
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\n", new UTF8Encoding(false));
            return 1;
        }
    }
}
