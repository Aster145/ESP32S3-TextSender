using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class KeyboardPassthroughForm : Form, IMessageFilter
{
    readonly KeyboardPassthroughSession session;
    readonly KeyboardReportState keys = new KeyboardReportState();
    readonly Label state = new Label(), held = new Label(), warning = new Label();
    readonly Button pause = new Button();
    bool ready, paused = true, closing, failed, allowInitialCapture = true;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    public KeyboardPassthroughForm(string port) : this(new KeyboardPassthroughSession(port)) { }

    internal KeyboardPassthroughForm(KeyboardPassthroughSession connection)
    {
        session = connection;
        Text = "键盘直通 · ESP32-S3";
        Icon = ApplicationIcon.Value;
        Font = new Font("Microsoft YaHei UI", 10);
        ImeMode = ImeMode.Disable;
        ClientSize = new Size(710, 440);
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
        var hint = new Label { Text = "切换窗口会释放所有按键并暂停；返回后点击“继续直通”。\r\n系统保留快捷键（如 Alt+Tab、Ctrl+Alt+Del）仍可能由电脑处理。",
            Location = new Point(24, 308), Size = new Size(662, 50), ForeColor = Color.DimGray };
        pause.Text = "暂停直通";
        pause.Enabled = false;
        pause.SetBounds(406, 386, 130, 34);
        pause.Click += delegate { if (paused) ResumeCapture(); else PauseCapture("已暂停，所有按键已请求释放。"); };
        var close = new Button { Text = "关闭直通", Location = new Point(551, 386), Size = new Size(135, 34) };
        close.Click += delegate { Close(); };
        Controls.AddRange(new Control[] { title, help, state, held, warning, hint, pause, close });

        session.Ready += delegate { Post(delegate {
            ready = true; pause.Enabled = true;
            if (allowInitialCapture && GetForegroundWindow() == Handle) ResumeCapture();
            else PauseCapture("已连接，点击“继续直通”开始控制。");
        }); };
        session.Error += delegate(object sender, KeyboardPassthroughErrorEventArgs e) { Post(delegate {
            failed = true; ready = false; paused = true; keys.Clear();
            pause.Enabled = false; held.Text = "当前没有按住的按键。";
            state.ForeColor = Color.Firebrick;
            state.Text = e.Message;
            warning.Text = "直通已停止。检查固件和连接后，关闭此窗口再重新打开。";
        }); };
        session.Stopped += delegate { Post(delegate {
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
        state.ForeColor = Color.Teal;
        state.Text = "正在直通：现在的按键会发送到目标设备。";
        pause.Text = "暂停直通";
        held.Text = "当前没有按住的按键。"; warning.Text = "";
    }

    void PauseCapture(string message)
    {
        allowInitialCapture = false;
        keys.Clear(); session.ReleaseAll(); paused = true;
        held.Text = "当前没有按住的按键。"; warning.Text = "";
        pause.Text = "继续直通";
        if (ready && !failed) { state.ForeColor = Color.DarkGoldenrod; state.Text = message; }
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
            KeyboardReport report;
            string note;
            if (keys.HandleKeyMessage(kind, message.WParam, message.LParam, out report, out note)) {
                ShowHeld(report);
                if (!session.Submit(report)) PauseCapture("直通已暂停，正在释放按键；请检查连接状态。");
            }
            if (!String.IsNullOrEmpty(note)) warning.Text = note;
            else if (kind == 0x101 || kind == 0x105) warning.Text = "";
            // Consume Tab, Escape and system-key messages as well, so they
            // cannot activate a local button while controlling the target.
            return true;
        }
        return kind == 0x102 || kind == 0x103 || kind == 0x106 || kind == 0x107;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true; paused = true; keys.Clear();
        Application.RemoveMessageFilter(this);
        session.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            closing = true;
            Application.RemoveMessageFilter(this);
            session.Dispose();
        }
        base.Dispose(disposing);
    }
}
