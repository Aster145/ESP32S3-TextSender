using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Parser for "script presets": a line-based mini language stored in a
// PhrasePreset. Each line is one instruction (TEXT / KEY / WAIT / RUN / VSTART);
// blank lines and lines starting with '#' are comments. Parsing is pure and
// offline: it never touches the serial port.
//
// KEY codes are the Arduino keyboard constants accepted by the firmware
// "KEY <code> <repeat>" command, which only takes codes 0x88..0xFF.

public enum PresetScriptStepKind { Text, Key, Wait, Run, VStart }

public sealed class PresetScriptStep
{
    public PresetScriptStepKind Kind { get; private set; }
    public int LineNumber { get; private set; }
    // TEXT: decoded content. RUN: script shortcut name.
    public string Text { get; private set; }
    public string KeyName { get; private set; }
    public int KeyCode { get; private set; }
    public int Repeat { get; private set; }
    public int WaitMs { get; private set; }

    internal PresetScriptStep(PresetScriptStepKind kind, int lineNumber)
    {
        Kind = kind;
        LineNumber = lineNumber;
        Repeat = 1;
    }

    internal void SetText(string value) { Text = value; }
    internal void SetKey(string name, int code, int repeat) { KeyName = name; KeyCode = code; Repeat = repeat; }
    internal void SetWait(int milliseconds) { WaitMs = milliseconds; }

    public string Describe()
    {
        switch (Kind)
        {
            case PresetScriptStepKind.Text:
                {
                    string preview = (Text ?? "").Replace("\n", "\\n").Replace("\t", "\\t");
                    if (preview.Length > 24) preview = preview.Substring(0, 24) + "…";
                    return "TEXT " + preview;
                }
            case PresetScriptStepKind.Key:
                return "KEY " + KeyName + (Repeat > 1 ? " x" + Repeat : "");
            case PresetScriptStepKind.Wait:
                return "WAIT " + (WaitMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " 秒";
            case PresetScriptStepKind.Run:
                return "RUN " + Text;
            case PresetScriptStepKind.VStart:
                return "VSTART（获取并发送 V=START）";
        }
        return "";
    }
}

public static class PresetScript
{
    public const int MaximumTextLength = 8192;
    public const int MinimumWaitSeconds = 0;
    public const int MaximumWaitSeconds = 60;
    public const int MinimumRepeat = 1;
    public const int MaximumRepeat = 100;

    // Arduino keyboard key codes; the firmware KEY command only accepts 0x88..0xFF.
    static readonly Dictionary<string, int> KeyCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        { "Enter", 0xB0 }, { "Return", 0xB0 },
        { "Esc", 0xB1 }, { "Escape", 0xB1 },
        { "Backspace", 0xB2 },
        { "Tab", 0xB3 },
        { "CapsLock", 0xC1 },
        { "Insert", 0xD1 }, { "Home", 0xD2 }, { "PageUp", 0xD3 }, { "Delete", 0xD4 },
        { "End", 0xD5 }, { "PageDown", 0xD6 },
        { "Right", 0xD7 }, { "RightArrow", 0xD7 },
        { "Left", 0xD8 }, { "LeftArrow", 0xD8 },
        { "Down", 0xD9 }, { "DownArrow", 0xD9 },
        { "Up", 0xDA }, { "UpArrow", 0xDA }
    };

    static PresetScript()
    {
        for (int index = 1; index <= 12; index++) KeyCodes["F" + index.ToString(CultureInfo.InvariantCulture)] = 0xC1 + index;
        foreach (int code in KeyCodes.Values)
            if (code < 0x88 || code > 0xFF)
                throw new InvalidOperationException("按键键码超出固件 KEY 命令的 0x88～0xFF 范围：" + code.ToString(CultureInfo.InvariantCulture));
    }

