using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading;

// Immutable USB boot-keyboard state: eight modifiers and at most six ordinary keys.
public sealed class KeyboardReport
{
    readonly byte[] keys = new byte[6];
    public byte Modifiers { get; private set; }
    public byte[] Keys { get { return (byte[])keys.Clone(); } }
    public bool IsEmpty {
        get {
            if (Modifiers != 0) return false;
            foreach (byte key in keys) if (key != 0) return false;
            return true;
        }
    }

    public KeyboardReport(byte modifiers, params byte[] usages)
    {
        if (usages == null) throw new ArgumentNullException("usages");
        if (usages.Length > 6) throw new ArgumentException("USB 键盘最多同时按下 6 个普通键。", "usages");
        Modifiers = modifiers;
        var seen = new HashSet<byte>();
        for (int index = 0; index < usages.Length; index++) {
            byte usage = usages[index];
            if (usage != 0 && (usage < 4 || usage >= 224 || !seen.Add(usage)))
                throw new ArgumentException("USB 普通键报告包含无效或重复键。", "usages");
            keys[index] = usage;
        }
    }

    internal string Command()
    {
        var value = new StringBuilder("HID ");
        value.Append(Modifiers.ToString(CultureInfo.InvariantCulture));
        foreach (byte key in keys) value.Append(' ').Append(key.ToString(CultureInfo.InvariantCulture));
        return value.ToString();
    }
}

public sealed class KeyboardReportState
{
    // Store physical identities, including rejected seventh keys, until their key-up.
    // A rejected key never becomes a delayed press when another slot becomes free.
    readonly Dictionary<int, byte> down = new Dictionary<int, byte>();
    readonly List<byte> ordinary = new List<byte>();

    public KeyboardReport Clear()
    {
        down.Clear(); ordinary.Clear();
        return new KeyboardReport(0);
    }

    public bool HandleKeyMessage(int message, IntPtr wParam, IntPtr lParam,
                                out KeyboardReport report, out string warning)
    {
        report = null; warning = null;
        bool pressed = message == 0x100 || message == 0x104;
        if (!pressed && message != 0x101 && message != 0x105) return false;
        long flags = lParam.ToInt64();
        int virtualKey = (int)wParam.ToInt64();
        int scan = (int)((flags >> 16) & 255);
        bool extended = (flags & 0x01000000L) != 0;
        // Bit 30 also suppresses held keys after focus was lost and Clear was called.
        if (pressed && (flags & 0x40000000L) != 0) return false;
        byte usage = MapKey(virtualKey, scan, extended);
        if (usage == 0) {
            if (pressed) warning = "这个按键不属于支持的标准 USB 键盘按键。";
            return false;
        }
        // Pause and NumLock may share a scan code; their VK distinguishes them.
        int identity = (virtualKey == 0x13 || virtualKey == 0x90) ? 0x20000 + virtualKey :
            (scan != 0 ? scan | (extended ? 0x100 : 0) : 0x10000 + virtualKey);
        byte previous;
        if (pressed) {
            if (down.ContainsKey(identity)) return false;
            if (usage < 224 && !ordinary.Contains(usage) && ordinary.Count == 6) {
                down.Add(identity, 0);
                warning = "最多同时按下 6 个普通键，另支持 8 个左右修饰键；超出的键本次未发送，请松开后重按。";
                return false;
            }
            down.Add(identity, usage);
            if (usage < 224 && !ordinary.Contains(usage)) ordinary.Add(usage);
        } else {
            if (!down.TryGetValue(identity, out previous)) return false;
            down.Remove(identity);
            if (previous == 0) return false;
            if (previous < 224 && !down.ContainsValue(previous)) ordinary.Remove(previous);
        }
        byte modifiers = 0;
        foreach (byte held in down.Values)
            if (held >= 224) modifiers |= (byte)(1 << (held - 224));
        report = new KeyboardReport(modifiers, ordinary.ToArray());
        return true;
    }

