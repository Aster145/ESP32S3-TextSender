using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

[assembly: System.Reflection.AssemblyTitle("ESP32-S3 TextSender")]
[assembly: System.Reflection.AssemblyVersion("1.8.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.8.0.0")]

public class PhrasePreset
{
    public string Name { get; set; }
    public string Text { get; set; }
    public bool AppendEnter { get; set; }
    public override string ToString() { return Name ?? ""; }
    public PhrasePreset Copy() { return new PhrasePreset { Name = Name, Text = Text, AppendEnter = AppendEnter }; }
}

public class ScriptShortcut
{
    public string Name { get; set; }
    public string ScriptPath { get; set; }
    public string WorkingDirectory { get; set; }
    public override string ToString() { return Name ?? ""; }
    public ScriptShortcut Copy() { return new ScriptShortcut { Name = Name, ScriptPath = ScriptPath, WorkingDirectory = WorkingDirectory }; }
}

public class SenderSettings
{
    public int StartDelayMs { get; set; }
    public int KeyGapMs { get; set; }
    public int LineGapMs { get; set; }
    public bool ClearIndent { get; set; }
    public bool KeepOnTop { get; set; }
    public bool PresetAutoEnter { get; set; }
    public string Ns2ProjectPath { get; set; }
    public string FtpScriptPath { get; set; }
    // Null means an older configuration without the new script list.
    public List<ScriptShortcut> ScriptShortcuts { get; set; }
    public bool ShouldSerializeFtpScriptPath() { return false; }
    public List<string> ProjectDirectories { get; set; }
    public List<PhrasePreset> Presets { get; set; }
    public SenderSettings() { KeyGapMs = 30; LineGapMs = 150; KeepOnTop = true; Ns2ProjectPath = ""; FtpScriptPath = ""; ProjectDirectories = new List<string>(); Presets = new List<PhrasePreset>(); }
    public static SenderSettings Defaults()
    {
        var settings = new SenderSettings();
        settings.ScriptShortcuts = new List<ScriptShortcut>();
        settings.Presets.Add(new PhrasePreset { Name = "问候", Text = "Hello ESP32-S3!" });
        settings.Presets.Add(new PhrasePreset { Name = "符号示例", Text = "REM AbC \"x#\";4+8" });
        return settings;
    }
}

class ScriptShortcutManager : Form
{
    ListBox list = new ListBox();
    TextBox name = new TextBox(), scriptPath = new TextBox(), workingDirectory = new TextBox();
    Button remove = new Button(), chooseScript = new Button(), chooseDirectory = new Button();
    bool loading;
    public List<ScriptShortcut> Result { get; private set; }