    public static string[] AllKeyNames()
    {
        var names = new List<string>(KeyCodes.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    public static List<PresetScriptStep> Parse(string source)
    {
        var steps = new List<PresetScriptStep>();
        string[] lines = (source ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int separator = line.IndexOfAny(new char[] { ' ', '\t' });
            string verb = separator < 0 ? line : line.Substring(0, separator);
            string rest = separator < 0 ? "" : line.Substring(separator + 1).TrimStart(' ', '\t');
            int number = index + 1;
            if (String.Equals(verb, "TEXT", StringComparison.OrdinalIgnoreCase)) steps.Add(ParseText(rest, number));
            else if (String.Equals(verb, "KEY", StringComparison.OrdinalIgnoreCase)) steps.Add(ParseKey(rest, number));
            else if (String.Equals(verb, "WAIT", StringComparison.OrdinalIgnoreCase)) steps.Add(ParseWait(rest, number));
            else if (String.Equals(verb, "RUN", StringComparison.OrdinalIgnoreCase)) steps.Add(ParseRun(rest, number));
            else if (String.Equals(verb, "VSTART", StringComparison.OrdinalIgnoreCase)) steps.Add(ParseVStart(rest, number));
            else throw Error(number, "未知指令「" + verb + "」，只支持 TEXT、KEY、WAIT、RUN 和 VSTART。");
        }
        if (steps.Count == 0)
            throw new Exception("脚本内容为空，至少需要一条 TEXT、KEY、WAIT、RUN 或 VSTART 指令。");
        return steps;
    }

    static PresetScriptStep ParseText(string rest, int number)
    {
        if (rest.Length == 0) throw Error(number, "TEXT 缺少要发送的文字。");
        string text = DecodeEscapes(rest, number);
        try { TextSender.Encode(text); }
        catch (Exception ex) { throw Error(number, "TEXT 内容无效：" + ex.Message); }
        var step = new PresetScriptStep(PresetScriptStepKind.Text, number);
        step.SetText(text);
        return step;
    }

    static PresetScriptStep ParseKey(string rest, int number)
    {
        string[] tokens = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw Error(number, "KEY 缺少按键名。");
        string name = tokens[0];
        int code;
        if (!KeyCodes.TryGetValue(name, out code))
            throw Error(number, "未知按键名「" + name + "」，可用按键名见「脚本语法…」说明。");
        int repeat = 1;
        if (tokens.Length == 1) { }
        else if (tokens.Length == 2) repeat = ParseRepeatToken(tokens[1], number);
        else if (tokens.Length == 3 && String.Equals(tokens[1], "x", StringComparison.OrdinalIgnoreCase))
            repeat = ParseCount(tokens[2], number);
        else throw Error(number, "KEY 参数太多，格式是 KEY 键名 x次数，例如 KEY Backspace x3。");
        var step = new PresetScriptStep(PresetScriptStepKind.Key, number);
        step.SetKey(name, code, repeat);
        return step;
    }

    static int ParseRepeatToken(string token, int number)
    {
        if (token.Length > 1 && (token[0] == 'x' || token[0] == 'X'))
            return ParseCount(token.Substring(1), number);
        throw Error(number, "KEY 的第二个参数是连按次数（1～100），例如 KEY Backspace x3。");
    }

    static int ParseCount(string value, int number)
    {
        int repeat;
        if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out repeat) ||
            repeat < MinimumRepeat || repeat > MaximumRepeat)
            throw Error(number, "连按次数必须是 " + MinimumRepeat + "～" + MaximumRepeat + " 的整数。");
        return repeat;
    }