    // Windows Set-1 scan codes preserve key positions when the PC layout changes.
    // USB usages follow HID Usage Tables, Keyboard/Keypad page (0x07).
    static byte MapKey(int vk, int scan, bool extended)
    {
        if (vk == 0x13) return 0x48; // Pause
        if (vk == 0x2C) return 0x46; // PrintScreen
        if (vk == 0x90) return 0x53; // NumLock
        if (vk == 0x91) return 0x47; // ScrollLock
        if (vk == 0xA0) return 0xE1;
        if (vk == 0xA1) return 0xE5;
        if (vk == 0xA2) return 0xE0;
        if (vk == 0xA3) return 0xE4;
        if (vk == 0xA4) return 0xE2;
        if (vk == 0xA5) return 0xE6;
        if (vk == 0x10) return scan == 0x36 ? (byte)0xE5 : (byte)0xE1;
        if (vk == 0x11) return extended ? (byte)0xE4 : (byte)0xE0;
        if (vk == 0x12) return extended ? (byte)0xE6 : (byte)0xE2;
        if (vk == 0x5B) return 0xE3;
        if (vk == 0x5C) return 0xE7;
        if (vk >= 0x7C && vk <= 0x87) return (byte)(0x68 + vk - 0x7C); // F13..F24
        if (extended) {
            switch (scan) {
                case 0x1C: return 0x58; // keypad Enter
                case 0x35: return 0x54; // keypad /
                case 0x47: return 0x4A; // Home
                case 0x48: return 0x52; // Up
                case 0x49: return 0x4B; // PageUp
                case 0x4B: return 0x50; // Left
                case 0x4D: return 0x4F; // Right
                case 0x4F: return 0x4D; // End
                case 0x50: return 0x51; // Down
                case 0x51: return 0x4E; // PageDown
                case 0x52: return 0x49; // Insert
                case 0x53: return 0x4C; // Delete
                case 0x5D: return 0x65; // Application/Menu
            }
            // Do not confuse browser/media E0 scan codes with letter positions.
            return MapVirtualKey(vk, extended);
        }
        if (scan >= 0x02 && scan <= 0x0A) return (byte)(0x1E + scan - 0x02);
        if (scan >= 0x3B && scan <= 0x44) return (byte)(0x3A + scan - 0x3B);
        switch (scan) {
            case 0x01: return 0x29; case 0x0B: return 0x27;
            case 0x0C: return 0x2D; case 0x0D: return 0x2E;
            case 0x0E: return 0x2A; case 0x0F: return 0x2B;
            case 0x10: return 0x14; case 0x11: return 0x1A; case 0x12: return 0x08;
            case 0x13: return 0x15; case 0x14: return 0x17; case 0x15: return 0x1C;
            case 0x16: return 0x18; case 0x17: return 0x0C; case 0x18: return 0x12;
            case 0x19: return 0x13; case 0x1A: return 0x2F; case 0x1B: return 0x30;
            case 0x1C: return 0x28; case 0x1D: return 0xE0;
            case 0x1E: return 0x04; case 0x1F: return 0x16; case 0x20: return 0x07;
            case 0x21: return 0x09; case 0x22: return 0x0A; case 0x23: return 0x0B;
            case 0x24: return 0x0D; case 0x25: return 0x0E; case 0x26: return 0x0F;
            case 0x27: return 0x33; case 0x28: return 0x34; case 0x29: return 0x35;
            case 0x2A: return 0xE1; case 0x2B: return 0x31;
            case 0x2C: return 0x1D; case 0x2D: return 0x1B; case 0x2E: return 0x06;
            case 0x2F: return 0x19; case 0x30: return 0x05; case 0x31: return 0x11;
            case 0x32: return 0x10; case 0x33: return 0x36; case 0x34: return 0x37;
            case 0x35: return 0x38; case 0x36: return 0xE5; case 0x37: return 0x55;
            case 0x38: return 0xE2; case 0x39: return 0x2C; case 0x3A: return 0x39;
            case 0x45: return 0x53; case 0x46: return 0x47;
            case 0x47: return 0x5F; case 0x48: return 0x60; case 0x49: return 0x61;
            case 0x4A: return 0x56; case 0x4B: return 0x5C; case 0x4C: return 0x5D;
            case 0x4D: return 0x5E; case 0x4E: return 0x57; case 0x4F: return 0x59;
            case 0x50: return 0x5A; case 0x51: return 0x5B; case 0x52: return 0x62;
            case 0x53: return 0x63; case 0x56: return 0x64;
            case 0x57: return 0x44; case 0x58: return 0x45;
        }
        return MapVirtualKey(vk, extended);
    }

    static byte MapVirtualKey(int vk, bool extended)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (byte)(4 + vk - 0x41);
        if (vk >= 0x31 && vk <= 0x39) return (byte)(0x1E + vk - 0x31);
        if (vk >= 0x70 && vk <= 0x7B) return (byte)(0x3A + vk - 0x70);
        if (vk >= 0x61 && vk <= 0x69) return (byte)(0x59 + vk - 0x61);
        switch (vk) {
            case 0x30: return 0x27; case 0x08: return 0x2A; case 0x09: return 0x2B;
            case 0x0D: return extended ? (byte)0x58 : (byte)0x28;
            case 0x14: return 0x39; case 0x1B: return 0x29; case 0x20: return 0x2C;
            case 0x21: return 0x4B; case 0x22: return 0x4E; case 0x23: return 0x4D;
            case 0x24: return 0x4A; case 0x25: return 0x50; case 0x26: return 0x52;
            case 0x27: return 0x4F; case 0x28: return 0x51; case 0x2D: return 0x49;
            case 0x2E: return 0x4C; case 0x5D: return 0x65; case 0x60: return 0x62;
            case 0x6A: return 0x55; case 0x6B: return 0x57; case 0x6C: return 0x85;
            case 0x6D: return 0x56; case 0x6E: return 0x63; case 0x6F: return 0x54;
            case 0xBA: return 0x33; case 0xBB: return 0x2E; case 0xBC: return 0x36;
            case 0xBD: return 0x2D; case 0xBE: return 0x37; case 0xBF: return 0x38;
            case 0xC0: return 0x35; case 0xDB: return 0x2F; case 0xDC: return 0x31;
            case 0xDD: return 0x30; case 0xDE: return 0x34; case 0xE2: return 0x64;
        }
        return 0;
    }
}