    public ScriptShortcutManager(List<ScriptShortcut> source)
    {
        Result = new List<ScriptShortcut>();
        foreach (ScriptShortcut item in source) Result.Add(item.Copy());
        Text = "管理脚本";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(820, 420);
        MinimumSize = SizeFromClientSize(new Size(760, 380));
        StartPosition = FormStartPosition.CenterParent;
        list.SetBounds(16, 16, 205, 340);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        list.SelectedIndexChanged += delegate { LoadSelected(); };
        var nameLabel = new Label { Text = "按钮名称", AutoSize = true, Location = new Point(240, 20) };
        name.SetBounds(330, 15, 472, 30);
        name.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var scriptLabel = new Label { Text = "脚本文件完整路径（.cmd / .bat / .ps1）", AutoSize = true, Location = new Point(240, 67) };
        scriptPath.SetBounds(240, 96, 452, 30);
        scriptPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        chooseScript.Text = "选择文件…"; chooseScript.SetBounds(702, 95, 100, 32);
        chooseScript.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var directoryLabel = new Label { Text = "工作目录（可选，留空使用脚本所在目录）", AutoSize = true, Location = new Point(240, 149) };
        workingDirectory.SetBounds(240, 178, 452, 30);
        workingDirectory.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        chooseDirectory.Text = "选择目录…"; chooseDirectory.SetBounds(702, 177, 100, 32);
        chooseDirectory.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var hint = new Label {
            Text = "保存后，点击主窗口中的脚本按钮启动。\r\n可直接粘贴路径；文件暂时不可用时仍可保留设置。\r\nPowerShell 脚本使用当前执行策略，不附加其他参数。",
            Location = new Point(240, 234), Size = new Size(562, 90),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        var add = new Button { Text = "新增", Location = new Point(16, 370), Size = new Size(90, 34) };
        remove.Text = "删除"; remove.SetBounds(116, 370, 90, 34);
        var ok = new Button { Text = "保存", Location = new Point(602, 370), Size = new Size(95, 34) };
        var cancel = new Button { Text = "取消", Location = new Point(707, 370), Size = new Size(95, 34), DialogResult = DialogResult.Cancel };
        add.Anchor = remove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        ok.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        name.TextChanged += delegate { UpdateDraft(); };
        scriptPath.TextChanged += delegate { UpdateDraft(); };
        workingDirectory.TextChanged += delegate { UpdateDraft(); };
        add.Click += delegate {
            var item = new ScriptShortcut { Name = "新脚本", ScriptPath = "", WorkingDirectory = "" };
            Result.Add(item); list.Items.Add(item); list.SelectedIndex = Result.Count - 1;
            name.Focus(); name.SelectAll();
        };
        remove.Click += delegate {
            int index = list.SelectedIndex;
            if (index < 0) return;
            loading = true;
            Result.RemoveAt(index); list.Items.RemoveAt(index);
            if (Result.Count > 0) list.SelectedIndex = Math.Min(index, Result.Count - 1);
            loading = false; LoadSelected();
        };
        chooseScript.Click += delegate {
            try {
                using (var dialog = new OpenFileDialog()) {
                    dialog.Title = "选择启动脚本";
                    dialog.Filter = "脚本文件 (*.cmd;*.bat;*.ps1)|*.cmd;*.bat;*.ps1";
                    dialog.CheckFileExists = true;
                    string current = "";
                    try { current = TextSender.NormalizeScriptLocation(scriptPath.Text, true); } catch { }
                    if (File.Exists(current)) dialog.FileName = current;
                    if (dialog.ShowDialog(this) == DialogResult.OK) scriptPath.Text = dialog.FileName;
                }
            } catch (Exception ex) { ShowError(ex); }
        };
        chooseDirectory.Click += delegate {
            try {
                string initial = "";
                try { initial = TextSender.NormalizeScriptLocation(workingDirectory.Text, true); } catch { }
                if (String.IsNullOrEmpty(initial)) {
                    string script = "";
                    try { script = TextSender.NormalizeScriptLocation(scriptPath.Text, true); } catch { }
                    if (!String.IsNullOrEmpty(script)) initial = Path.GetDirectoryName(script);
                }
                string selected = ExplorerFolderPicker.Pick(this, initial);
                if (selected != null) workingDirectory.Text = selected;
            } catch (Exception ex) { ShowError(ex); }
        };
        ok.Click += delegate {
            try {
                foreach (ScriptShortcut item in Result) TextSender.ValidateScriptShortcut(item);
                DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { ShowError(ex); }
        };
        CancelButton = cancel;
        Controls.AddRange(new Control[] { list, nameLabel, name, scriptLabel, scriptPath, chooseScript,
            directoryLabel, workingDirectory, chooseDirectory, hint, add, remove, ok, cancel });
        foreach (ScriptShortcut item in Result) list.Items.Add(item);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        else LoadSelected();
    }

    void ShowError(Exception error)
    {
        MessageBox.Show(this, error.Message, "脚本设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void LoadSelected()
    {
        if (loading) return;
        loading = true;
        int index = list.SelectedIndex;
        bool selected = index >= 0;
        name.Enabled = scriptPath.Enabled = workingDirectory.Enabled = remove.Enabled = chooseScript.Enabled = chooseDirectory.Enabled = selected;
        name.Text = selected ? Result[index].Name : "";
        scriptPath.Text = selected ? Result[index].ScriptPath : "";
        workingDirectory.Text = selected ? Result[index].WorkingDirectory : "";
        loading = false;
    }

    void UpdateDraft()
    {
        int index = list.SelectedIndex;
        if (loading || index < 0) return;
        ScriptShortcut item = Result[index];
        bool changedName = item.Name != name.Text;
        item.Name = name.Text; item.ScriptPath = scriptPath.Text; item.WorkingDirectory = workingDirectory.Text;
        if (changedName) {
            loading = true; list.Items[index] = item; list.SelectedIndex = index; loading = false;
        }
    }
}

class PresetManager : Form
{
    ListBox list = new ListBox();
    TextBox name = new TextBox(), text = new TextBox();
    CheckBox appendEnter = new CheckBox();
    Button remove = new Button();
    bool loading;
    public List<PhrasePreset> Result { get; private set; }

    public PresetManager(List<PhrasePreset> source)
    {
        Result = new List<PhrasePreset>();
        foreach (PhrasePreset item in source) Result.Add(item.Copy());
        Text = "管理常用短语";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(720, 450);
        StartPosition = FormStartPosition.CenterParent;
        list.SetBounds(16, 16, 205, 398);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        list.SelectedIndexChanged += delegate { LoadSelected(); };
        var nameLabel = new Label { Text = "按钮名称", AutoSize = true, Location = new Point(240, 19) };
        name.SetBounds(330, 14, 410, 30);
        name.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var textLabel = new Label { Text = "发送内容（保留空格和换行）", AutoSize = true, Location = new Point(240, 58) };
        text.SetBounds(240, 89, 500, 285);
        text.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        text.Multiline = true; text.AcceptsReturn = true; text.AcceptsTab = true;
        text.ScrollBars = ScrollBars.Both; text.WordWrap = false;
        text.Font = new Font("Consolas", 11);
        appendEnter.Text = "末尾再发送一次回车";
        appendEnter.SetBounds(240, 382, 350, 30);
        appendEnter.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        name.TextChanged += delegate { UpdateDraft(); };
        text.TextChanged += delegate { UpdateDraft(); };
        appendEnter.CheckedChanged += delegate { UpdateDraft(); };
        var add = new Button { Text = "新增", Location = new Point(16, 430), Size = new Size(90, 34) };
        remove.Text = "删除"; remove.SetBounds(116, 430, 90, 34);
        var ok = new Button { Text = "保存", Location = new Point(540, 430), Size = new Size(95, 34) };
        var cancel = new Button { Text = "取消", Location = new Point(645, 430), Size = new Size(95, 34), DialogResult = DialogResult.Cancel };
        add.Anchor = remove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        ok.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        add.Click += delegate {
            var item = new PhrasePreset { Name = "新短语", Text = "", AppendEnter = false };
            Result.Add(item); list.Items.Add(item); list.SelectedIndex = Result.Count - 1;
            name.Focus(); name.SelectAll();
        };
        remove.Click += delegate {
            int index = list.SelectedIndex;
            if (index < 0) return;
            loading = true;
            Result.RemoveAt(index); list.Items.RemoveAt(index);
            if (Result.Count > 0) list.SelectedIndex = Math.Min(index, Result.Count - 1);
            loading = false; LoadSelected();
        };
        ok.Click += delegate {
            try {
                foreach (PhrasePreset item in Result) TextSender.ValidatePreset(item);
                DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "短语尚未保存", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        CancelButton = cancel;
        Controls.AddRange(new Control[] { list, nameLabel, name, textLabel, text, appendEnter, add, remove, ok, cancel });
        foreach (PhrasePreset item in Result) list.Items.Add(item);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        else LoadSelected();
    }

    void LoadSelected()
    {
        if (loading) return;
        loading = true;
        int index = list.SelectedIndex;
        bool selected = index >= 0;
        name.Enabled = text.Enabled = appendEnter.Enabled = remove.Enabled = selected;
        name.Text = selected ? Result[index].Name : "";
        text.Text = selected ? Result[index].Text : "";
        appendEnter.Checked = selected && Result[index].AppendEnter;
        loading = false;
    }

    void UpdateDraft()
    {
        int index = list.SelectedIndex;
        if (loading || index < 0) return;
        PhrasePreset item = Result[index];
        bool changedName = item.Name != name.Text;
        item.Name = name.Text; item.Text = text.Text; item.AppendEnter = appendEnter.Checked;
        if (changedName) {
            loading = true;
            list.Items[index] = item;
            list.SelectedIndex = index;
            loading = false;
        }
    }
}

class TextSender : Form
{
    ComboBox ports = new ComboBox();
    Button refresh = new Button(), send = new Button(), stop = new Button(), caps = new Button(), manage = new Button();
    Button chooseProject = new Button(), getStart = new Button(), sendStart = new Button();
    ComboBox projects = new ComboBox();
    Button removeProject = new Button();
    Button manageScripts = new Button();
    TextBox projectPath = new TextBox(), startPreview = new TextBox();
    Label startStatus = new Label();
    CheckBox keepOnTop = new CheckBox();
    TextBox editor = new TextBox();
    Label status = new Label();
    NumericUpDown startDelay = new NumericUpDown(), keyGap = new NumericUpDown(), lineGap = new NumericUpDown();
    CheckBox noIndent = new CheckBox();
    CheckBox presetAutoEnter = new CheckBox();
    FlowLayoutPanel phraseButtons = new FlowLayoutPanel();
    FlowLayoutPanel scriptButtons = new FlowLayoutPanel();
    ToolTip tips = new ToolTip();
    List<PhrasePreset> presets = new List<PhrasePreset>();
    List<string> projectDirectories = new List<string>();
    List<ScriptShortcut> scriptShortcuts = new List<ScriptShortcut>();
    SerialPort connection, active;
    bool busy, closing, loadingSettings, projectPathChanged, updatingProjects;
    string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml");
    string[] arguments;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--test-offline") { OfflineTest(args[1]); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TextSender(args));
    }

    TextSender(string[] args)
    {
        arguments = args;
        Text = "ESP32-S3 键盘输入 v1.8.0";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(940, 800);
        MinimumSize = SizeFromClientSize(new Size(940, 800));
        StartPosition = FormStartPosition.CenterScreen;
        var label = new Label { Text = "设备串口", Location = new Point(16, 19), AutoSize = true };
        ports.SetBounds(95, 14, 150, 30); ports.DropDownStyle = ComboBoxStyle.DropDownList;
        ports.SelectedIndexChanged += delegate { if (!busy) CloseConnection(); };
        refresh.Text = "刷新"; refresh.SetBounds(258, 13, 75, 32);
        refresh.Click += delegate { RefreshPorts(); };
        caps.Text = "切换目标 Caps Lock"; caps.SetBounds(348, 13, 230, 32);
        caps.Click += async delegate {
            if (busy || ports.SelectedItem == null) return;
            SetBusy(true);
            try {
                string port = ports.Text;
                await Task.Run(() => Transmit(port, null, 0, delegate {}, false));
                if (!closing) status.Text = "已发送 Caps Lock 切换键，请观察目标设备的大小写状态。";
            } catch (Exception ex) { if (!closing) status.Text = "切换失败：" + ex.Message; }
            finally { if (!closing) SetBusy(false); }
        };
        keepOnTop.Text = "窗口置顶";
        keepOnTop.SetBounds(640, 16, 180, 28);
        var hint = new Label {
            Text = "最多 8192 个 ASCII 字符。等待设为 0 时立即发送；首次连接仍需要约 1.2 秒初始化。",
            Location = new Point(16, 58), Size = new Size(908, 30), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        var delayLabel = new Label { Text = "发送前等待（秒）", Location = new Point(16, 99), AutoSize = true };
        startDelay.SetBounds(154, 94, 78, 30);
        startDelay.Minimum = 0; startDelay.Maximum = 10; startDelay.DecimalPlaces = 1; startDelay.Increment = 0.1M;
        var keyLabel = new Label { Text = "逐字间隔（毫秒）", Location = new Point(255, 99), AutoSize = true };
        keyGap.SetBounds(396, 94, 78, 30); keyGap.Minimum = 1; keyGap.Maximum = 1000;
        var lineLabel = new Label { Text = "换行等待（毫秒）", Location = new Point(500, 99), AutoSize = true };
        lineGap.SetBounds(641, 94, 78, 30); lineGap.Minimum = 0; lineGap.Maximum = 3000;
        noIndent.Text = "清除回车后的自动缩进（仅用于 SmileBASIC 的 EDIT 编辑界面，保留原文缩进）";
        noIndent.SetBounds(16, 133, 908, 30);
        noIndent.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var favorites = new GroupBox { Text = "常用短语 · 点击按钮直接发送", Location = new Point(16, 174), Size = new Size(908, 126) };
        favorites.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        presetAutoEnter.Text = "发送后自动回车";
        presetAutoEnter.SetBounds(16, 20, 240, 28);
        tips.SetToolTip(presetAutoEnter, "勾选后所有常用短语自动回车；与单条短语的回车选项同时开启时只加一次。");
        manage.Text = "管理常用短语"; manage.SetBounds(748, 17, 145, 32);
        manage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        manage.Click += delegate { ManagePresets(); };
        phraseButtons.SetBounds(12, 50, 881, 64);
        phraseButtons.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        phraseButtons.AutoScroll = true; phraseButtons.WrapContents = true;
        favorites.Controls.AddRange(new Control[] { presetAutoEnter, manage, phraseButtons });
        var startGroup = new GroupBox { Text = "ns2jb / HBLauncher · V=START 连接口令", Location = new Point(16, 310), Size = new Size(908, 194) };
        startGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var switchLabel = new Label { Text = "切换项目", Location = new Point(12, 29), AutoSize = true };
        projects.SetBounds(90, 23, 540, 28);
        projects.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        projects.DropDownStyle = ComboBoxStyle.DropDownList;
        projects.DropDownWidth = 880; projects.MaxDropDownItems = 12;
        projects.FormattingEnabled = true;
        projects.Format += delegate(object sender, ListControlConvertEventArgs e) {
            string directory = e.ListItem as string;
            if (directory != null) e.Value = ProjectName(directory) + "  —  " + directory;
        };
        projects.SelectedIndexChanged += delegate {
            if (loadingSettings || updatingProjects || busy || projects.SelectedIndex < 0) return;
            projectPath.Text = (string)projects.SelectedItem;
            projectPathChanged = false;
            RebuildProjects(); SaveCurrentSettings(); RefreshStart();
        };
        chooseProject.Text = "添加文件夹…"; chooseProject.SetBounds(640, 21, 135, 32);
        chooseProject.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        chooseProject.Click += delegate { ChooseProject(); };
        removeProject.Text = "移除项目"; removeProject.SetBounds(785, 21, 108, 32);
        removeProject.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        removeProject.Click += delegate { RemoveProject(); };
        tips.SetToolTip(removeProject, "仅从列表移除，不会删除电脑里的文件夹。");
        var projectLabel = new Label { Text = "项目目录", Location = new Point(12, 68), AutoSize = true };
        projectPath.SetBounds(90, 62, 803, 28);
        projectPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        tips.SetToolTip(projectPath, "可以直接粘贴项目目录或 runs 目录，按回车加入列表并获取最新口令。");
        startPreview.SetBounds(12, 100, 881, 28); startPreview.ReadOnly = true;
        startPreview.Font = new Font("Consolas", 10);
        startPreview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        getStart.Text = "获取最新"; getStart.SetBounds(12, 141, 108, 34);
        getStart.Click += delegate { RefreshStart(); };
        sendStart.Text = "获取并发送 V=START"; sendStart.SetBounds(130, 141, 200, 34);
        sendStart.Click += async delegate { await SendLatestStart(); };
        startStatus.SetBounds(342, 137, 550, 48);
        startStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        startGroup.Controls.AddRange(new Control[] { switchLabel, projects, removeProject, projectLabel, projectPath, chooseProject, startPreview, getStart, sendStart, startStatus });
        var scriptsGroup = new GroupBox { Text = "启动脚本", Location = new Point(16, 518), Size = new Size(908, 90) };
        scriptsGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scriptButtons.SetBounds(12, 22, 728, 60);
        scriptButtons.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scriptButtons.AutoScroll = true; scriptButtons.WrapContents = false;
        manageScripts.Text = "管理脚本…"; manageScripts.SetBounds(748, 24, 145, 34);
        manageScripts.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        manageScripts.Click += delegate { ManageScripts(); };
        scriptsGroup.Controls.AddRange(new Control[] { scriptButtons, manageScripts });
        editor.Multiline = true; editor.AcceptsReturn = true; editor.AcceptsTab = true;
        editor.ScrollBars = ScrollBars.Both; editor.WordWrap = false;
        editor.Font = new Font("Consolas", 12);
        editor.SetBounds(16, 620, 908, 72);
        editor.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        send.SetBounds(16, 704, 230, 36); send.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        send.Click += async delegate { await SendText(editor.Text, "编辑框"); };
        stop.Text = "停止"; stop.SetBounds(260, 704, 80, 36);
        stop.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; stop.Enabled = false;
        stop.Click += delegate {
            try { if (active != null && active.IsOpen) active.Write(new byte[] { 24 }, 0, 1); }
            catch (Exception ex) { status.Text = "停止失败：" + ex.Message; }
        };
        status.Text = "双线：选择 UART 口的串口；单线：选择原生 USB 口的串口。";
        status.SetBounds(16, 751, 908, 42);
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.AddRange(new Control[] { label, ports, refresh, caps, hint, delayLabel, startDelay,
            keyLabel, keyGap, lineLabel, lineGap, noIndent, favorites, startGroup, scriptsGroup, keepOnTop, editor, send, stop, status });
        LoadSettings();
        projectPath.TextChanged += delegate {
            if (loadingSettings) return;
            projectPathChanged = true;
            updatingProjects = true; projects.SelectedIndex = -1; updatingProjects = false;
            removeProject.Enabled = false;
            startPreview.Clear(); tips.SetToolTip(startPreview, "");
            startStatus.Text = "按回车或点击“获取最新”应用路径。";
        };
        projectPath.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            RefreshStart();
        };
        projectPath.Leave += delegate { if (projectPathChanged && !closing) RefreshStart(); };
        startDelay.ValueChanged += delegate { UpdateSendLabel(); SaveCurrentSettings(); };
        keyGap.ValueChanged += delegate { SaveCurrentSettings(); };
        lineGap.ValueChanged += delegate { SaveCurrentSettings(); };
        noIndent.CheckedChanged += delegate { SaveCurrentSettings(); };
        presetAutoEnter.CheckedChanged += delegate { SaveCurrentSettings(); };
        keepOnTop.CheckedChanged += delegate { TopMost = keepOnTop.Checked; SaveCurrentSettings(); };
        RefreshPorts();
        FormClosing += delegate {
            ApplyProjectPath();
            closing = true;
            try { if (busy && active != null && active.IsOpen) active.Write(new byte[] { 24 }, 0, 1); } catch { }
            CloseConnection(); tips.Dispose();
        };
        Shown += async delegate {
            RefreshStart();
            if (arguments.Length == 2 && arguments[0] == "--test-ui") {
                BeginInvoke((Action)delegate {
                    try {
                        using (var bitmap = new Bitmap(Width, Height)) {
                            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                            bitmap.Save(arguments[1], ImageFormat.Png);
                        }
                    } finally { Close(); }
                });
            } else if (arguments.Length == 3 && arguments[0] == "--self-test") {
                await SelfTest(arguments[1], arguments[2]);
            }
        };
    }

    void UpdateSendLabel()
    {
        send.Text = startDelay.Value == 0 ? "立即发送编辑框" : "发送编辑框（" + startDelay.Value.ToString("0.#") + " 秒后）";
    }

    void RefreshPorts()
    {
        CloseConnection();
        string old = ports.Text;
        ports.Items.Clear();
        string[] names = SerialPort.GetPortNames(); Array.Sort(names); ports.Items.AddRange(names);
        if (ports.Items.Contains(old)) ports.SelectedItem = old;
        else if (ports.Items.Count > 0) ports.SelectedIndex = 0;
    }

    void CloseConnection()
    {
        SerialPort old = connection;
        connection = null;
        if (old != null) { try { old.Dispose(); } catch { } }
    }

    public static string NormalizeScriptLocation(string value, bool allowEmpty)
    {
        string path = (value ?? "").Trim();
        if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            path = path.Substring(1, path.Length - 2);
        if (String.IsNullOrWhiteSpace(path)) {
            if (allowEmpty) return "";
            throw new ArgumentException("请填写脚本文件的完整路径。");
        }
        bool drivePath = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        if (!drivePath && !path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("请使用完整路径，不能填写相对路径或命令参数：" + path);
        return Path.GetFullPath(path);
    }

    public static void ValidateScriptShortcut(ScriptShortcut item)
    {
        if (item == null || String.IsNullOrWhiteSpace(item.Name))
            throw new ArgumentException("每个脚本都需要一个按钮名称。");
        string path = NormalizeScriptLocation(item.ScriptPath, false);
        string extension = Path.GetExtension(path);
        if (!String.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("脚本「" + item.Name + "」仅支持 .cmd、.bat 或 .ps1 文件，不能附加参数。");
        NormalizeScriptLocation(item.WorkingDirectory, true);
    }

    static ProcessStartInfo CreateScriptStartInfo(ScriptShortcut item)
    {
        ValidateScriptShortcut(item);
        string script = NormalizeScriptLocation(item.ScriptPath, false);
        string directory = NormalizeScriptLocation(item.WorkingDirectory, true);
        if (!File.Exists(script)) throw new FileNotFoundException("找不到启动脚本：" + script, script);
        if (String.IsNullOrEmpty(directory)) directory = Path.GetDirectoryName(script);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("工作目录不存在：" + directory);
        var startInfo = new ProcessStartInfo {
            FileName = script, WorkingDirectory = directory, UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (String.Equals(Path.GetExtension(script), ".ps1", StringComparison.OrdinalIgnoreCase)) {
            startInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            startInfo.Arguments = "-NoProfile -File \"" + script + "\"";
        }
        return startInfo;
    }

    void LaunchScript(ScriptShortcut item)
    {
        if (busy || closing) return;
        try {
            using (Process process = Process.Start(CreateScriptStartInfo(item))) { }
            status.Text = "已启动「" + item.Name + "」，请在打开的窗口查看运行结果。";
        } catch (Exception ex) { status.Text = "脚本启动失败：" + ex.Message; }
    }

    void RebuildScriptButtons()
    {
        while (scriptButtons.Controls.Count > 0) scriptButtons.Controls[0].Dispose();
        foreach (ScriptShortcut entry in scriptShortcuts) {
            ScriptShortcut item = entry;
            var button = new Button { Text = item.Name, UseMnemonic = false, AutoSize = true,
                Height = 34, MinimumSize = new Size(110, 34), Margin = new Padding(4) };
            tips.SetToolTip(button, item.ScriptPath + "\r\n工作目录：" +
                (String.IsNullOrWhiteSpace(item.WorkingDirectory) ? "脚本所在目录" : item.WorkingDirectory));
            button.Click += delegate { LaunchScript(item); };
            scriptButtons.Controls.Add(button);
        }
        if (scriptShortcuts.Count == 0)
            scriptButtons.Controls.Add(new Label { Text = "暂无脚本，点击“管理脚本…”添加。", AutoSize = true, Margin = new Padding(6) });
    }

    void ApplyScriptShortcuts(List<ScriptShortcut> shortcuts)
    {
        if (shortcuts == null) throw new ArgumentNullException("shortcuts");
        var updated = new List<ScriptShortcut>();
        foreach (ScriptShortcut item in shortcuts) {
            ValidateScriptShortcut(item);
            updated.Add(new ScriptShortcut { Name = item.Name.Trim(), ScriptPath = NormalizeScriptLocation(item.ScriptPath, false),
                WorkingDirectory = NormalizeScriptLocation(item.WorkingDirectory, true) });
        }
        scriptShortcuts = updated;
        RebuildScriptButtons();
        if (SaveCurrentSettings()) status.Text = "脚本快捷项已保存，点击对应按钮启动。";
    }

    void ManageScripts()
    {
        if (busy || closing) return;
        using (var dialog = new ScriptShortcutManager(scriptShortcuts)) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { ApplyScriptShortcuts(dialog.Result); }
            catch (Exception ex) { status.Text = "脚本设置未保存：" + ex.Message; }
        }
    }

    public static byte[] Encode(string value)
    {
        value = (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        if (value.Length == 0 || value.Length > 8192) throw new Exception("请输入 1～8192 个字符（含换行）。");
        foreach (char c in value)
            if (!((c >= 32 && c <= 126) || c == '\n' || c == '\t'))
                throw new Exception("仅支持英文、数字、常见半角符号、换行和制表符，暂不支持中文内容。");
        return Encoding.ASCII.GetBytes(value);
    }

    public static string PresetText(PhrasePreset item, bool autoEnter = false)
    {
        string text = item.Text ?? "";
        bool append = item.AppendEnter || (autoEnter && !text.EndsWith("\n") && !text.EndsWith("\r"));
        return text + (append ? "\n" : "");
    }

    public static void ValidatePreset(PhrasePreset item)
    {
        if (item == null || String.IsNullOrWhiteSpace(item.Name)) throw new Exception("每个短语都需要一个按钮名称。");
        try { Encode(PresetText(item)); }
        catch (Exception ex) { throw new Exception("短语「" + item.Name + "」：" + ex.Message); }
    }

    async Task SendText(string text, string source, bool? clearIndentOverride = null)
    {
        if (busy || closing) return;
        try {
            byte[] data = Encode(text);
            if (ports.SelectedItem == null) throw new Exception("未找到串口，请连接开发板后点击刷新。");
            SetBusy(true);
            status.Text = "正在连接设备……";
            string port = ports.Text;
            int delayMs = (int)(startDelay.Value * 1000), gap = (int)keyGap.Value, newlineWait = (int)lineGap.Value;
            bool clearIndent = clearIndentOverride ?? noIndent.Checked;
            string delayText = startDelay.Value.ToString("0.#");
            string result = await Task.Run(() => Transmit(port, data, delayMs, delegate {
                PostToUi(delegate {
                    stop.Enabled = true;
                    status.Text = delayMs == 0 ? "正在输入「" + source + "」……" :
                        "「" + source + "」已排队，" + delayText + " 秒后开始输入，请切换到目标窗口。";
                });
            }, false, gap, newlineWait, clearIndent));
            if (!closing) status.Text = result == "DONE" ? "设备已完成发送，请核对目标文字。" : "已停止。";
        } catch (Exception ex) {
            if (!closing) status.Text = "发送失败：" + ex.Message;
        } finally { if (!closing) SetBusy(false); }
    }

    void PostToUi(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)delegate { if (!closing && !IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    void SetBusy(bool value)
    {
        busy = value;
        send.Enabled = refresh.Enabled = ports.Enabled = caps.Enabled = manage.Enabled = phraseButtons.Enabled = !value;
        startDelay.Enabled = keyGap.Enabled = lineGap.Enabled = noIndent.Enabled = !value;
        presetAutoEnter.Enabled = !value;
        manageScripts.Enabled = scriptButtons.Enabled = !value;
        chooseProject.Enabled = getStart.Enabled = sendStart.Enabled = !value;
        projects.Enabled = !value;
        removeProject.Enabled = !value && projects.SelectedIndex >= 0;
        projectPath.ReadOnly = value;
        editor.ReadOnly = value;
        if (!value) stop.Enabled = false;
    }

    void RebuildPresetButtons()
    {
        while (phraseButtons.Controls.Count > 0) phraseButtons.Controls[0].Dispose();
        foreach (PhrasePreset entry in presets) {
            PhrasePreset item = entry;
            var button = new Button { Text = item.Name, UseMnemonic = false, AutoSize = true, Height = 34, MinimumSize = new Size(110, 34), Margin = new Padding(4) };
            tips.SetToolTip(button, item.Text + (item.AppendEnter ? "\r\n（末尾再发送一次回车）" : ""));
            button.Click += async delegate { await SendText(PresetText(item, presetAutoEnter.Checked), item.Name); };
            phraseButtons.Controls.Add(button);
        }
        if (presets.Count == 0) phraseButtons.Controls.Add(new Label { Text = "暂无常用短语，点击管理添加。", AutoSize = true, Margin = new Padding(6) });
    }

    void ManagePresets()
    {
        if (busy) return;
        using (var dialog = new PresetManager(presets)) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            presets = dialog.Result;
            RebuildPresetButtons();
            if (SaveCurrentSettings()) status.Text = "常用短语已保存。点击短语按钮即可发送，编辑框内容不会被替换。";
        }
    }

    void ChooseProject()
    {
        if (busy) return;
        ApplyProjectPath();
        try {
            string selected = ExplorerFolderPicker.Pick(this, projectPath.Text);
            if (selected == null) return;
            projectPath.Text = selected;
            RefreshStart();
        } catch (Exception ex) {
            startStatus.Text = "无法打开文件夹窗口，可直接在项目目录中粘贴路径。";
            status.Text = "浏览文件夹失败：" + ex.Message;
        }
    }

    void ApplyProjectPath()
    {
        if (!projectPathChanged) return;
        string path = NormalizeProjectDirectory(projectPath.Text);
        projectPath.Text = path;
        projectPathChanged = false;
        if (Directory.Exists(path)) RememberProject(path);
        RebuildProjects();
        SaveCurrentSettings();
    }

    static string NormalizeProjectDirectory(string value)
    {
        string path = (value ?? "").Trim();
        if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            path = path.Substring(1, path.Length - 2);
        if (path.Length == 0) return path;
        try {
            string full = Path.GetFullPath(path);
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd('\\', '/') : full;
        } catch (ArgumentException) { return path; }
        catch (NotSupportedException) { return path; }
        catch (IOException) { return path; }
    }

    static string ProjectKey(string path)
    {
        string normalized = NormalizeProjectDirectory(path);
        // A project's root and its runs folder refer to the same token source.
        if (normalized.EndsWith("\\runs", StringComparison.OrdinalIgnoreCase))
            return normalized.Substring(0, normalized.Length - 5);
        return normalized;
    }

    static string ProjectName(string path)
    {
        string key = ProjectKey(path);
        int separator = Math.Max(key.LastIndexOf('\\'), key.LastIndexOf('/'));
        string name = key.Substring(separator + 1);
        return name.Length == 0 ? path : name;
    }

    int FindProject(string path)
    {
        string key = ProjectKey(path);
        for (int i = 0; i < projectDirectories.Count; i++)
            if (String.Equals(ProjectKey(projectDirectories[i]), key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    void RememberProject(string path)
    {
        path = NormalizeProjectDirectory(path);
        if (!String.IsNullOrWhiteSpace(path) && FindProject(path) < 0) projectDirectories.Add(path);
    }

    void RebuildProjects()
    {
        updatingProjects = true;
        projects.BeginUpdate();
        try {
            projects.Items.Clear();
            foreach (string path in projectDirectories) projects.Items.Add(path);
            projects.SelectedIndex = FindProject(projectPath.Text);
        } finally { projects.EndUpdate(); updatingProjects = false; }
        removeProject.Enabled = !busy && projects.SelectedIndex >= 0;
        tips.SetToolTip(projects, projectPath.Text);
    }

    void RemoveProject()
    {
        if (busy || projects.SelectedIndex < 0) return;
        int index = projects.SelectedIndex;
        projectDirectories.RemoveAt(index);
        projectPath.Text = projectDirectories.Count == 0 ? "" : projectDirectories[Math.Min(index, projectDirectories.Count - 1)];
        projectPathChanged = false;
        RebuildProjects(); SaveCurrentSettings(); RefreshStart();
    }

    void ShowStart(StartTokenInfo info)
    {
        startPreview.Text = info.Command;
        tips.SetToolTip(startPreview, info.FilePath);
        startStatus.Text = Path.GetFileName(Path.GetDirectoryName(info.FilePath)) + "  " + info.LastWriteUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + "\r\n在 Direct 模式点击发送，末尾自动回车。";
    }

    void RefreshStart()
    {
        if (busy) return;
        ApplyProjectPath();
        try {
            var info = StartTokenReader.ReadLatest(projectPath.Text);
            ShowStart(info);
            try { StartTokenReader.EnsureReady(info); }
            catch (Exception ex) { startStatus.Text = ex.Message; }
        } catch (Exception ex) {
            startPreview.Clear();
            startStatus.Text = ex.Message;
        }
    }

    async Task SendLatestStart()
    {
        if (busy || closing) return;
        ApplyProjectPath();
        try {
            // Read again at click time; never send an earlier preview's token.
            var info = StartTokenReader.ReadLatest(projectPath.Text);
            ShowStart(info);
            StartTokenReader.EnsureReady(info);
            // This is a Direct-mode command; editor autoindent cleanup must not follow Enter.
            await SendText(info.Command + "\n", "V=START", false);
        } catch (Exception ex) {
            startPreview.Clear(); startStatus.Text = ex.Message;
            status.Text = "未发送 V=START：" + ex.Message;
        }
    }

    static void ValidateSettings(SenderSettings settings)
    {
        if (settings == null || settings.StartDelayMs < 0 || settings.StartDelayMs > 10000 || settings.StartDelayMs % 100 != 0 ||
            settings.KeyGapMs < 1 || settings.KeyGapMs > 1000 || settings.LineGapMs < 0 || settings.LineGapMs > 3000 || settings.Presets == null)
            throw new Exception("设置文件中的等待时间、逐字间隔或短语列表无效。");
        foreach (PhrasePreset item in settings.Presets) ValidatePreset(item);
        if (settings.ScriptShortcuts == null) throw new Exception("脚本快捷项列表无效。");
        foreach (ScriptShortcut item in settings.ScriptShortcuts) ValidateScriptShortcut(item);
    }

    static void MigrateScriptShortcuts(SenderSettings settings)
    {
        if (settings.ScriptShortcuts == null) {
            settings.ScriptShortcuts = new List<ScriptShortcut>();
            if (!String.IsNullOrWhiteSpace(settings.FtpScriptPath))
                settings.ScriptShortcuts.Add(new ScriptShortcut { Name = "FTP",
                    ScriptPath = NormalizeScriptLocation(settings.FtpScriptPath, false), WorkingDirectory = "" });
        }
        settings.FtpScriptPath = "";
    }

    static SenderSettings ReadSettingsFile(string path)
    {
        using (var stream = File.OpenRead(path)) {
            // XmlSerializer may allocate a List even when its XML element is absent.
            // Check the element itself so an explicit empty list never revives legacy FTP.
            bool hasScriptList = false;
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { CloseInput = false })) {
                while (reader.Read()) {
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "ScriptShortcuts")
                        hasScriptList = true;
                }
            }
            stream.Position = 0;
            var settings = (SenderSettings)new XmlSerializer(typeof(SenderSettings)).Deserialize(stream);
            if (!hasScriptList) settings.ScriptShortcuts = null;
            else if (settings.ScriptShortcuts == null) settings.ScriptShortcuts = new List<ScriptShortcut>();
            MigrateScriptShortcuts(settings);
            ValidateSettings(settings); return settings;
        }
    }

    static void WriteSettingsFile(string path, SenderSettings settings)
    {
        if (settings != null) MigrateScriptShortcuts(settings);
        ValidateSettings(settings);
        string temporary = path + ".tmp";
        try {
            var options = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineHandling = NewLineHandling.Entitize };
            using (XmlWriter writer = XmlWriter.Create(temporary, options)) new XmlSerializer(typeof(SenderSettings)).Serialize(writer, settings);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    void LoadSettings()
    {
        loadingSettings = true;
        SenderSettings settings = SenderSettings.Defaults();
        try { if (File.Exists(settingsPath)) settings = ReadSettingsFile(settingsPath); }
        catch (Exception ex) { status.Text = "设置读取失败，已使用默认值：" + ex.Message; }
        startDelay.Value = settings.StartDelayMs / 1000M;
        keyGap.Value = settings.KeyGapMs; lineGap.Value = settings.LineGapMs; noIndent.Checked = settings.ClearIndent;
        keepOnTop.Checked = settings.KeepOnTop; TopMost = settings.KeepOnTop;
        presetAutoEnter.Checked = settings.PresetAutoEnter;
        scriptShortcuts = new List<ScriptShortcut>();
        foreach (ScriptShortcut item in settings.ScriptShortcuts) scriptShortcuts.Add(item.Copy());
        RebuildScriptButtons();
        projectPath.Text = String.IsNullOrWhiteSpace(settings.Ns2ProjectPath) ? (Environment.GetEnvironmentVariable("NS2JB_RUNS") ?? "") : settings.Ns2ProjectPath;
        projectDirectories.Clear();
        if (settings.ProjectDirectories != null)
            foreach (string directory in settings.ProjectDirectories) RememberProject(directory);
        RememberProject(projectPath.Text);
        projectPathChanged = false;
        RebuildProjects();
        presets = settings.Presets; RebuildPresetButtons(); UpdateSendLabel();
        loadingSettings = false;
    }

    bool SaveCurrentSettings()
    {
        if (loadingSettings) return true;
        var settings = new SenderSettings { StartDelayMs = (int)(startDelay.Value * 1000), KeyGapMs = (int)keyGap.Value,
            LineGapMs = (int)lineGap.Value, ClearIndent = noIndent.Checked, KeepOnTop = keepOnTop.Checked, PresetAutoEnter = presetAutoEnter.Checked,
            Ns2ProjectPath = projectPath.Text, ScriptShortcuts = new List<ScriptShortcut>(scriptShortcuts),
            ProjectDirectories = new List<string>(projectDirectories), Presets = presets };
        try { WriteSettingsFile(settingsPath, settings); return true; }
        catch (Exception ex) { status.Text = "设置未能保存；本次仍可使用，下次启动不会保留：" + ex.Message; return false; }
    }

    static string TypeCommand(string identity, int count, int delayMs, int gap, int newlineWait, bool clearIndent)
    {
        if (count < 1 || count > 8192 || delayMs < 0 || delayMs > 10000 || gap < 1 || gap > 1000 || newlineWait < 0 || newlineWait > 3000)
            throw new ArgumentOutOfRangeException("输入设置超出范围。");
        if (Array.IndexOf(identity.Split(' '), "TYPE2=1") >= 0)
            return String.Format("TYPE2 {0} {1} {2} {3} {4}", delayMs, count, gap, newlineWait, clearIndent ? 1 : 0);
        if (gap == 12 && newlineWait == 0 && !clearIndent) return "TYPE " + delayMs + " " + count;
        throw new Exception("固件未提供 TYPE2；请使用现有配套 v1.3 固件。旧模式仅支持逐字间隔 12、换行等待 0、关闭防缩进。");
    }

    static int CompletionTimeout(byte[] data, int delayMs, int gap, int newlineWait, bool clearIndent)
    {
        long ms = delayMs;
        foreach (byte b in data) {
            if (b != 10) ms += gap + 20;
            else {
                ms += 20 + Math.Max(Math.Max(gap, newlineWait), clearIndent ? 100 : 0);
                if (clearIndent) ms += 130 + Math.Max(gap, 30);
            }
        }
        return (int)Math.Min(Int32.MaxValue, ms + ms / 4 + 30000);
    }

    string Transmit(string port, byte[] data, int delayMs, Action queued, bool requireFocus,
                    int gap = 12, int newlineWait = 0, bool clearIndent = false)
    {
        SerialPort serial = connection;
        try {
            if (serial == null || !serial.IsOpen || serial.PortName != port) {
                CloseConnection();
                serial = new SerialPort(port, 115200);
                connection = serial;
                serial.NewLine = "\n";
                serial.DtrEnable = true; serial.RtsEnable = true;
                serial.ReadTimeout = 5000; serial.WriteTimeout = 5000;
                serial.Open();
                Thread.Sleep(1200);
            }
            if (closing) throw new OperationCanceledException("窗口已关闭。");
            active = serial;
            serial.ReadTimeout = 5000;
            serial.DiscardInBuffer();
            serial.WriteLine("PING");
            string identity = serial.ReadLine().Trim();
            if (!(identity == "ESP32S3-TEXTKEY/1" || identity.StartsWith("ESP32S3-TEXTKEY/1 ")))
                throw new Exception("所选串口不是配套键盘固件：" + identity);
            if (requireFocus && GetForegroundWindow() != Handle) throw new Exception("测试窗口不在前台，未发送按键。");
            if (data == null) {
                serial.WriteLine("KEY 193 1");
                string keyResult = serial.ReadLine().Trim();
                if (keyResult != "DONE") throw new Exception(keyResult);
                return keyResult;
            }
            serial.WriteLine(TypeCommand(identity, data.Length, delayMs, gap, newlineWait, clearIndent));
            string response = serial.ReadLine().Trim();
            if (response != "READY") throw new Exception(response);
            serial.Write(data, 0, data.Length);
            response = serial.ReadLine().Trim();
            if (response != "QUEUED") throw new Exception(response);
            queued();
            serial.ReadTimeout = CompletionTimeout(data, delayMs, gap, newlineWait, clearIndent);
            response = serial.ReadLine().Trim();
            if (response != "DONE" && response != "CANCELLED") throw new Exception(response);
            return response;
        } catch { CloseConnection(); throw; }
        finally { active = null; }
    }

    static void OfflineTest(string path)
    {
        int passed = 0;
        Action<bool, string> check = (value, name) => { if (!value) throw new Exception(name); passed++; };
        Action<Action, string> rejects = (action, name) => {
            bool rejected = false; try { action(); } catch { rejected = true; } check(rejected, name);
        };
        string settingsTestPath = Path.Combine(Path.GetTempPath(), "TextSender-test-" + Guid.NewGuid().ToString("N") + ".xml");
        try {
            check(Encoding.ASCII.GetString(Encode("  A\r\n\r\n B\rC")) == "  A\n\n B\nC", "newline normalization / preserved indentation");
            rejects(() => Encode("中文"), "ASCII guard");
            rejects(() => Encode(""), "empty input");
            check(Encode(new string('a', 8192)).Length == 8192, "maximum size");
            rejects(() => Encode(new string('a', 8193)), "oversize");
            check(TypeCommand("ESP32S3-TEXTKEY/1 TYPE2=1", 10, 3000, 30, 150, true) == "TYPE2 3000 10 30 150 1", "extended protocol");
            check(TypeCommand("ESP32S3-TEXTKEY/1 TYPE2=1", 10, 0, 30, 150, false) == "TYPE2 0 10 30 150 0", "zero delay stays zero");
            check(TypeCommand("ESP32S3-TEXTKEY/1 TYPE2=1", 10, (int)(0.1M * 1000), 30, 150, false) == "TYPE2 100 10 30 150 0", "fractional seconds map to milliseconds");
            check(TypeCommand("ESP32S3-TEXTKEY/1", 10, 0, 12, 0, false) == "TYPE 0 10", "legacy protocol");
            rejects(() => TypeCommand("ESP32S3-TEXTKEY/1", 10, 0, 30, 0, false), "old firmware interval guard");
            rejects(() => TypeCommand("ESP32S3-TEXTKEY/1", 10, 0, 12, 0, true), "old firmware indentation guard");
            rejects(() => TypeCommand("ESP32S3-TEXTKEY/1 TYPE2=10", 10, 0, 30, 0, false), "exact capability token");
            rejects(() => TypeCommand("TYPE2=1", 1, 0, 0, 0, false), "minimum interval");
            rejects(() => TypeCommand("TYPE2=1", 1, 0, 1001, 0, false), "maximum interval");
            rejects(() => TypeCommand("TYPE2=1", 1, 0, 12, 3001, false), "maximum newline wait");
            rejects(() => TypeCommand("TYPE2=1", 1, -1, 30, 0, false), "minimum start delay");
            rejects(() => TypeCommand("TYPE2=1", 1, 10001, 30, 0, false), "maximum start delay");
            check(CompletionTimeout(Encode(new string('a', 8192)), 3000, 1000, 0, false) > 8192000, "long sends timeout");
            check(CompletionTimeout(Encode(new string('\n', 8192)), 3000, 1000, 3000, true) > 8192 * 4130, "newline cleanup timeout");
            var preset = new PhrasePreset { Name = "多行测试", Text = "  A\r\nB\t!", AppendEnter = false };
            check(Encoding.ASCII.GetString(Encode(PresetText(preset))) == "  A\nB\t!", "preset keeps exact text without Enter");
            preset.AppendEnter = true;
            check(Encoding.ASCII.GetString(Encode(PresetText(preset))) == "  A\nB\t!\n", "preset appends exactly one Enter");
            preset.Text = "A\n";
            check(PresetText(preset) == "A\n\n", "append Enter does not strip existing newline");
            rejects(() => ValidatePreset(new PhrasePreset { Name = "中文内容", Text = "内容" }), "preset ASCII guard");
            rejects(() => ValidatePreset(new PhrasePreset { Name = "too long", Text = new string('a', 8192), AppendEnter = true }), "Enter counts toward byte maximum");
            rejects(() => ValidatePreset(new PhrasePreset { Name = " ", Text = "A" }), "preset name required");
            var settings = SenderSettings.Defaults();
            check(settings.KeepOnTop, "always on top enabled by default");
            check(settings.StartDelayMs == 0 && settings.Presets.Count == 2 && !settings.Presets[0].AppendEnter && !settings.Presets[1].AppendEnter, "safe defaults");
            settings.StartDelayMs = 1700; settings.KeyGapMs = 40; settings.LineGapMs = 250; settings.ClearIndent = true;
            settings.KeepOnTop = false; settings.Ns2ProjectPath = "D:\\ns2jb test\\项目";
            settings.Presets[0].Name = "首项 & <测试>"; settings.Presets[0].Text = "  A\r\n B\nC"; settings.Presets[0].AppendEnter = true;
            WriteSettingsFile(settingsTestPath, settings);
            SenderSettings restored = ReadSettingsFile(settingsTestPath);
            check(!restored.KeepOnTop && restored.Ns2ProjectPath == settings.Ns2ProjectPath, "topmost and project path settings roundtrip");
            check(restored.StartDelayMs == 1700 && restored.KeyGapMs == 40 && restored.LineGapMs == 250 && restored.ClearIndent, "timing settings roundtrip");
            check(restored.Presets.Count == 2 && restored.Presets[0].Name == settings.Presets[0].Name && restored.Presets[1].Name == settings.Presets[1].Name, "preset names and order roundtrip");
            check(restored.Presets[0].Text == settings.Presets[0].Text && restored.Presets[0].AppendEnter, "preset multiline and Enter roundtrip");
            settings.StartDelayMs = 0; WriteSettingsFile(settingsTestPath, settings);
            check(ReadSettingsFile(settingsTestPath).StartDelayMs == 0, "update existing settings");
            settings.KeyGapMs = 0;
            rejects(() => WriteSettingsFile(settingsTestPath, settings), "invalid settings rejected before replacing file");
            check(ReadSettingsFile(settingsTestPath).KeyGapMs == 40, "invalid settings preserve previous file");
            File.WriteAllText(path, "PASS " + passed + " offline checks; no serial or HID operations.\r\n", Encoding.UTF8);
        } catch (Exception ex) {
            File.WriteAllText(path, "FAIL after " + passed + ": " + ex, Encoding.UTF8); Environment.ExitCode = 1;
        } finally { if (File.Exists(settingsTestPath)) File.Delete(settingsTestPath); }
    }

    async Task SelfTest(string port, string resultPath)
    {
        string sample = "ESP32-S3 test 123 AaZz !@#$%^&*()_+-=[]{};:',.<>/?\\|\nSecond line.";
        try {
            SetBusy(true); editor.ReadOnly = false; editor.Text = ""; editor.ImeMode = ImeMode.Disable;
            foreach (InputLanguage language in InputLanguage.InstalledInputLanguages)
                if (language.Culture.Name == "en-US") { InputLanguage.CurrentInputLanguage = language; break; }
            Activate(); editor.Focus(); status.Text = "正在测试实际 USB 键盘输入……";
            await Task.Delay(600);
            IntPtr ownHandle = Handle;
            if (GetForegroundWindow() != ownHandle) throw new Exception("测试窗口未获得焦点，未发送按键。");
            string result = await Task.Run(() => Transmit(port, Encode(sample), 0, delegate {}, true));
            await Task.Delay(300);
            string captured = editor.Text.Replace("\r\n", "\n");
            bool pass = result == "DONE" && captured == sample;
            File.WriteAllText(resultPath, (pass ? "PASS" : "FAIL") + "\r\nFirmware: " + result + "\r\nExpected: " + sample + "\r\nCaptured: " + captured, Encoding.UTF8);
        } catch (Exception ex) { File.WriteAllText(resultPath, "FAIL\r\n" + ex.ToString(), Encoding.UTF8); }
        Close();
    }
}
