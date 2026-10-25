using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

class ProjectSwitchUiCheck
{
    static int passed;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object instance, string name) { return instance.GetType().GetField(name, PrivateInstance).GetValue(instance); }
    static object Call(object instance, string name, params object[] args) { return instance.GetType().GetMethod(name, PrivateInstance).Invoke(instance, args); }
    static void Check(bool valid, string message) { if (!valid) throw new Exception(message); passed++; }
    static bool SamePath(string left, string right) {
        return String.Equals((left ?? "").TrimEnd('\\', '/'), (right ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
    static int FindProject(ComboBox projects, string path) {
        for (int index = 0; index < projects.Items.Count; index++)
            if (SamePath(Convert.ToString(projects.Items[index]), path)) return index;
        return -1;
    }
    static XmlDocument ReadXml(string path) { var xml = new XmlDocument(); xml.Load(path); return xml; }
    static string Setting(XmlDocument xml, string name) { return xml.SelectSingleNode("/SenderSettings/" + name).InnerText; }
    static void NoSerial(Form form) {
        Check(Field(form, "connection") == null && Field(form, "active") == null, "project operation unexpectedly opened serial");
    }
    static void EnterPath(TextBox path) {
        var enter = new KeyEventArgs(Keys.Enter);
        typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(path, new object[] { enter });
        Check(enter.Handled && enter.SuppressKeyPress, "path Enter was not consumed");
        Application.DoEvents();
    }
    static Form CreateForm(Type appType, string configuration) {
        var form = (Form)Activator.CreateInstance(appType, PrivateInstance, null, new object[] { new string[0] }, null);
        appType.GetField("settingsPath", PrivateInstance).SetValue(form, configuration);
        Call(form, "LoadSettings");
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show(); Application.DoEvents();
        return form;
    }
    static void WriteOldSettings(string path, string project) {
        using (XmlWriter writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize })) {
            writer.WriteStartElement("SenderSettings");
            writer.WriteElementString("StartDelayMs", "500");
            writer.WriteElementString("KeyGapMs", "41");
            writer.WriteElementString("LineGapMs", "200");
            writer.WriteElementString("ClearIndent", "true");
            writer.WriteElementString("KeepOnTop", "false");
            writer.WriteElementString("Ns2ProjectPath", project);
            writer.WriteStartElement("Presets"); writer.WriteStartElement("PhrasePreset");
            writer.WriteElementString("Name", "保留的常用短语");
            writer.WriteElementString("Text", "REM Preserved\n  NEXT");
            writer.WriteElementString("AppendEnter", "false");
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
    }
    static void CheckPreserved(XmlDocument before, XmlDocument after) {
        foreach (string setting in new[] { "StartDelayMs", "KeyGapMs", "LineGapMs", "ClearIndent", "KeepOnTop" })
            Check(Setting(before, setting) == Setting(after, setting), setting + " changed while switching projects");
        string oldPresets = before.SelectSingleNode("/SenderSettings/Presets").OuterXml;
        string newPresets = after.SelectSingleNode("/SenderSettings/Presets").OuterXml;
        Check(oldPresets == newPresets, "presets changed while switching projects; before=" + oldPresets.Replace("\r", "<CR>").Replace("\n", "<LF>") +
            "; after=" + newPresets.Replace("\r", "<CR>").Replace("\n", "<LF>"));
    }

    [STAThread]
    static int Main(string[] args)
    {
        string report = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(Path.GetDirectoryName(report), "project-switch-" + Guid.NewGuid().ToString("N"));
        string switchRoot = Path.Combine(fixture, "Switch2-项目");
        string launcherRoot = Path.Combine(fixture, "HBLauncher-项目");
        string switchRun = Path.Combine(switchRoot, "runs", "switch2-fixture");
        string launcherRun = Path.Combine(launcherRoot, "runs", "launcher-fixture");
        string offlineRoot = switchRoot + "-temporarily-offline";
        string oldEnvironment = Environment.GetEnvironmentVariable("NS2JB_RUNS");
        try {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);
            Directory.CreateDirectory(switchRun); Directory.CreateDirectory(launcherRun);
            string switchCommand = "V=START(64011,127,0,0,1,11,12,13,14)";
            string launcherCommand = "V=START(64012,127,0,0,1,21,22,23,24)";
            string switchFile = Path.Combine(switchRun, "connect.txt"), launcherFile = Path.Combine(launcherRun, "connect.txt");
            File.WriteAllText(switchFile, switchCommand); File.WriteAllText(launcherFile, launcherCommand);
            string configuration = Path.Combine(fixture, "settings.xml");
            WriteOldSettings(configuration, switchRoot);
            XmlDocument original = ReadXml(configuration);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Type appType = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("TextSender", true);

            using (Form form = CreateForm(appType, configuration)) {
                var path = (TextBox)Field(form, "projectPath");
                var projects = (ComboBox)Field(form, "projects");
                var preview = (TextBox)Field(form, "startPreview");
                var remove = (Button)Field(form, "removeProject");
                Check(projects.DropDownStyle == ComboBoxStyle.DropDownList, "saved-project selector must be DropDownList");
                Check(projects.Items.Count == 1 && FindProject(projects, switchRoot) == 0, "old one-path settings did not migrate");
                Check(SamePath(path.Text, switchRoot) && preview.Text == switchCommand, "migrated project did not load its own token");
                NoSerial(form);

                path.Text = "  \"" + launcherRoot + "\"  ";
                Check(preview.Text == "", "editing project path kept stale token");
                EnterPath(path);
                Check(projects.Items.Count == 2 && FindProject(projects, launcherRoot) >= 0, "second project was not remembered");
                Check(SamePath(path.Text, launcherRoot) && preview.Text == launcherCommand, "path Enter did not fetch launcher token");
                NoSerial(form);

                projects.SelectedIndex = FindProject(projects, switchRoot); Application.DoEvents();
                Check(SamePath(path.Text, switchRoot) && preview.Text == switchCommand, "switch to first project used another project's token");
                projects.SelectedIndex = FindProject(projects, launcherRoot); Application.DoEvents();
                Check(SamePath(path.Text, launcherRoot) && preview.Text == launcherCommand, "switch to second project used another project's token");
                NoSerial(form);

                path.Text = launcherRoot.ToUpperInvariant() + "\\";
                EnterPath(path);
                Check(projects.Items.Count == 2, "case/trailing slash duplicate was added");
                Check(preview.Text == launcherCommand, "normalized duplicate lost active token");
                path.Text = Path.Combine(launcherRoot, "runs");
                EnterPath(path);
                Check(projects.Items.Count == 2 && preview.Text == launcherCommand, "project root and its runs child created duplicate entries or lost token");
                path.Text = launcherRoot;
                EnterPath(path);
                XmlDocument saved = ReadXml(configuration);
                Check(SamePath(Setting(saved, "Ns2ProjectPath"), launcherRoot), "active project was not persisted");
                Check(saved.SelectNodes("/SenderSettings/ProjectDirectories/*").Count == 2, "saved project list count incorrect");
                CheckPreserved(original, saved);
                form.Close();
            }

            using (Form form = CreateForm(appType, configuration)) {
                var path = (TextBox)Field(form, "projectPath");
                var projects = (ComboBox)Field(form, "projects");
                var preview = (TextBox)Field(form, "startPreview");
                var remove = (Button)Field(form, "removeProject");
                Check(projects.Items.Count == 2 && FindProject(projects, switchRoot) >= 0 && FindProject(projects, launcherRoot) >= 0,
                    "remembered project list did not survive reload");
                Check(SamePath(path.Text, launcherRoot) && preview.Text == launcherCommand,
                    "active project and preview did not survive reload");
                Call(form, "SetBusy", true);
                Check(!projects.Enabled && !remove.Enabled && path.ReadOnly, "project controls remain enabled during send");
                Call(form, "SetBusy", false);
                Check(projects.Enabled && remove.Enabled && !path.ReadOnly, "project controls were not restored after send");
                NoSerial(form);

                // Only move this harness's own fixture directory, within its unique work folder.
                string fixturePrefix = Path.GetFullPath(fixture) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(switchRoot).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(offlineRoot).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("fixture move escaped test folder");
                Directory.Move(switchRoot, offlineRoot);
                try {
                    projects.SelectedIndex = FindProject(projects, switchRoot); Application.DoEvents();
                    Check(SamePath(path.Text, switchRoot), "missing selected project silently fell back to another project");
                    Check(preview.Text == "", "missing project retained another project's token");
                    Check(projects.Items.Count == 2, "temporarily missing project disappeared from saved list");
                    NoSerial(form);
                } finally { Directory.Move(offlineRoot, switchRoot); }

                projects.SelectedIndex = FindProject(projects, launcherRoot); Application.DoEvents();
                remove.PerformClick(); Application.DoEvents();
                Check(projects.Items.Count == 1 && FindProject(projects, launcherRoot) < 0, "remove did not delete selected list entry");
                Check(Directory.Exists(launcherRoot) && File.ReadAllText(launcherFile) == launcherCommand,
                    "remove changed launcher directory or file");
                Check(Directory.Exists(switchRoot) && File.ReadAllText(switchFile) == switchCommand,
                    "remove changed first project's directory or file");
                Check(preview.Text != launcherCommand, "removed project left its token in preview");
                XmlDocument saved = ReadXml(configuration);
                Check(saved.SelectNodes("/SenderSettings/ProjectDirectories/*").Count == 1, "removal was not persisted");
                CheckPreserved(original, saved);
                NoSerial(form);
                form.Close();
            }

            using (Form form = CreateForm(appType, configuration)) {
                var projects = (ComboBox)Field(form, "projects");
                Check(projects.Items.Count == 1 && FindProject(projects, launcherRoot) < 0, "removed project returned on reload");
                NoSerial(form); form.Close();
            }
            File.WriteAllText(report, "PASS " + passed + " reflection UI checks: old-settings migration, add, switch, exact per-project tokens, normalization/dedup, persistence/reload, missing-folder preview clearing, busy state, list-only removal, unchanged presets/timings, no serial operations.\r\nFixture: " + fixture + "\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + " checks: " + error + "\r\nFixture: " + fixture, Encoding.UTF8);
            return 1;
        } finally {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", oldEnvironment);
        }
    }
}