// ReadLine must return or throw TimeoutException within about 100 ms, and
// WriteLine must have a finite timeout. Only the session's worker calls transport.
public interface IKeyboardPassthroughTransport : IDisposable
{
    void Open();
    void WriteLine(string value);
    string ReadLine();
    void Close();
}

sealed class SerialKeyboardPassthroughTransport : IKeyboardPassthroughTransport
{
    readonly SerialPort serial;
    public SerialKeyboardPassthroughTransport(string port)
    {
        serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One);
        serial.DtrEnable = true; serial.RtsEnable = true;
        serial.Handshake = Handshake.None;
        serial.Encoding = Encoding.ASCII; serial.NewLine = "\n";
        serial.ReadTimeout = 100; serial.WriteTimeout = 250;
    }
    public void Open() { serial.Open(); serial.DiscardInBuffer(); }
    public void WriteLine(string value) { serial.WriteLine(value); }
    public string ReadLine() { return serial.ReadLine(); }
    public void Close() { serial.Close(); }
    public void Dispose() { serial.Dispose(); }
}

public sealed class KeyboardPassthroughErrorEventArgs : EventArgs
{
    public string Message { get; private set; }
    public KeyboardPassthroughErrorEventArgs(string message) { Message = message; }
}

public sealed class KeyboardPassthroughSession : IDisposable
{
    const int QueueLimit = 128;
    const int AcknowledgementTimeoutMs = 600;
    const int HeartbeatMs = 200;
    readonly object gate = new object();
    readonly AutoResetEvent work = new AutoResetEvent(false);
    readonly Queue<KeyboardReport> pending = new Queue<KeyboardReport>();
    readonly IKeyboardPassthroughTransport transport;
    readonly int initializationDelayMs;
    Thread worker;
    bool started, stopping, disposed, finished, ready, releaseRequested;
    string requestedError;

    public event EventHandler Ready;
    public event EventHandler Stopped;
    public event EventHandler<KeyboardPassthroughErrorEventArgs> Error;
    public bool IsReady { get { lock (gate) return ready && !stopping && !disposed; } }

    public KeyboardPassthroughSession(string port)
        : this(new SerialKeyboardPassthroughTransport(port), 1200) { }

    public KeyboardPassthroughSession(IKeyboardPassthroughTransport transport, int initializationDelayMs)
    {
        if (transport == null) throw new ArgumentNullException("transport");
        if (initializationDelayMs < 0 || initializationDelayMs > 10000)
            throw new ArgumentOutOfRangeException("initializationDelayMs");
        this.transport = transport; this.initializationDelayMs = initializationDelayMs;
    }

    public void Start()
    {
        lock (gate) {
            if (disposed) throw new ObjectDisposedException("KeyboardPassthroughSession");
            if (started || stopping) return;
            started = true;
            worker = new Thread(Run);
            worker.IsBackground = true; worker.Name = "TextSender keyboard passthrough";
            worker.Start();
        }
    }

    // No repeat coalescing: a quick down/up must reach the device as two reports.
    public bool Submit(KeyboardReport report)
    {
        if (report == null) throw new ArgumentNullException("report");
        lock (gate) {
            if (!ready || stopping || disposed) return false;
            if (pending.Count >= QueueLimit) {
                requestedError = "按键发送队列已满，直通已停止并释放按键；请重新连接。";
                stopping = true; ready = false; pending.Clear(); releaseRequested = true;
                work.Set(); return false;
            }
            pending.Enqueue(report); work.Set(); return true;
        }
    }

    public void ReleaseAll()
    {
        lock (gate) {
            if (disposed || stopping) return;
            pending.Clear(); releaseRequested = true;
            work.Set();
        }
    }

    public void Stop()
    {
        lock (gate) {
            if (stopping) return;
            stopping = true; ready = false; pending.Clear(); releaseRequested = true;
            if (!disposed) work.Set();
        }
    }

    bool IsStopping { get { lock (gate) return stopping; } }

