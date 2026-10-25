using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

class PresetAutoEnterCheck
{
    static int passed;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
    static object Field(object form, string name) { return form.GetType().GetField(name, PrivateInstance).GetValue(form); }
    static void Call(object form, string name, params object[] args) { form.GetType().GetMethod(name, PrivateInstance).Invoke(form, args); }
    static Form CreateForm(Type type, string settingsPath) {
        var form = (Form)Activator.CreateInstance(type, PrivateInstance, null, new object[] { new string[0] }, null);
        type.GetField("settingsPath", PrivateInstance).SetValue(form, settingsPath);
        Call(form, "LoadSettings");
        return form;
    }
    static string Render(Type appType, Type presetType, string text, bool localEnter, bool globalEnter) {
        object preset = Activator.CreateInstance(presetType);
        presetType.GetProperty("Name").SetValue(preset, "Fixture", null);
        presetType.GetProperty("Text").SetValue(preset, text, null);
        presetType.GetProperty("AppendEnter").SetValue(preset, localEnter, null);
        return (string)appType.GetMethod("PresetText", BindingFlags.Public | BindingFlags.Static, null,
            new Type[] { presetType, typeof(bool) }, null).Invoke(null, new object[] { preset, globalEnter });
    }
    static XmlDocument ReadXml(string path) { var xml = new XmlDocument(); xml.Load(path); return xml; }

    [STAThread]
    static int Main(string[] args)
    {
        string report = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(Path.GetDirectoryName(report), "preset-auto-enter-" + Guid.NewGuid().ToString("N"));
        string oldEnvironment = Environment.GetEnvironmentVariable("NS2JB_RUNS");
        try {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);
            Directory.CreateDirectory(fixture);
            string settingsPath = Path.Combine(fixture, "settings.xml");
            Assembly app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type appType = app.GetType("TextSender", true), presetType = app.GetType("PhrasePreset", true);
            Type settingsType = app.GetType("SenderSettings", true);
            object defaults = settingsType.GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            Check(!(bool)settingsType.GetProperty("PresetAutoEnter").GetValue(defaults, null), "global auto-Enter default must be off");
            using (var writer = XmlWriter.Create(settingsPath, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                new XmlSerializer(settingsType).Serialize(writer, defaults);
            // Exercise migration from an older settings file that has no global option.
            XmlDocument oldSettings = ReadXml(settingsPath);
            XmlNode option = oldSettings.SelectSingleNode("/SenderSettings/PresetAutoEnter");
            if (option != null) option.ParentNode.RemoveChild(option);
            oldSettings.Save(settingsPath);

            Check(Render(appType, presetType, "  A", false, false) == "  A", "global off changed literal text");
            Check(Render(appType, presetType, "  A", false, true) == "  A\n", "global on did not append Enter");
            Check(Render(appType, presetType, "A", true, false) == "A\n", "local Enter option changed");
            Check(Render(appType, presetType, "A", true, true) == "A\n", "both options appended Enter twice");
            Check(Render(appType, presetType, "A\n", false, true) == "A\n", "global option doubled existing LF");
            Check(Render(appType, presetType, "A\r\n", false, true) == "A\r\n", "global option doubled existing CRLF");
            Check(Render(appType, presetType, "A\r", false, true) == "A\r", "global option doubled existing CR");
            Check(Render(appType, presetType, "A\n", true, true) == "A\n\n", "explicit local append behavior changed");
            string oversized = Render(appType, presetType, new string('A', 8192), false, true);
            Check(oversized.Length == 8193, "global append byte count is incorrect");
            bool rejected = false;
            try { appType.GetMethod("Encode", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { oversized }); }
            catch (TargetInvocationException) { rejected = true; }
            Check(rejected, "global auto-Enter overflow not rejected before typing");

            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (Form form = CreateForm(appType, settingsPath)) {
                var checkbox = (CheckBox)Field(form, "presetAutoEnter");
                Check(!checkbox.Checked, "old settings migration enabled global auto-Enter");
                checkbox.Checked = true;
                Check(ReadXml(settingsPath).SelectSingleNode("/SenderSettings/PresetAutoEnter").InnerText == "true", "checkbox change did not save");
                Call(form, "SetBusy", true);
                Check(!checkbox.Enabled, "global auto-Enter checkbox enabled while sending");
                Call(form, "SetBusy", false);
                Check(checkbox.Enabled, "global auto-Enter checkbox not restored after sending");
                Check(Field(form, "connection") == null && Field(form, "active") == null, "checkbox UI opened a serial port");
            }
            using (Form form = CreateForm(appType, settingsPath)) {
                var checkbox = (CheckBox)Field(form, "presetAutoEnter");
                Check(checkbox.Checked, "global auto-Enter did not survive reload");
                checkbox.Checked = false;
                Check(ReadXml(settingsPath).SelectSingleNode("/SenderSettings/PresetAutoEnter").InnerText == "false", "turning option off did not save");
                Check(Field(form, "connection") == null && Field(form, "active") == null, "reloading option opened a serial port");
            }
            File.WriteAllText(report, "PASS " + passed + " checks: default off, old-settings migration, global/local Enter combinations, existing CR/LF preservation, overflow guard, checkbox persistence/reload, busy state, no serial operations.\r\nFixture: " + fixture + "\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\nFixture: " + fixture, Encoding.UTF8);
            return 1;
        } finally { Environment.SetEnvironmentVariable("NS2JB_RUNS", oldEnvironment); }
    }
}
