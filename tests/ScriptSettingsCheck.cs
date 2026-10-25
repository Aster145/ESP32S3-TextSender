using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

class ScriptSettingsCheck
{
    static int passed;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    static Type appType, shortcutType;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
    static object Property(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    static void SetProperty(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    static object Field(object form, string name) { return form.GetType().GetField(name, PrivateInstance).GetValue(form); }
    static object Call(object form, string name, params object[] args) { return form.GetType().GetMethod(name, PrivateInstance).Invoke(form, args); }
    static object StaticCall(string name, params object[] args) { return appType.GetMethod(name, PrivateStatic).Invoke(null, args); }
    static XmlDocument ReadXml(string path) { var xml = new XmlDocument(); xml.Load(path); return xml; }
    static void WriteXml(XmlDocument xml, string path) {
        using (XmlWriter writer = XmlWriter.Create(path, new XmlWriterSettings {
            Encoding = new UTF8Encoding(false), Indent = true, NewLineHandling = NewLineHandling.Entitize
        })) xml.Save(writer);
    }
    static Form CreateForm(string settingsPath) {
        var form = (Form)Activator.CreateInstance(appType, PrivateInstance, null, new object[] { new string[0] }, null);
        appType.GetField("settingsPath", PrivateInstance).SetValue(form, settingsPath);
        Call(form, "LoadSettings");
        return form;
    }
    static void NoSerial(Form form) {
        Check(Field(form, "connection") == null && Field(form, "active") == null, "script settings opened a serial port");
    }
    static object Shortcut(string name, string path, string workingDirectory) {
        object item = Activator.CreateInstance(shortcutType);
        SetProperty(item, "Name", name); SetProperty(item, "ScriptPath", path); SetProperty(item, "WorkingDirectory", workingDirectory);
        return item;
    }
    static IList List(params object[] items) {
        IList result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(shortcutType));
        foreach (object item in items) result.Add(item);
        return result;
    }
    static void AssertShortcut(object item, string name, string path, string workingDirectory, string context) {
        Check((string)Property(item, "Name") == name && (string)Property(item, "ScriptPath") == path &&
            ((string)Property(item, "WorkingDirectory") ?? "") == workingDirectory, context);
    }
    static void CheckButtons(Form form, string[] names) {
        var panel = (FlowLayoutPanel)Field(form, "scriptButtons");
        var buttons = new List<Button>();
        foreach (Control control in panel.Controls) { var button = control as Button; if (button != null) buttons.Add(button); }
        Check(buttons.Count == names.Length, "script button count differs from configured shortcut count");
        for (int index = 0; index < names.Length; index++)
            Check(buttons[index].Text == names[index] && !buttons[index].UseMnemonic, "shortcut button name/order or literal ampersand is incorrect");
    }
    static void CheckPreserved(XmlDocument before, XmlDocument after) {
        foreach (string name in new[] { "StartDelayMs", "KeyGapMs", "LineGapMs", "ClearIndent", "KeepOnTop", "PresetAutoEnter", "Ns2ProjectPath", "ProjectDirectories", "Presets" })
            Check(before.SelectSingleNode("/SenderSettings/" + name).OuterXml == after.SelectSingleNode("/SenderSettings/" + name).OuterXml,
                "script changes modified unrelated setting " + name);
    }
    static void RejectStart(object shortcut, Type expected, string messageFragment, string label) {
        bool rejected = false;
        try { StaticCall("CreateScriptStartInfo", shortcut); }
        catch (TargetInvocationException error) {
            rejected = error.InnerException != null && expected.IsInstanceOfType(error.InnerException) &&
                error.InnerException.Message.Contains(messageFragment);
        }
        Check(rejected, label);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: ScriptSettingsCheck.exe TextSender.exe report.txt"); return 2; }
        string report = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(Path.GetDirectoryName(report), "script-settings-" + Guid.NewGuid().ToString("N"));
        string oldEnvironment = Environment.GetEnvironmentVariable("NS2JB_RUNS");
        try {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);
            Directory.CreateDirectory(fixture);
            string configuration = Path.Combine(fixture, "settings.xml");
            string project = Path.Combine(fixture, "Example project");
            string scriptDirectory = Path.Combine(fixture, "Scripts & spaces 项目");
            string customDirectory = Path.Combine(fixture, "Working & spaces 目录");
            Directory.CreateDirectory(project); Directory.CreateDirectory(scriptDirectory); Directory.CreateDirectory(customDirectory);
            string cmd = Path.Combine(scriptDirectory, "First & script.cmd");
            string bat = Path.Combine(scriptDirectory, "Second 脚本.bat");
            string ps1 = Path.Combine(scriptDirectory, "Third & 脚本.ps1");
            File.WriteAllText(cmd, "@rem Offline fixture only; never executed.\r\n", Encoding.ASCII);
            File.WriteAllText(bat, "@rem Offline fixture only; never executed.\r\n", Encoding.ASCII);
            File.WriteAllText(ps1, "# Offline fixture only; never executed.\r\n", Encoding.ASCII);

            Assembly app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            appType = app.GetType("TextSender", true); shortcutType = app.GetType("ScriptShortcut", true);
            Type settingsType = app.GetType("SenderSettings", true);
            object defaults = settingsType.GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            Check(((IList)Property(defaults, "ScriptShortcuts")).Count == 0, "default script shortcuts must be empty");
            Check((string)Property(defaults, "Ns2ProjectPath") == "" && ((IList)Property(defaults, "ProjectDirectories")).Count == 0,
                "public defaults must not include project paths");
            SetProperty(defaults, "StartDelayMs", 1700); SetProperty(defaults, "KeyGapMs", 41); SetProperty(defaults, "LineGapMs", 230);
            SetProperty(defaults, "ClearIndent", true); SetProperty(defaults, "KeepOnTop", false); SetProperty(defaults, "PresetAutoEnter", true);
            SetProperty(defaults, "Ns2ProjectPath", project); SetProperty(defaults, "ProjectDirectories", new List<string> { project });
            object firstPreset = ((IList)Property(defaults, "Presets"))[0];
            SetProperty(firstPreset, "Name", "Existing phrase & <fixture>");
            SetProperty(firstPreset, "Text", "  REM Existing\r\n  NEXT"); SetProperty(firstPreset, "AppendEnter", true);
            StaticCall("WriteSettingsFile", configuration, defaults);

            // Recreate a genuine old XML shape, with legacy FTP but no new list.
            XmlDocument oldSettings = ReadXml(configuration);
            XmlNode listOption = oldSettings.SelectSingleNode("/SenderSettings/ScriptShortcuts");
            Check(listOption != null, "new settings must serialize an explicit empty shortcut list");
            listOption.ParentNode.RemoveChild(listOption);
            XmlNode legacyOption = oldSettings.SelectSingleNode("/SenderSettings/FtpScriptPath");
            if (legacyOption == null) { legacyOption = oldSettings.CreateElement("FtpScriptPath"); oldSettings.DocumentElement.AppendChild(legacyOption); }
            legacyOption.InnerText = cmd;
            WriteXml(oldSettings, configuration);

            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (Form form = CreateForm(configuration)) {
                IList migrated = (IList)Field(form, "scriptShortcuts");
                Check(migrated.Count == 1, "legacy FTP did not migrate to exactly one shortcut");
                AssertShortcut(migrated[0], "FTP", cmd, "", "legacy FTP name/path/default directory changed");
                CheckButtons(form, new[] { "FTP" });
                Check((bool)Call(form, "SaveCurrentSettings"), "saving migrated shortcuts failed");
                NoSerial(form);
            }
            XmlDocument migratedSettings = ReadXml(configuration);
            Check(migratedSettings.SelectNodes("/SenderSettings/ScriptShortcuts/ScriptShortcut").Count == 1, "migration did not persist one shortcut");
            CheckPreserved(oldSettings, migratedSettings);
            using (Form form = CreateForm(configuration)) {
                Check(((IList)Field(form, "scriptShortcuts")).Count == 1, "legacy FTP migrated more than once");
                NoSerial(form);
            }

            object commandShortcut = Shortcut("命令 & 窗口", cmd, "");
            object batchShortcut = Shortcut("批处理", bat, customDirectory);
            object powershellShortcut = Shortcut("PowerShell 脚本", ps1, "");
            using (Form form = CreateForm(configuration)) {
                Call(form, "ApplyScriptShortcuts", List(commandShortcut, batchShortcut, powershellShortcut));
                CheckButtons(form, new[] { "命令 & 窗口", "批处理", "PowerShell 脚本" });
                NoSerial(form);
            }
            CheckPreserved(oldSettings, ReadXml(configuration));
            using (Form form = CreateForm(configuration)) {
                IList restored = (IList)Field(form, "scriptShortcuts");
                Check(restored.Count == 3, "multiple shortcuts did not persist");
                AssertShortcut(restored[0], "命令 & 窗口", cmd, "", "first shortcut did not preserve Unicode and spaces");
                AssertShortcut(restored[1], "批处理", bat, customDirectory, "custom working directory did not persist");
                AssertShortcut(restored[2], "PowerShell 脚本", ps1, "", "PowerShell shortcut did not persist");
                CheckButtons(form, new[] { "命令 & 窗口", "批处理", "PowerShell 脚本" });
                Call(form, "SetBusy", true);
                Check(!((FlowLayoutPanel)Field(form, "scriptButtons")).Enabled, "script buttons remain enabled while sending");
                Call(form, "SetBusy", false);
                Check(((FlowLayoutPanel)Field(form, "scriptButtons")).Enabled, "script buttons not restored after sending");
                NoSerial(form);
            }

            // Editing the manager's draft and cancelling must not mutate the caller's items.
            Type managerType = app.GetType("ScriptShortcutManager", true);
            IList original = List(Shortcut("Original", cmd, ""));
            using (var manager = (Form)Activator.CreateInstance(managerType, new object[] { original })) {
                IList draft = (IList)Property(manager, "Result");
                Check(!Object.ReferenceEquals(original, draft) && !Object.ReferenceEquals(original[0], draft[0]), "manager must deep-copy its draft");
                SetProperty(draft[0], "Name", "Cancelled edit"); SetProperty(draft[0], "ScriptPath", bat); SetProperty(draft[0], "WorkingDirectory", customDirectory);
                draft.Add(Shortcut("Cancelled new item", ps1, ""));
                manager.DialogResult = DialogResult.Cancel;
                Check(original.Count == 1, "cancelled new shortcut escaped the draft");
                AssertShortcut(original[0], "Original", cmd, "", "cancelled edits changed the original shortcut");
            }
            object copy = shortcutType.GetMethod("Copy").Invoke(original[0], null);
            SetProperty(copy, "Name", "Copied edit");
            Check(!Object.ReferenceEquals(copy, original[0]) && (string)Property(original[0], "Name") == "Original", "ScriptShortcut.Copy aliases the source");

            using (Form form = CreateForm(configuration)) {
                Call(form, "ApplyScriptShortcuts", List(Shortcut("Edited name", ps1, customDirectory)));
                CheckButtons(form, new[] { "Edited name" });
                NoSerial(form);
            }
            using (Form form = CreateForm(configuration)) {
                IList edited = (IList)Field(form, "scriptShortcuts");
                Check(edited.Count == 1, "removing other shortcuts did not persist");
                AssertShortcut(edited[0], "Edited name", ps1, customDirectory, "editing name/path/working directory did not persist");
                Call(form, "ApplyScriptShortcuts", List());
                CheckButtons(form, new string[0]);
                NoSerial(form);
            }
            XmlDocument emptySettings = ReadXml(configuration);
            Check(emptySettings.SelectSingleNode("/SenderSettings/ScriptShortcuts") != null &&
                emptySettings.SelectNodes("/SenderSettings/ScriptShortcuts/ScriptShortcut").Count == 0,
                "deleting the last shortcut must persist an explicit empty list");
            // A stale legacy value must not resurrect a deliberately emptied new list.
            XmlNode staleLegacy = emptySettings.SelectSingleNode("/SenderSettings/FtpScriptPath");
            if (staleLegacy == null) { staleLegacy = emptySettings.CreateElement("FtpScriptPath"); emptySettings.DocumentElement.AppendChild(staleLegacy); }
            staleLegacy.InnerText = cmd;
            WriteXml(emptySettings, configuration);
            using (Form form = CreateForm(configuration)) {
                Check(((IList)Field(form, "scriptShortcuts")).Count == 0, "empty new list resurrected legacy FTP");
                CheckButtons(form, new string[0]);
                Check((bool)Call(form, "SaveCurrentSettings"), "saving empty shortcut list failed");
                NoSerial(form);
            }
            using (Form form = CreateForm(configuration)) {
                Check(((IList)Field(form, "scriptShortcuts")).Count == 0, "deleted last shortcut returned after reload");
                NoSerial(form);
            }
            CheckPreserved(oldSettings, ReadXml(configuration));

            // Only inspect ProcessStartInfo; never call LaunchScript or Process.Start.
            var commandStart = (ProcessStartInfo)StaticCall("CreateScriptStartInfo", commandShortcut);
            Check(commandStart.FileName == cmd && commandStart.WorkingDirectory == scriptDirectory, "CMD path/default working directory incorrect");
            Check(commandStart.UseShellExecute && commandStart.Arguments == "" && commandStart.WindowStyle == ProcessWindowStyle.Normal,
                "CMD launch should use shell association without extra arguments");
            var batchStart = (ProcessStartInfo)StaticCall("CreateScriptStartInfo", batchShortcut);
            Check(batchStart.FileName == bat && batchStart.WorkingDirectory == customDirectory && batchStart.UseShellExecute && batchStart.Arguments == "",
                "BAT launch path/custom working directory incorrect");
            var powershellStart = (ProcessStartInfo)StaticCall("CreateScriptStartInfo", powershellShortcut);
            Check(String.Equals(Path.GetFileName(powershellStart.FileName), "powershell.exe", StringComparison.OrdinalIgnoreCase),
                "PowerShell script is not launched through Windows PowerShell");
            Check(powershellStart.Arguments.Contains("-NoProfile") && powershellStart.Arguments.Contains("-File \"" + ps1 + "\"") &&
                powershellStart.Arguments.IndexOf("-Command", StringComparison.OrdinalIgnoreCase) < 0,
                "PowerShell script path must be a quoted File argument, not shell text");
            Check(powershellStart.WorkingDirectory == scriptDirectory && powershellStart.WindowStyle == ProcessWindowStyle.Normal,
                "PowerShell default working directory/window incorrect");
            var powershellCustom = (ProcessStartInfo)StaticCall("CreateScriptStartInfo", Shortcut("PS custom", ps1, customDirectory));
            Check(powershellCustom.WorkingDirectory == customDirectory, "PowerShell custom working directory ignored");
            RejectStart(Shortcut("Missing", Path.Combine(fixture, "missing.cmd"), ""), typeof(FileNotFoundException), "missing.cmd", "missing script must be rejected with its path");
            RejectStart(Shortcut("Missing directory", cmd, Path.Combine(fixture, "missing-directory")), typeof(DirectoryNotFoundException),
                "missing-directory", "missing working directory must be rejected with its path");

            File.WriteAllText(report, "PASS " + passed + " checks: legacy one-time migration, explicit empty-list preservation, multiple shortcut buttons and persistence, edit/delete/reload, manager cancel/deep copy, unrelated settings preservation, CMD/BAT/PS1 launch parameters, Unicode/space paths, missing paths, busy state. No scripts executed; no serial/HID operations.\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\n", Encoding.UTF8);
            return 1;
        } finally { Environment.SetEnvironmentVariable("NS2JB_RUNS", oldEnvironment); }
    }
}