    static PresetScriptStep ParseWait(string rest, int number)
    {
        decimal seconds;
        if (!Decimal.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            throw Error(number, "WAIT 的等待时间必须是数字秒数，例如 WAIT 2 或 WAIT 0.5。");
        if (seconds < MinimumWaitSeconds || seconds > MaximumWaitSeconds)
            throw Error(number, "等待时间范围是 " + MinimumWaitSeconds + "～" + MaximumWaitSeconds + " 秒。");
        var step = new PresetScriptStep(PresetScriptStepKind.Wait, number);
        step.SetWait((int)Math.Round(seconds * 1000M));
        return step;
    }

    static PresetScriptStep ParseRun(string rest, int number)
    {
        string name = rest.Trim();
        if (name.Length == 0)
            throw Error(number, "RUN 缺少脚本名称，应填写当前项目里的脚本快捷按钮名称。");
        var step = new PresetScriptStep(PresetScriptStepKind.Run, number);
        step.SetText(name);
        return step;
    }

    static PresetScriptStep ParseVStart(string rest, int number)
    {
        if (rest.Trim().Length > 0)
            throw Error(number, "VSTART 不需要参数，直接写一行 VSTART 即可。");
        return new PresetScriptStep(PresetScriptStepKind.VStart, number);
    }

    static string DecodeEscapes(string value, int number)
    {
        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character != '\\') { result.Append(character); continue; }
            if (index + 1 >= value.Length) throw Error(number, "转义不完整：行尾的 \\ 后缺少字符。");
            char next = value[++index];
            if (next == 'n') result.Append('\n');
            else if (next == 't') result.Append('\t');
            else if (next == '\\') result.Append('\\');
            else throw Error(number, "不支持的转义 \\" + next + "，只支持 \\n（回车）、\\t（Tab）和 \\\\（反斜杠）。");
        }
        return result.ToString();
    }

    static Exception Error(int number, string message)
    {
        return new Exception("第 " + number.ToString(CultureInfo.InvariantCulture) + " 行：" + message);
    }

    public static string HelpText()
    {
        var text = new StringBuilder();
        text.AppendLine("脚本短语语法");
        text.AppendLine();
        text.AppendLine("每行一条指令；空行和以 # 开头的行是注释；指令名和按键名大小写不限。");
        text.AppendLine();
        text.AppendLine("TEXT <文字>            发送文字。\\n=回车  \\t=Tab  \\\\=反斜杠；");
        text.AppendLine("                       空格直接写在文字里；最多 " + MaximumTextLength + " 个 ASCII 字符。");
        text.AppendLine("KEY <按键名> [x次数]   按一下按键；连按加次数（" + MinimumRepeat + "～" + MaximumRepeat + "），");
        text.AppendLine("                       例如 KEY Enter 或 KEY Backspace x3。");
        text.AppendLine("WAIT <秒>             等待，支持小数（0.5 = 半秒），范围 " + MinimumWaitSeconds + "～" + MaximumWaitSeconds + " 秒。");
        text.AppendLine("RUN <脚本名>           启动当前项目里该名称的脚本快捷按钮（“管理脚本…”中配置）。");
        text.AppendLine("VSTART                 读取并发送当前项目的最新 V=START 连接口令，");
        text.AppendLine("                       等同于主窗口的“获取并发送 V=START”按钮；不需要参数。");
        text.AppendLine();
        text.AppendLine("示例：发文字 → 等 2 秒 → 回车 → 再发 → 退格 3 次 → 等 3 秒 → 启动脚本");
        text.AppendLine();
        text.AppendLine("  # 井号开头的行是注释");
        text.AppendLine("  TEXT Hello, target device!");
        text.AppendLine("  WAIT 2");
        text.AppendLine("  KEY Enter");
        text.AppendLine("  TEXT Second message");
        text.AppendLine("  KEY Backspace x3");
        text.AppendLine("  WAIT 3");
        text.AppendLine("  RUN FTP");
        text.AppendLine();
        text.AppendLine("可用按键名（括号内是同义写法）：");
        foreach (string line in GroupedKeyNames()) text.AppendLine("  " + line);
        text.AppendLine();
        text.AppendLine("说明：");
        text.AppendLine("· 脚本不使用“发送前等待”倒计时和“发送后自动回车”，间隔全部由 WAIT 控制；");
        text.AppendLine("  逐字间隔、换行等待沿用主窗口当前设置。");
        text.AppendLine("· 执行期间可随时点击主窗口的“停止”，包括等待中。");
        text.AppendLine("· RUN 的脚本快捷项不存在时会提示并停止后续步骤。");
        text.AppendLine("· VSTART 在口令会话已过期（ns2jb / HBLauncher 已结束）时会提示并停止后续步骤。");
        return text.ToString();
    }

    // Build the key list from the code table itself so the help text can
    // never drift from the actually supported keys.
    static List<string> GroupedKeyNames()
    {
        var namesByCode = new Dictionary<int, List<string>>();
        foreach (KeyValuePair<string, int> pair in KeyCodes)
        {
            List<string> names;
            if (!namesByCode.TryGetValue(pair.Value, out names)) { names = new List<string>(); namesByCode[pair.Value] = names; }
            if (!names.Contains(pair.Key)) names.Add(pair.Key);
        }
        var codes = new List<int>(namesByCode.Keys);
        codes.Sort();
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (int code in codes)
        {
            namesByCode[code].Sort(StringComparer.OrdinalIgnoreCase);
            string group = String.Join("/", namesByCode[code].ToArray());
            if (current.Length > 0)
            {
                if (current.Length + 2 + group.Length > 72) { lines.Add(current.ToString()); current.Length = 0; }
                else current.Append("  ");
            }
            current.Append(group);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }
}
