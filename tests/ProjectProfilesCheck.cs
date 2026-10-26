using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

class ProjectProfilesCheck
{
    static int passed;
    static Type appType, shortcutType;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static object Field(object instance, string name) { return instance.GetType().GetField(name, PrivateInstance).GetValue(instance); }
    static object Property(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    static void SetProperty(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    static object Call(object instance, string name, params object[] args) { return instance.GetType().GetMethod(name, PrivateInstance).Invoke(instance, args); }
    static object ReadSettings(string path) { return appType.GetMethod("ReadSettingsFile", PrivateStatic).Invoke(null, new object[] { path }); }
    static bool SamePath(string a, string b) { return String.Equals((a ?? "").TrimEnd('\\', '/'), (b ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
    static XmlDocument ReadXml(string path) { var xml = new XmlDocument(); xml.Load(path); return xml; }
    static void WriteXml(XmlDocument xml, string path) {
        using (XmlWriter writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineHandling = NewLineHandling.Entitize })) xml.Save(writer);
    }
    static XmlNode Add(XmlDocument xml, XmlNode parent, string name, string value) {
        XmlNode node = xml.CreateElement(name); if (value != null) node.InnerText = value; parent.AppendChild(node); return node;
    }
    static void WriteLegacy(string path, string[] directories, string active, string script) {
        var xml = new XmlDocument(); XmlNode root = xml.CreateElement("SenderSettings"); xml.AppendChild(root);
        Add(xml, root, "StartDelayMs", "700"); Add(xml, root, "KeyGapMs", "42"); Add(xml, root, "LineGapMs", "210");
        Add(xml, root, "ClearIndent", "true"); Add(xml, root, "KeepOnTop", "false"); Add(xml, root, "PresetAutoEnter", "true");
        Add(xml, root, "Ns2ProjectPath", active);
        XmlNode oldDirectories = Add(xml, root, "ProjectDirectories", null);
        foreach (string directory in directories) Add(xml, oldDirectories, "string", directory);
        XmlNode scripts = Add(xml, root, "ScriptShortcuts", null);
        if (script != null) {
            XmlNode item = Add(xml, scripts, "ScriptShortcut", null);
            Add(xml, item, "Name", "Legacy script"); Add(xml, item, "ScriptPath", script); Add(xml, item, "WorkingDirectory", "");
        }
        XmlNode presets = Add(xml, root, "Presets", null), preset = Add(xml, presets, "PhrasePreset", null);
        Add(xml, preset, "Name", "Keep & <fixture>"); Add(xml, preset, "Text", "  REM Keep\r\n  NEXT"); Add(xml, preset, "AppendEnter", "false");
        WriteXml(xml, path);
    }
    static object Shortcut(string name, string path) {
        object item = Activator.CreateInstance(shortcutType);
        SetProperty(item, "Name", name); SetProperty(item, "ScriptPath", path); SetProperty(item, "WorkingDirectory", ""); return item;
    }
    static IList Scripts(params object[] items) {
        IList result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(shortcutType));
        foreach (object item in items) result.Add(item); return result;
    }
    static object Profile(IList profiles, string path) {
        foreach (object profile in profiles) if (SamePath((string)Property(profile, "DirectoryPath"), path)) return profile;
        throw new Exception("expected profile not found");
    }
    static IList FormProfiles(Form form) { return (IList)Field(form, "projectProfiles"); }
    static Form CreateForm(string configuration) {
        var form = (Form)Activator.CreateInstance(appType, PrivateInstance, null, new object[] { new string[0] }, null);
        appType.GetField("settingsPath", PrivateInstance).SetValue(form, configuration); Call(form, "LoadSettings"); return form;
    }
    static void Select(Form form, string path) {
        var combo = (ComboBox)Field(form, "projects");
        for (int index = 0; index < combo.Items.Count; index++) {
            if (SamePath((string)combo.Items[index], path)) { combo.SelectedIndex = index; return; }
        }
        throw new Exception("project cannot be selected");
    }
    static void Enter(Control control) {
        var keys = new KeyEventArgs(Keys.Enter);
        typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(control, new object[] { keys });
        Check(keys.Handled && keys.SuppressKeyPress, "Enter was not consumed by project control");
    }
    static void SetPath(Form form, string path) { var box = (TextBox)Field(form, "projectPath"); box.Text = path; Enter(box); }
    static void Rename(Form form, string name, bool enter) {
        var box = (TextBox)Field(form, "projectName"); box.Text = name;
        if (enter) Enter(box); else typeof(Control).GetMethod("OnLeave", PrivateInstance).Invoke(box, new object[] { EventArgs.Empty });
    }
    static void Names(Form form, params string[] expected) {
        IList scripts = (IList)Field(form, "scriptShortcuts");
        Check(scripts.Count == expected.Length, "active project's script count is incorrect");
        var buttons = new List<Button>();
        foreach (Control control in ((FlowLayoutPanel)Field(form, "scriptButtons")).Controls) { var button = control as Button; if (button != null) buttons.Add(button); }
        Check(buttons.Count == expected.Length, "active project's script button count is incorrect");
        for (int index = 0; index < expected.Length; index++)
            Check((string)Property(scripts[index], "Name") == expected[index] && buttons[index].Text == expected[index], "script list and button names differ");
        Check(Field(form, "connection") == null && Field(form, "active") == null, "project/profile action opened serial");
    }
    static void Preserved(XmlDocument before, XmlDocument after) {
        foreach (string option in new[] { "StartDelayMs", "KeyGapMs", "LineGapMs", "ClearIndent", "KeepOnTop", "PresetAutoEnter", "Presets" })
            Check(before.SelectSingleNode("/SenderSettings/" + option).OuterXml == after.SelectSingleNode("/SenderSettings/" + option).OuterXml,
                "profile operations modified unrelated setting " + option);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: ProjectProfilesCheck.exe TextSender.exe report.txt"); return 2; }
        string report = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(Path.GetDirectoryName(report), "project-profiles-" + Guid.NewGuid().ToString("N"));
        string oldEnvironment = Environment.GetEnvironmentVariable("NS2JB_RUNS");
        try {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);
            Directory.CreateDirectory(fixture);
            string a = Path.Combine(fixture, "Project A 项目"), b = Path.Combine(fixture, "Project B 项目"), c = Path.Combine(fixture, "Current only C"), d = Path.Combine(fixture, "New project D");
            foreach (string directory in new[] { a, b, c, d }) Directory.CreateDirectory(Path.Combine(directory, "runs"));
            string cmd = Path.Combine(fixture, "No execution.cmd"), ps1 = Path.Combine(fixture, "No execution.ps1");
            File.WriteAllText(cmd, "@rem Offline fixture only.\r\n", Encoding.ASCII); File.WriteAllText(ps1, "# Offline fixture only.\r\n", Encoding.ASCII);
            string configuration = Path.Combine(fixture, "settings.xml");
            WriteLegacy(configuration, new[] { a, b }, c, cmd);
            XmlDocument original = ReadXml(configuration);
            Assembly app = Assembly.LoadFrom(Path.GetFullPath(args[0])); appType = app.GetType("TextSender", true); shortcutType = app.GetType("ScriptShortcut", true);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);

            object migrated = ReadSettings(configuration);
            IList profiles = (IList)Property(migrated, "Projects");
            Check(profiles.Count == 3, "migration lost a saved project or the separately selected current project");
            object pa = Profile(profiles, a), pb = Profile(profiles, b), pc = Profile(profiles, c);
            IList sa = (IList)Property(pa, "ScriptShortcuts"), sb = (IList)Property(pb, "ScriptShortcuts"), sc = (IList)Property(pc, "ScriptShortcuts");
            Check(sa.Count == 1 && sb.Count == 1 && sc.Count == 1, "legacy global scripts were not copied to every migrated project");
            Check(!Object.ReferenceEquals(sa, sb) && !Object.ReferenceEquals(sa[0], sb[0]) && !Object.ReferenceEquals(sa[0], sc[0]), "migration shares script instances between projects");
            SetProperty(sa[0], "Name", "A copy only");
            Check((string)Property(sb[0], "Name") == "Legacy script" && (string)Property(sc[0], "Name") == "Legacy script", "editing migrated project A altered other projects");
            Check(((IList)Property(migrated, "ScriptShortcuts")).Count == 0, "migration left duplicated unassigned global scripts");
            object copied = pa.GetType().GetMethod("Copy").Invoke(pa, null);
            IList copiedScripts = (IList)Property(copied, "ScriptShortcuts"); SetProperty(copiedScripts[0], "Name", "Copy only");
            Check(!Object.ReferenceEquals(copied, pa) && !Object.ReferenceEquals(copiedScripts, sa) && (string)Property(sa[0], "Name") == "A copy only", "ProjectProfile.Copy does not deep-copy script items");

            const string alias = "同名项目 & Demo";
            using (Form form = CreateForm(configuration)) {
                Select(form, a); Names(form, "Legacy script"); Rename(form, alias, true);
                Check((string)Property(Profile(FormProfiles(form), a), "Name") == alias, "project alias Enter did not save");
                Call(form, "ApplyScriptShortcuts", Scripts(Shortcut("A first", cmd), Shortcut("A second", ps1)));
                Select(form, b); Names(form, "Legacy script"); Rename(form, alias, false);
                Check((string)Property(Profile(FormProfiles(form), b), "Name") == alias, "project alias Leave did not save");
                Call(form, "ApplyScriptShortcuts", Scripts(Shortcut("B only", ps1)));
                Names(form, "B only");
                var combo = (ComboBox)Field(form, "projects");
                foreach (object item in combo.Items) {
                    string path = (string)item;
                    if (SamePath(path, a) || SamePath(path, b)) Check(combo.GetItemText(item).Contains(alias) && combo.GetItemText(item).Contains(path), "same-name selector does not show alias and path");
                }
                Select(form, a); Names(form, "A first", "A second");
                SetPath(form, Path.Combine(a, "runs")); Names(form, "A first", "A second");
                Check(FormProfiles(form).Count == 3 && ((TextBox)Field(form, "projectName")).Text == alias, "runs alias created another profile or lost its name");
                SetPath(form, a.ToUpperInvariant() + "\\"); Names(form, "A first", "A second");
                Check(FormProfiles(form).Count == 3, "case/trailing separator created a duplicate profile");
                Call(form, "ApplyScriptShortcuts", Scripts()); Names(form);
                Select(form, b); Names(form, "B only");
                Select(form, a); Names(form);
                Check((bool)Call(form, "SaveCurrentSettings"), "saving profiles failed");
            }
            XmlDocument saved = ReadXml(configuration);
            Check(saved.SelectNodes("/SenderSettings/Projects/ProjectProfile").Count == 3 && saved.SelectSingleNode("/SenderSettings/ProjectDirectories") == null, "new format did not replace legacy project directory list");
            Preserved(original, saved);
            using (Form form = CreateForm(configuration)) {
                Check((string)Property(Profile(FormProfiles(form), a), "Name") == alias && (string)Property(Profile(FormProfiles(form), b), "Name") == alias, "same-name project aliases did not survive reload");
                Select(form, a); Names(form);
                Select(form, b); Names(form, "B only");
                Call(form, "RemoveProject");
                Check(FormProfiles(form).Count == 2 && Directory.Exists(b), "removing a profile changed its directory or kept the profile");
                foreach (object shortcut in (IList)Field(form, "scriptShortcuts")) Check((string)Property(shortcut, "Name") != "B only", "removing current project retained its script");
                Select(form, c); Names(form, "Legacy script");
                var path = (TextBox)Field(form, "projectPath"); path.Text = Path.Combine(fixture, "Unknown missing project");
                Names(form); // Editing the path must immediately discard old project buttons.
                Enter(path); Names(form);
                Check(FormProfiles(form).Count == 2, "unknown nonexistent path became a remembered project");
                SetPath(form, d); Names(form);
                Check(FormProfiles(form).Count == 3, "new existing project was not added");
                Select(form, c); Names(form, "Legacy script");
                Select(form, a); Names(form);
                Check((bool)Call(form, "SaveCurrentSettings"), "saving after deletion failed");
            }
            using (Form form = CreateForm(configuration)) {
                bool hasB = false; foreach (object profile in FormProfiles(form)) if (SamePath((string)Property(profile, "DirectoryPath"), b)) hasB = true;
                Check(!hasB, "deleted project revived after reload"); Select(form, a); Names(form); Select(form, d); Names(form);
            }
            Preserved(original, ReadXml(configuration));

            string pendingConfiguration = Path.Combine(fixture, "pending.xml");
            WriteLegacy(pendingConfiguration, new string[0], "", cmd);
            object pending = ReadSettings(pendingConfiguration);
            Check(((IList)Property(pending, "Projects")).Count == 0 && ((IList)Property(pending, "ScriptShortcuts")).Count == 1, "old no-project scripts were lost instead of kept pending");
            using (Form form = CreateForm(pendingConfiguration)) {
                SetPath(form, a); Names(form, "Legacy script"); SetPath(form, b); Names(form); Select(form, a); Names(form, "Legacy script");
                Check((bool)Call(form, "SaveCurrentSettings"), "saving imported pending scripts failed");
            }
            Check(ReadXml(pendingConfiguration).SelectNodes("/SenderSettings/ScriptShortcuts/ScriptShortcut").Count == 0, "pending scripts were not consumed exactly once");
            using (Form form = CreateForm(pendingConfiguration)) { Select(form, a); Names(form, "Legacy script"); Select(form, b); Names(form); }

            string explicitEmpty = Path.Combine(fixture, "explicit-empty.xml");
            WriteLegacy(explicitEmpty, new[] { a, b }, a, null);
            XmlDocument empty = ReadXml(explicitEmpty); Add(empty, empty.DocumentElement, "Projects", null); Add(empty, empty.DocumentElement, "FtpScriptPath", cmd); WriteXml(empty, explicitEmpty);
            object emptySettings = ReadSettings(explicitEmpty);
            Check(((IList)Property(emptySettings, "Projects")).Count == 0 && ((IList)Property(emptySettings, "ScriptShortcuts")).Count == 0, "explicit empty new projects resurrected legacy values");
            using (Form form = CreateForm(explicitEmpty)) {
                Check(FormProfiles(form).Count == 0 && ((ComboBox)Field(form, "projects")).Items.Count == 0, "loading explicit empty projects revived old selection/history"); Names(form);
            }

            string explicitScripts = Path.Combine(fixture, "explicit-empty-scripts.xml");
            WriteLegacy(explicitScripts, new[] { a }, a, cmd);
            XmlDocument one = ReadXml(explicitScripts); XmlNode projectList = Add(one, one.DocumentElement, "Projects", null), profileNode = Add(one, projectList, "ProjectProfile", null);
            Add(one, profileNode, "Name", "Intentionally blank"); Add(one, profileNode, "DirectoryPath", a); Add(one, profileNode, "ScriptShortcuts", null); WriteXml(one, explicitScripts);
            using (Form form = CreateForm(explicitScripts)) { Select(form, a); Names(form); Check((string)Property(Profile(FormProfiles(form), a), "Name") == "Intentionally blank", "explicit project name changed during migration"); }

            // The environment is a first-run hint, not a way to revive removed projects.
            string emptyWithEnvironment = Path.Combine(fixture, "empty-with-environment.xml");
            WriteLegacy(emptyWithEnvironment, new string[0], "", null);
            XmlDocument emptyEnvironmentXml = ReadXml(emptyWithEnvironment);
            Add(emptyEnvironmentXml, emptyEnvironmentXml.DocumentElement, "Projects", null);
            WriteXml(emptyEnvironmentXml, emptyWithEnvironment);
            Environment.SetEnvironmentVariable("NS2JB_RUNS", a);
            using (Form form = CreateForm(emptyWithEnvironment)) {
                Check(FormProfiles(form).Count == 0 && ((ComboBox)Field(form, "projects")).Items.Count == 0,
                    "environment revived an explicitly empty saved project list");
                Check(((TextBox)Field(form, "projectPath")).Text == "", "environment replaced an explicitly empty saved project path");
                Names(form);
            }
            string firstRun = Path.Combine(fixture, "first-run-environment.xml");
            Check(!File.Exists(firstRun), "first-run environment fixture unexpectedly already exists");
            using (Form form = CreateForm(firstRun)) {
                Check(FormProfiles(form).Count == 1 && SamePath((string)Property(FormProfiles(form)[0], "DirectoryPath"), a),
                    "first run with a valid environment path did not add its project");
                Check(SamePath(((TextBox)Field(form, "projectPath")).Text, a), "first run did not select the environment project");
                Names(form);
                Call(form, "RemoveProject");
                Check(FormProfiles(form).Count == 0 && ((TextBox)Field(form, "projectPath")).Text == "",
                    "removing the environment project did not clear the list and active path");
                Names(form);
                Check((bool)Call(form, "SaveCurrentSettings"), "saving removal of the environment project failed");
            }
            XmlDocument removedEnvironment = ReadXml(firstRun);
            Check(removedEnvironment.SelectSingleNode("/SenderSettings/Projects") != null &&
                removedEnvironment.SelectNodes("/SenderSettings/Projects/ProjectProfile").Count == 0 &&
                removedEnvironment.SelectSingleNode("/SenderSettings/Ns2ProjectPath").InnerText == "",
                "environment project removal did not persist explicit empty values");
            using (Form form = CreateForm(firstRun)) {
                Check(FormProfiles(form).Count == 0 && ((ComboBox)Field(form, "projects")).Items.Count == 0 &&
                    ((TextBox)Field(form, "projectPath")).Text == "", "environment revived the removed last project on reload");
                Names(form);
            }
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);

            File.WriteAllText(report, "PASS " + passed + " checks: genuine old XML migration and deep copies, alias Enter/Leave and reload, same names with different paths, root/runs normalization, per-project script isolation/edit/delete, empty-list persistence, unknown-path clearing, profile deletion, pending-script import once, explicit empty configuration protection, first-run environment import without reviving removed projects, unrelated settings preservation. No scripts executed; no serial/HID operations.\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\n", Encoding.UTF8); return 1;
        } finally { Environment.SetEnvironmentVariable("NS2JB_RUNS", oldEnvironment); }
    }
}
