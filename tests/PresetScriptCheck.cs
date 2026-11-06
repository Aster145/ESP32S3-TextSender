using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

// Offline checks for script presets: parsing, key names, validation, settings
// round-trip and migration, the manager UI, and the generated help text.
// Uses an isolated settings file; never opens a serial port, never starts a
// script process, and never sends HID keys.

class PresetScriptCheck
{
    static int passed;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type appType, presetType, stepType, parserType;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
    static object Property(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    static void SetProperty(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    static object Field(object form, string name) { return form.GetType().GetField(name, PrivateInstance).GetValue(form); }
    static void SetField(object form, string name, object value) { form.GetType().GetField(name, PrivateInstance).SetValue(form, value); }
    static object Call(object form, string name, params object[] args) { return form.GetType().GetMethod(name, PrivateInstance).Invoke(form, args); }
    static object StaticCall(string name, params object[] args) { return appType.GetMethod(name, AnyStatic).Invoke(null, args); }
    static object ParserCall(string name, params object[] args) { return parserType.GetMethod(name, AnyStatic).Invoke(null, args); }
    static IList List(params object[] items)
    {
        IList result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(presetType));
        foreach (object item in items) result.Add(item);
        return result;
    }
    static object Preset(string name, string text, bool appendEnter, bool isScript)
    {
        object item = Activator.CreateInstance(presetType);
        SetProperty(item, "Name", name); SetProperty(item, "Text", text);
        SetProperty(item, "AppendEnter", appendEnter); SetProperty(item, "IsScript", isScript);
        return item;
    }
    static string Describe(object step)
    {
        return (string)stepType.GetMethod("Describe").Invoke(step, null);
    }
    // Maps PresetScriptStepKind declaration order to a stable test index.
    static int Kind(object step) { return Convert.ToInt32(Property(step, "Kind")); }
    static readonly int KindText = 0, KindKey = 1, KindWait = 2, KindRun = 3, KindVStart = 4;

    static void RejectParse(string script, string messageFragment, string label)
    {
        bool rejected = false;
        try { ParserCall("Parse", script); }
        catch (TargetInvocationException error)
        {
            rejected = error.InnerException != null && error.InnerException is Exception &&
                error.InnerException.Message.Contains(messageFragment);
            if (!rejected) throw new Exception(label + " - unexpected message: " + (error.InnerException == null ? "" : error.InnerException.Message));
        }
        Check(rejected, label);
    }

    static void RejectValidate(object preset, string messageFragment, string label)
    {
        bool rejected = false;
        try { StaticCall("ValidatePreset", preset); }
        catch (TargetInvocationException error)
        {
            rejected = error.InnerException != null && error.InnerException.Message.Contains(messageFragment);
            if (!rejected) throw new Exception(label + " - unexpected message: " + (error.InnerException == null ? "" : error.InnerException.Message));
        }
        Check(rejected, label);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: PresetScriptCheck.exe TextSender.exe report.txt"); return 2; }
        string report = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(Path.GetDirectoryName(report), "preset-script-" + Guid.NewGuid().ToString("N"));
        string oldEnvironment = Environment.GetEnvironmentVariable("NS2JB_RUNS");
        try {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);
            Directory.CreateDirectory(fixture);
            string settingsPath = Path.Combine(fixture, "settings.xml");

            Assembly app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            appType = app.GetType("TextSender", true);
            presetType = app.GetType("PhrasePreset", true);
            stepType = app.GetType("PresetScriptStep", true);
            parserType = app.GetType("PresetScript", true);

            // --- Parsing: kinds, parameters, comments, line numbers. ---
            IList steps = (IList)ParserCall("Parse", new object[] {
                "# 一条注释\r\nTEXT Hello\\nWorld\r\n\r\nWAIT 2\nKEY Enter\nKEY Backspace x3\nKEY delete x 2\nRUN FTP"
            });
            Check(steps.Count == 6, "comments and blank lines must be skipped");
            Check(Kind(steps[0]) == KindText && (string)Property(steps[0], "Text") == "Hello\nWorld", "TEXT escapes must decode");
            Check(Kind(steps[1]) == KindWait && (int)Property(steps[1], "WaitMs") == 2000, "WAIT seconds map to milliseconds");
            Check(Kind(steps[2]) == KindKey && (int)Property(steps[2], "KeyCode") == 176 && (int)Property(steps[2], "Repeat") == 1, "KEY Enter default repeat is 1");
            Check(Kind(steps[3]) == KindKey && (int)Property(steps[3], "KeyCode") == 178 && (int)Property(steps[3], "Repeat") == 3, "KEY Backspace x3 repeats");
            Check(Kind(steps[4]) == KindKey && (int)Property(steps[4], "KeyCode") == 212 && (int)Property(steps[4], "Repeat") == 2, "KEY delete x 2 parses two tokens");
            Check(Kind(steps[5]) == KindRun && (string)Property(steps[5], "Text") == "FTP", "RUN stores the shortcut name");
            Check((int)Property(steps[4], "LineNumber") == 7, "line numbers count real source lines");
            Check((int)Property(steps[5], "LineNumber") == 8 && Kind(steps[5]) == KindRun, "RUN keeps its own line number");

            // VSTART: built-in V=START send, case-insensitive, no arguments.
            IList vstart = (IList)ParserCall("Parse", new object[] { "vStart" });
            Check(vstart.Count == 1 && Kind(vstart[0]) == KindVStart, "VSTART parses as its own step kind");
            Check(Describe(vstart[0]).Contains("V=START"), "VSTART describes the V=START action");
            Check((int)Property(vstart[0], "LineNumber") == 1, "VSTART carries its line number");

            // Mixed-case verbs, tab separators, literal backslash escape, leading spaces.
            IList mixed = (IList)ParserCall("Parse", new object[] {
                "  text  A\\tB\\\\C \n\tkey\tTab\nWait 0.25\nrun  My Script 01 "
            });
            Check(mixed.Count == 4 && (string)Property(mixed[0], "Text") == "A\tB\\C", "TEXT decodes escapes and keeps inner spaces");
            Check(Kind(mixed[1]) == KindKey && (int)Property(mixed[1], "KeyCode") == 179, "tab-separated KEY parses");
            Check((int)Property(mixed[2], "WaitMs") == 250, "fractional WAIT seconds parse");
            Check((string)Property(mixed[3], "Text") == "My Script 01", "RUN keeps the full rest of the line");

            // Key code table sanity and aliases.
            string[] names = (string[])parserType.GetMethod("AllKeyNames").Invoke(null, null);
            var expected = new HashSet<string>(StringComparer.Ordinal) {
                "Enter", "Return", "Esc", "Escape", "Backspace", "Tab", "CapsLock",
                "Insert", "Home", "PageUp", "Delete", "End", "PageDown",
                "Up", "UpArrow", "Down", "DownArrow", "Left", "LeftArrow", "Right", "RightArrow"
            };
            for (int function = 1; function <= 12; function++) expected.Add("F" + function);
            foreach (string name in names) { Check(expected.Contains(name), "unexpected key name " + name); expected.Remove(name); }
            Check(expected.Count == 0, "key name table is missing entries");
            foreach (string name in new[] { "return", "ESCAPE", "f5", "upArrow", "pageUP" }) {
                IList one = (IList)parserType.GetMethod("Parse").Invoke(null, new object[] { "KEY " + name });
                Check(Kind(one[0]) == KindKey && (int)Property(one[0], "KeyCode") >= 0x88 && (int)Property(one[0], "KeyCode") <= 0xFF,
                    "key name " + name + " must be case-insensitive and in firmware range");
            }
            IList f12 = (IList)parserType.GetMethod("Parse").Invoke(null, new object[] { "KEY F12" });
            Check((int)Property(f12[0], "KeyCode") == 205, "F12 key code");

            // Describe used by the status line.
            IList described = (IList)parserType.GetMethod("Parse").Invoke(null, new object[] { "KEY Backspace x3" });
            Check(Describe(described[0]).Contains("Backspace x3"), "KEY Describe includes the repeat count");
            IList waitDescribed = (IList)parserType.GetMethod("Parse").Invoke(null, new object[] { "WAIT 0.5" });
            Check(Describe(waitDescribed[0]).Contains("0.5"), "WAIT Describe shows seconds");

            // --- Rejections. ---
            RejectParse("PRINT x", "未知指令", "unknown verb rejected");
            RejectParse("TEXT x\nRUN", "第 2 行", "errors carry the failing line number");
            RejectParse("KEY Nope", "未知按键名", "unknown key rejected");
            RejectParse("KEY", "缺少按键名", "KEY without a name rejected");
            RejectParse("KEY Enter x0", "次数", "repeat 0 rejected");
            RejectParse("KEY Enter x101", "次数", "repeat 101 rejected");
            RejectParse("KEY Enter 3", "次数", "bare repeat number rejected");
            RejectParse("KEY Enter x2 y", "参数太多", "extra KEY arguments rejected");
            RejectParse("WAIT abc", "数字秒数", "non-numeric WAIT rejected");
            RejectParse("WAIT -1", "范围", "negative WAIT rejected");
            RejectParse("WAIT 60.1", "范围", "WAIT above 60 seconds rejected");
            RejectParse("TEXT", "缺少", "empty TEXT rejected");
            RejectParse("TEXT 中文", "TEXT 内容无效", "non-ASCII TEXT rejected");
            RejectParse("TEXT " + new string('a', 8193), "TEXT 内容无效", "oversize TEXT rejected");
            RejectParse("TEXT a\\q", "不支持的转义", "unknown escape rejected");
            RejectParse("TEXT trailing\\", "转义不完整", "trailing backslash rejected");
            RejectParse("RUN", "缺少脚本名称", "RUN without a name rejected");
            RejectParse("VSTART now", "不需要参数", "VSTART with arguments rejected");
            RejectParse("", "脚本内容为空", "empty script rejected");
            RejectParse("# only\n# comments", "脚本内容为空", "comment-only script rejected");

            // --- ValidatePreset branches on IsScript. ---
            StaticCall("ValidatePreset", Preset("OK 脚本", "TEXT A\nWAIT 1", false, true));
            StaticCall("ValidatePreset", Preset("OK 文本", "A", false, false));
            RejectValidate(Preset("坏脚本", "WAIT 99", false, true), "脚本短语「坏脚本」", "script validation names the preset");
            RejectValidate(Preset("空脚本", "", false, true), "脚本内容为空", "script validation rejects empty scripts");

            // --- Help text is generated from the real key table. ---
            string help = (string)parserType.GetMethod("HelpText").Invoke(null, null);
            foreach (string keyword in new[] { "TEXT", "KEY", "WAIT", "RUN", "VSTART", "Backspace x3", "RUN FTP", "60", "8192", "Enter", "Delete", "F1", "Up" })
                Check(help.Contains(keyword), "help text is missing " + keyword);

            // --- Settings round-trip and migration from old files. ---
            Type settingsType = app.GetType("SenderSettings", true);
            object defaults = settingsType.GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            IList defaultPresets = (IList)Property(defaults, "Presets");
            Check(defaultPresets.Count == 3, "defaults include the script example preset");
            Check((bool)Property(defaultPresets[2], "IsScript"), "default script preset is a script");
            // ASCII-only script source so the same file also validates as a legacy plain preset.
            SetProperty(defaultPresets[2], "Text", "TEXT migrate\\tcheck\nWAIT 0.5\nRUN Project Script");
            StaticCall("WriteSettingsFile", settingsPath, defaults);
            SenderSettingsRoundTrip(settingsPath);
            // An old settings file without IsScript must load every preset as plain text.
            XmlDocument old = new XmlDocument(); old.Load(settingsPath);
            foreach (XmlNode node in old.SelectNodes("/SenderSettings/Presets/PhrasePreset/IsScript"))
                node.ParentNode.RemoveChild(node);
            old.Save(settingsPath);
            using (Form form = CreateForm(settingsPath)) {
                IList loaded = (IList)Field(form, "presets");
                Check(loaded.Count == 3 && !(bool)Property(loaded[0], "IsScript") && !(bool)Property(loaded[2], "IsScript"), "old settings load as plain text presets");
                NoSerial(form);
            }
            // Writing after loading must persist script flags again.
            using (Form form = CreateForm(settingsPath)) {
                SetProperty(((IList)Field(form, "presets"))[2], "IsScript", true);
                Check((bool)Call(form, "SaveCurrentSettings"), "saving script presets failed");
                NoSerial(form);
            }
            SenderSettingsRoundTrip(settingsPath);

            // --- Manager UI: script checkbox, disabled Enter option, deep copy. ---
            Type managerType = app.GetType("PresetManager", true);
            IList source = List(Preset("文本项", "Hello", false, false), Preset("脚本项", "TEXT A\nWAIT 1", true, true));
            using (var manager = (Form)Activator.CreateInstance(managerType, new object[] { source })) {
                IList draft = (IList)Property(manager, "Result");
                Check(!Object.ReferenceEquals(source, draft) && !Object.ReferenceEquals(source[1], draft[1]), "manager deep-copies presets");
                var scriptBox = (CheckBox)Field(manager, "isScript");
                var enterBox = (CheckBox)Field(manager, "appendEnter");
                var syntaxButton = (Button)Field(manager, "syntax");
                var label = (Label)Field(manager, "textLabel");
                Check(scriptBox != null && syntaxButton != null && label != null, "manager exposes script mode controls");
                // The constructor auto-selects the first item: plain text mode.
                Check(!scriptBox.Checked && enterBox.Enabled && !label.Text.Contains("脚本"), "first item starts in plain text mode");
                var list = (ListBox)Field(manager, "list");
                list.SelectedIndex = 1;
                Check(scriptBox.Checked && !enterBox.Enabled && label.Text.Contains("脚本"), "script item disables Enter option and relabels");
                // Toggling the checkbox edits the draft item.
                scriptBox.Checked = false;
                Check(!(bool)Property(draft[1], "IsScript"), "checkbox edits the draft preset");
                scriptBox.Checked = true;
                Check((bool)Property(draft[1], "IsScript"), "checkbox edits the draft preset back");
            }
            // Cancelled edits must not touch the caller's items.
            using (var manager = (Form)Activator.CreateInstance(managerType, new object[] { source })) {
                IList draft = (IList)Property(manager, "Result");
                SetProperty(draft[0], "IsScript", true);
                manager.DialogResult = DialogResult.Cancel;
                Check(!(bool)Property(source[0], "IsScript"), "cancelled script toggle leaked into the source list");
            }

            // --- Main window buttons and stoppable waits. ---
            object settings = settingsType.GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            IList presetList = (IList)Property(settings, "Presets");
            presetList.Clear();
            presetList.Add(Preset("我的脚本", "TEXT Hi\nWAIT 2\nKEY Enter", false, true));
            presetList.Add(Preset("普通", "Hello", true, false));
            StaticCall("WriteSettingsFile", settingsPath, settings);
            using (Form form = CreateForm(settingsPath)) {
                var panel = (FlowLayoutPanel)Field(form, "phraseButtons");
                var buttons = new List<Button>();
                foreach (Control control in panel.Controls) { var button = control as Button; if (button != null) buttons.Add(button); }
                Check(buttons.Count == 2 && buttons[0].Text == "我的脚本", "script preset gets its own button");
                var tips = (ToolTip)Field(form, "tips");
                Check(tips.GetToolTip(buttons[0]).StartsWith("（脚本短语）"), "script button tooltip is marked");
                Check(!tips.GetToolTip(buttons[1]).StartsWith("（脚本短语）") && tips.GetToolTip(buttons[1]).Contains("回车"), "plain preset tooltip unchanged");
                // WaitOrStop honors the cancel flag without a serial port.
                SetField(form, "scriptCancel", true);
                Check(!(bool)Call(form, "WaitOrStop", 10), "WaitOrStop returns immediately when cancelled");
                SetField(form, "scriptCancel", false);
                Check((bool)Call(form, "WaitOrStop", 10), "WaitOrStop completes a short wait");
                NoSerial(form);
            }

            File.WriteAllText(report, "PASS " + passed + " script preset checks; no serial, process or HID operations.\r\n", Encoding.UTF8);
        } catch (Exception ex) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + ex + "\r\n", Encoding.UTF8);
            return 1;
        } finally {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", oldEnvironment);
            try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); } catch { }
        }
        return 0;
    }

    static void SenderSettingsRoundTrip(string settingsPath)
    {
        object restored = StaticCall("ReadSettingsFile", settingsPath);
        IList presets = (IList)Property(restored, "Presets");
        Check(presets.Count == 3 && (bool)Property(presets[2], "IsScript"), "script flag round-trips");
        Check((string)Property(presets[2], "Text") == "TEXT migrate\\tcheck\nWAIT 0.5\nRUN Project Script", "script source round-trips exactly");
    }

    static Form CreateForm(string settingsPath)
    {
        var form = (Form)Activator.CreateInstance(appType, PrivateInstance, null, new object[] { new string[0] }, null);
        appType.GetField("settingsPath", PrivateInstance).SetValue(form, settingsPath);
        Call(form, "LoadSettings");
        return form;
    }

    static void NoSerial(Form form)
    {
        Check(Field(form, "connection") == null && Field(form, "active") == null, "script preset check opened a serial port");
    }
}
