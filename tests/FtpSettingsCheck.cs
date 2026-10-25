using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

class FtpSettingsCheck
{
    static int passed;
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
    static object Property(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    static void SetProperty(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    static object Field(object form, string name) { return form.GetType().GetField(name, PrivateInstance).GetValue(form); }
    static object Call(object form, string name, params object[] args) { return form.GetType().GetMethod(name, PrivateInstance).Invoke(form, args); }
    static object StaticCall(Type type, string name, params object[] args) { return type.GetMethod(name, PrivateStatic).Invoke(null, args); }
    static XmlDocument ReadXml(string path) { var xml = new XmlDocument(); xml.Load(path); return xml; }
    static Form CreateForm(Type type, string settingsPath) {
        var form = (Form)Activator.CreateInstance(type, PrivateInstance, null, new object[] { new string[0] }, null);
        type.GetField("settingsPath", PrivateInstance).SetValue(form, settingsPath);
        Call(form, "LoadSettings");
        return form;
    }
    static void NoSerial(Form form) {
        Check(Field(form, "connection") == null && Field(form, "active") == null, "FTP settings opened a serial port");
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: FtpSettingsCheck.exe TextSender.exe report.txt"); return 2; }
        string report = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(Path.GetDirectoryName(report), "ftp-settings-" + Guid.NewGuid().ToString("N"));
        string oldEnvironment = Environment.GetEnvironmentVariable("NS2JB_RUNS");
        try {
            Environment.SetEnvironmentVariable("NS2JB_RUNS", null);
            Directory.CreateDirectory(fixture);
            string configuration = Path.Combine(fixture, "settings.xml");
            string project = Path.Combine(fixture, "Example project");
            string scriptDirectory = Path.Combine(fixture, "Scripts with spaces 项目");
            Directory.CreateDirectory(project); Directory.CreateDirectory(scriptDirectory);
            string script = Path.Combine(scriptDirectory, "FTP fixture.cmd");
            File.WriteAllText(script, "@rem Offline fixture only; this script must never be executed.\r\n", Encoding.ASCII);

            Assembly app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type appType = app.GetType("TextSender", true), settingsType = app.GetType("SenderSettings", true);
            object defaults = settingsType.GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            Check(String.Equals(Property(defaults, "FtpScriptPath"), ""), "default FTP script must be empty");
            Check(String.Equals(Property(defaults, "Ns2ProjectPath"), ""), "default project path must be empty");
            Check(((IList)Property(defaults, "ProjectDirectories")).Count == 0, "default project history must be empty");

            SetProperty(defaults, "StartDelayMs", 1700);
            SetProperty(defaults, "KeyGapMs", 41);
            SetProperty(defaults, "LineGapMs", 230);
            SetProperty(defaults, "ClearIndent", true);
            SetProperty(defaults, "KeepOnTop", false);
            SetProperty(defaults, "PresetAutoEnter", true);
            SetProperty(defaults, "Ns2ProjectPath", project);
            SetProperty(defaults, "ProjectDirectories", new List<string> { project });
            object firstPreset = ((IList)Property(defaults, "Presets"))[0];
            SetProperty(firstPreset, "Name", "Existing phrase & <fixture>");
            SetProperty(firstPreset, "Text", "  REM Existing\r\n  NEXT");
            SetProperty(firstPreset, "AppendEnter", true);
            StaticCall(appType, "WriteSettingsFile", configuration, defaults);

            // Recreate an older settings file that predates configurable FTP.
            XmlDocument oldSettings = ReadXml(configuration);
            XmlNode ftpOption = oldSettings.SelectSingleNode("/SenderSettings/FtpScriptPath");
            Check(ftpOption != null, "new settings omit FTP option");
            ftpOption.ParentNode.RemoveChild(ftpOption);
            // Preserve literal CR/LF when removing the option; XmlDocument.Save(path)
            // otherwise rewrites them as raw XML newlines, normalized on reload.
            using (XmlWriter writer = XmlWriter.Create(configuration, new XmlWriterSettings {
                Encoding = new UTF8Encoding(false), Indent = true, NewLineHandling = NewLineHandling.Entitize
            })) oldSettings.Save(writer);
            object migrated = StaticCall(appType, "ReadSettingsFile", configuration);
            Check(String.Equals(Property(migrated, "FtpScriptPath"), ""), "old settings migration must leave FTP empty");

            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (Form form = CreateForm(appType, configuration)) {
                Check(String.Equals(Field(form, "ftpScriptPath"), ""), "loading old settings restored a hardcoded FTP path");
                NoSerial(form);
                appType.GetField("ftpScriptPath", PrivateInstance).SetValue(form, script);
                Check((bool)Call(form, "SaveCurrentSettings"), "saving selected FTP script failed");
                NoSerial(form);
            }
            XmlDocument saved = ReadXml(configuration);
            Check(saved.SelectSingleNode("/SenderSettings/FtpScriptPath").InnerText == script, "selected FTP path not saved exactly");
            foreach (string name in new[] { "StartDelayMs", "KeyGapMs", "LineGapMs", "ClearIndent", "KeepOnTop", "PresetAutoEnter", "Ns2ProjectPath", "ProjectDirectories", "Presets" }) {
                Check(oldSettings.SelectSingleNode("/SenderSettings/" + name).OuterXml == saved.SelectSingleNode("/SenderSettings/" + name).OuterXml,
                    "selecting FTP changed " + name);
            }
            using (Form form = CreateForm(appType, configuration)) {
                Check(String.Equals(Field(form, "ftpScriptPath"), script), "selected FTP script did not survive reload");
                NoSerial(form);
            }

            // Only inspect ProcessStartInfo; do not call LaunchFtp or Process.Start.
            var start = (ProcessStartInfo)StaticCall(appType, "CreateFtpStartInfo", script);
            Check(start.FileName == Path.GetFullPath(script), "script path not preserved exactly");
            Check(start.WorkingDirectory == Path.GetFullPath(scriptDirectory), "script working directory incorrect");
            Check(start.UseShellExecute, "script must use Windows shell association");
            Check(start.Arguments == "", "script should receive no extra arguments");
            Check(start.WindowStyle == ProcessWindowStyle.Normal, "script window must remain visible");
            bool missingRejected = false;
            try { StaticCall(appType, "CreateFtpStartInfo", Path.Combine(fixture, "missing.cmd")); }
            catch (TargetInvocationException error) { missingRejected = error.InnerException is FileNotFoundException; }
            Check(missingRejected, "missing FTP script was not rejected before launch");

            File.WriteAllText(report, "PASS " + passed + " checks: empty public defaults, old-settings migration, FTP path persistence/reload, other settings preserved, exact script launch parameters, missing-file rejection. No scripts executed; no serial/HID operations.\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception error) {
            File.WriteAllText(report, "FAIL after " + passed + ": " + error + "\r\n", Encoding.UTF8);
            return 1;
        } finally { Environment.SetEnvironmentVariable("NS2JB_RUNS", oldEnvironment); }
    }
}
