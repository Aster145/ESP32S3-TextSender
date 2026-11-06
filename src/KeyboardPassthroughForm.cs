using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class KeyboardPassthroughForm : Form, IMessageFilter
{
    readonly KeyboardPassthroughSession session;
    readonly IKeyboardCaptureHook captureHook;
    readonly KeyboardReportState keys = new KeyboardReportState();
    readonly Label state = new Label(), held = new Label(), warning = new Label();
    readonly Button pause = new Button();
    readonly CheckBox blockComputerShortcuts = new CheckBox();
    bool ready, paused = true, closing, failed, allowInitialCapture = true;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    public KeyboardPassthroughForm(string port) : this(new KeyboardPassthroughSession(port)) { }

    internal KeyboardPassthroughForm(KeyboardPassthroughSession connection)
        : this(connection, new KeyboardCaptureHook()) { }

    internal KeyboardPassthroughForm(KeyboardPassthroughSession connection, IKeyboardCaptureHook hook)
    {
        if (connection == null) throw new ArgumentNullException("connection");
        if (hook == null) throw new ArgumentNullException("hook");
        session = connection;
        captureHook = hook;
        Text = "键盘直通 · ESP32-S3";
        Icon = ApplicationIcon.Value;
        Font = new Font("Microsoft YaHei UI", 10);
        ImeMode = ImeMode.Disable;
        ClientSize = new Size(710, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var title = new Label { Text = "用你的键盘直接控制目标设备", AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold), Location = new Point(22, 20) };
        var help = new Label { Text = "按下、松开和长按即时转发，支持多个键同时按住。\r\n支持 6 个普通键，以及左右 Ctrl、Shift、Alt、Win 修饰键。",
            Location = new Point(24, 65), Size = new Size(662, 57) };
        state.Text = "正在连接并检查键盘直通固件……";
        state.SetBounds(24, 132, 662, 67);
        state.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        held.Text = "当前没有按住的按键。";
        held.SetBounds(24, 204, 662, 46);
        warning.SetBounds(24, 252, 662, 46);
        warning.ForeColor = Color.DarkOrange;
        blockComputerShortcuts.Text = "屏蔽电脑快捷键（默认开启，仅在本窗口直通时生效）";
        blockComputerShortcuts.Checked = true;
        blockComputerShortcuts.SetBounds(24, 306, 662, 30);
        blockComputerShortcuts.CheckedChanged += delegate {
            if (!ready || closing) return;
            bool resume = !paused;
            PauseCapture("设置已切换，所有按键已请求释放。点击“继续直通”恢复。");
            if (resume) ResumeCapture();
        };
        var hint = new Label { Text = "用鼠标点击“暂停直通”或其他窗口退出接管；返回后手动继续。\r\n暂停前先松开按键；恢复后如某键无反应，请松开后再按一次。\r\nCtrl+Alt+Del、Win+L 等系统保留快捷键不能保证屏蔽。",
            Location = new Point(24, 347), Size = new Size(662, 76), ForeColor = Color.DimGray };
        pause.Text = "暂停直通";
        pause.Enabled = false;
        pause.SetBounds(406, 446, 130, 34);
        pause.Click += delegate { if (paused) ResumeCapture(); else PauseCapture("已暂停，所有按键已请求释放。"); };
        var close = new Button { Text = "关闭直通", Location = new Point(551, 446), Size = new Size(135, 34) };
        close.Click += delegate { Close(); };
        Controls.AddRange(new Control[] { title, help, state, held, warning, blockComputerShortcuts, hint, pause, close });

        captureHook.Failed += CaptureFailed;

        session.Ready += delegate { Post(delegate {
            ready = true; pause.Enabled = true;
            if (allowInitialCapture && GetForegroundWindow() == Handle) ResumeCapture();
            else PauseCapture("已连接，点击“继续直通”开始控制。");
        }); };
        session.Error += delegate(object sender, KeyboardPassthroughErrorEventArgs e) { Post(delegate {
            captureHook.Stop();
            failed = true; ready = false; paused = true; keys.Clear();
            pause.Enabled = false; held.Text = "当前没有按住的按键。";
            state.ForeColor = Color.Firebrick;
            state.Text = e.Message;
            warning.Text = "直通已停止。检查固件和连接后，关闭此窗口再重新打开。";
        }); };
        session.Stopped += delegate { Post(delegate {
            captureHook.Stop();
            ready = false; paused = true; keys.Clear(); pause.Enabled = false;
            held.Text = "当前没有按住的按键。";
            if (!failed) { state.ForeColor = Color.DimGray; state.Text = "键盘直通已停止。"; }
        }); };
        Shown += delegate { Application.AddMessageFilter(this); session.Start(); };
        Deactivate += delegate { if (!closing) PauseCapture("窗口已失去焦点，直通已暂停。返回后点击“继续直通”。"); };
    }

    void Post(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)delegate { if (!closing && !IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    void ResumeCapture()
    {
        if (!ready || closing || GetForegroundWindow() != Handle) return;
        keys.Clear(); session.ReleaseAll(); paused = false;
        if (blockComputerShortcuts.Checked) {
            try { captureHook.Start(Handle, CaptureKey); }
            catch (Exception error) { CaptureFailed(error); return; }
        }
        state.ForeColor = Color.Teal;
        state.Text = blockComputerShortcuts.Checked ? "正在直通：电脑快捷键已屏蔽，按键发送到目标设备。" :
            "正在直通：未屏蔽电脑快捷键，其他程序可能响应按键。";
        pause.Text = "暂停直通";
        held.Text = "当前没有按住的按键。"; warning.Text = "";
    }

    void PauseCapture(string message)
    {
        allowInitialCapture = false;
        paused = true; captureHook.Stop(); keys.Clear(); session.ReleaseAll();
        held.Text = "当前没有按住的按键。"; warning.Text = "";
        pause.Text = "继续直通";
        if (ready && !failed) { state.ForeColor = Color.DarkGoldenrod; state.Text = message; }
    }

    void CaptureFailed(Exception error)
    {
        if (closing || IsDisposed) return;
        PauseCapture("未能屏蔽电脑快捷键，直通已暂停。");
        warning.Text = "请用鼠标点击“继续直通”重试，或取消屏蔽选项。" + error.Message;
    }

    void CaptureKey(int kind, IntPtr virtualKey, IntPtr flags)
    {
        if (!ready || paused || closing || !blockComputerShortcuts.Checked || GetForegroundWindow() != Handle) return;
        ForwardKey(kind, virtualKey, flags);
    }

    void ForwardKey(int kind, IntPtr virtualKey, IntPtr flags)
    {
        KeyboardReport report;
        string note;
        if (keys.HandleKeyMessage(kind, virtualKey, flags, out report, out note)) {
            ShowHeld(report);
            if (!session.Submit(report)) PauseCapture("直通已暂停，正在释放按键；请检查连接状态。");
        }
        if (!String.IsNullOrEmpty(note)) warning.Text = note;
        else if (kind == 0x101 || kind == 0x105) warning.Text = "";
    }

    void ShowHeld(KeyboardReport report)
    {
        int count = 0;
        foreach (byte key in report.Keys) if (key != 0) count++;
        string[] names = { "左 Ctrl", "左 Shift", "左 Alt", "左 Win", "右 Ctrl", "右 Shift", "右 Alt", "右 Win" };
        var modifiers = new List<string>();
        for (int bit = 0; bit < names.Length; bit++)
            if ((report.Modifiers & (1 << bit)) != 0) modifiers.Add(names[bit]);
        held.Text = report.IsEmpty ? "当前没有按住的按键。" :
            "按住的普通键：" + count + " / 6　修饰键：" + (modifiers.Count == 0 ? "无" : String.Join(" + ", modifiers.ToArray()));
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (!ready || paused || closing || GetForegroundWindow() != Handle) return false;
        int kind = message.Msg;
        if (kind == 0x100 || kind == 0x101 || kind == 0x104 || kind == 0x105) {
            // The native hook owns forwarding when enabled. Never forward a
            // second copy from a queued window message or an old held key.
            if (!blockComputerShortcuts.Checked) ForwardKey(kind, message.WParam, message.LParam);
            // Consume Tab, Escape and system-key messages as well, so they
            // cannot activate a local button while controlling the target.
            return true;
        }
        return kind == 0x102 || kind == 0x103 || kind == 0x106 || kind == 0x107;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true; paused = true; keys.Clear();
        captureHook.Stop();
        Application.RemoveMessageFilter(this);
        session.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            closing = true;
            captureHook.Failed -= CaptureFailed;
            captureHook.Dispose();
            Application.RemoveMessageFilter(this);
            session.Dispose();
        }
        base.Dispose(disposing);
    }
}