    void Run()
    {
        bool hidAvailable = false;
        string error = null;
        try {
            transport.Open();
            var initialization = Stopwatch.StartNew();
            while (!IsStopping && initialization.ElapsedMilliseconds < initializationDelayMs)
                work.WaitOne((int)Math.Max(1, Math.Min(100, initializationDelayMs - initialization.ElapsedMilliseconds)));
            if (IsStopping) return;
            transport.WriteLine("PING");
            ReadIdentity();
            if (IsStopping) return;
            hidAvailable = true;
            Exchange("HID OFF");
            lock (gate) {
                if (stopping) return;
                releaseRequested = false; ready = true;
            }
            Notify(Ready);
            KeyboardReport held = new KeyboardReport(0);
            var sinceSent = Stopwatch.StartNew();
            while (!IsStopping) {
                KeyboardReport report = null;
                bool release;
                lock (gate) {
                    release = releaseRequested;
                    if (release) releaseRequested = false;
                    else if (pending.Count != 0) report = pending.Dequeue();
                }
                if (release) {
                    Exchange("HID OFF");
                    held = new KeyboardReport(0); sinceSent.Restart();
                } else if (report != null) {
                    Exchange(report.Command());
                    held = report; sinceSent.Restart();
                } else if (!held.IsEmpty && sinceSent.ElapsedMilliseconds >= HeartbeatMs) {
                    Exchange(held.Command()); sinceSent.Restart();
                } else {
                    int wait = held.IsEmpty ? HeartbeatMs : Math.Max(1, HeartbeatMs - (int)sinceSent.ElapsedMilliseconds);
                    work.WaitOne(wait);
                }
            }
        } catch (Exception failure) {
            error = "键盘直通已停止：" + failure.Message;
        } finally {
            lock (gate) {
                ready = false; stopping = true; pending.Clear();
                if (requestedError != null) error = requestedError;
            }
            if (hidAvailable) {
                // A broken ACK or disconnected device must never replay queued keys.
                // Try one explicit release, then let the firmware's 1000ms watchdog cover loss of link.
                try { Exchange("HID OFF"); } catch { }
            }
            try { transport.Close(); } catch { }
            try { transport.Dispose(); } catch { }
            lock (gate) { finished = true; work.Dispose(); }
            if (error != null) NotifyError(error);
            Notify(Stopped);
        }
    }

    void ReadIdentity()
    {
        var elapsed = Stopwatch.StartNew();
        while (!IsStopping && elapsed.ElapsedMilliseconds < 2000) {
            string response;
            try { response = transport.ReadLine(); }
            catch (TimeoutException) { continue; }
            if (response == null) throw new InvalidOperationException("串口连接已关闭。");
            string[] fields = response.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields[0] == "ESP32S3-TEXTKEY/1") {
                if (Array.IndexOf(fields, "HID=1") < 0)
                    throw new InvalidOperationException("键盘直通需要 TextKeyboard v1.4 固件（HID=1），当前固件不支持。");
                return;
            }
            if (response.StartsWith("ERR", StringComparison.Ordinal))
                throw new InvalidOperationException("设备拒绝连接检查；键盘直通需要 TextKeyboard v1.4 固件（HID=1）。");
        }
        if (!IsStopping)
            throw new TimeoutException("未识别到支持 HID=1 的 TextKeyboard；键盘直通需要 v1.4 固件。");
    }

    void Exchange(string command)
    {
        transport.WriteLine(command);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < AcknowledgementTimeoutMs) {
            string response;
            try { response = transport.ReadLine(); }
            catch (TimeoutException) { continue; }
            if (response == null) throw new InvalidOperationException("串口连接已关闭。");
            response = response.Trim();
            if (response == "OK HID") return;
            // Do not include a serial reply or the command in errors: they may contain key data.
            throw new InvalidOperationException("设备未确认 HID 按键报告，已停止发送。");
        }
        throw new TimeoutException("等待 HID 确认超时，已停止发送。");
    }

    void Notify(EventHandler handler)
    {
        if (handler == null) return;
        try { handler(this, EventArgs.Empty); } catch { }
    }

    void NotifyError(string message)
    {
        var handler = Error;
        if (handler == null) return;
        try { handler(this, new KeyboardPassthroughErrorEventArgs(message)); } catch { }
    }

    public void Dispose()
    {
        Thread current;
        lock (gate) {
            if (disposed) return;
            stopping = true; ready = false; pending.Clear(); releaseRequested = true;
            disposed = true; current = worker;
            if (started && !finished) work.Set();
        }
        if (current == null) {
            try { transport.Dispose(); } finally { work.Dispose(); }
        } else if (current != Thread.CurrentThread) {
            // Every I/O has a finite timeout; the worker owns eventual cleanup.
            current.Join(1500);
        }
    }
}
