using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;

class SingleInstanceCheck
{
    static int passed;
    static readonly List<Process> ownedProcesses = new List<Process>();
    static readonly object processLock = new object();
    static int cleanupFailures;
    const string EditorFixture = "REM retained editor text\r\nPRINT 123";

    delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder value, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, StringBuilder text);

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passed++;
    }

    static bool Alive(Process process)
    {
        process.Refresh();
        return !process.HasExited;
    }

    static bool WaitUntil(Func<bool> condition, int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        do {
            if (condition()) return true;
            Thread.Sleep(25);
        } while (elapsed.ElapsedMilliseconds < milliseconds);
        return condition();
    }

    static Process Start(string executable, string arguments)
    {
        var info = new ProcessStartInfo(executable, arguments);
        info.WorkingDirectory = Path.GetDirectoryName(executable);
        info.UseShellExecute = false;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        // Prevent a real project configured in the parent environment entering
        // the disposable fixture. All files and processes belong to this test.
        info.EnvironmentVariables.Remove("NS2JB_RUNS");
        Process process = Process.Start(info);
        lock (processLock) ownedProcesses.Add(process);
        return process;
    }

    static IntPtr WaitForWindow(Process process, string context)
    {
        bool ready = WaitUntil(delegate {
            return !Alive(process) || process.MainWindowHandle != IntPtr.Zero;
        }, 12000);
        Check(ready && Alive(process) && process.MainWindowHandle != IntPtr.Zero,
            context + ": no owned main window; close any separately running instance before testing");
        IntPtr window = process.MainWindowHandle;
        Check(IsWindow(window), context + ": main window is invalid");
        return window;
    }

    static void SameWindow(Process process, IntPtr window, string context)
    {
        Check(Alive(process), context + ": original process exited");
        Check(process.MainWindowHandle == window && IsWindow(window), context + ": original main window changed");
    }

    static IntPtr FillEditor(IntPtr window)
    {
        var editors = new List<IntPtr>();
        EnumChildWindows(window, delegate(IntPtr child, IntPtr ignored) {
            var className = new StringBuilder(256);
            GetClassName(child, className, className.Capacity);
            if (className.ToString().StartsWith("WindowsForms10.EDIT", StringComparison.Ordinal)
                && (GetWindowLong(child, -16) & 4) != 0) editors.Add(child); // ES_MULTILINE
            return true;
        }, IntPtr.Zero);
        Check(editors.Count == 1, "expected exactly one multiline text editor on the main form");
        IntPtr editor = editors[0];
        Check(SendMessage(editor, 0x000C, IntPtr.Zero, EditorFixture) != IntPtr.Zero,
            "could not set isolated editor fixture"); // WM_SETTEXT, no Send button action
        EditorUnchanged(editor, "editor fixture");
        return editor;
    }

    static void EditorUnchanged(IntPtr editor, string context)
    {
        var value = new StringBuilder(EditorFixture.Length + 16);
        SendMessage(editor, 0x000D, new IntPtr(value.Capacity), value); // WM_GETTEXT
        Check(value.ToString() == EditorFixture, context + ": editor contents changed");
    }

    static void DuplicateExited(Process process, string context)
    {
        Check(process.WaitForExit(12000), context + ": duplicate process stayed running");
        Check(process.ExitCode == 0, context + ": duplicate exit code was " + process.ExitCode);
    }

    static void CloseOwned(Process process, string context)
    {
        Check(Alive(process), context + ": process already exited");
        IntPtr window = process.MainWindowHandle;
        Check(window != IntPtr.Zero && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero),
            context + ": could not request normal window close");
        Check(process.WaitForExit(12000), context + ": normal close did not exit");
        Check(process.ExitCode == 0, context + ": normal close returned an error");
    }

    static string FindExample(string executable)
    {
        string directory = Path.GetDirectoryName(executable);
        string beside = Path.Combine(directory, "settings.example.xml");
        if (File.Exists(beside)) return beside;
        DirectoryInfo parent = Directory.GetParent(directory);
        if (parent != null) {
            string candidate = Path.Combine(parent.FullName, "settings.example.xml");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Place settings.example.xml beside the application or in its parent directory.");
    }

    static string CopyApplication(string source, string example, string directory, string name, bool savedSettings)
    {
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, name);
        File.Copy(source, executable);
        File.Copy(example, Path.Combine(directory, "settings.example.xml"));
        if (savedSettings) {
            var xml = new XmlDocument();
            xml.Load(example);
            // The public example must have no projects or scripts. Retain its
            // other defaults, but avoid a topmost test window covering work.
            Check(xml.SelectNodes("/SenderSettings/Projects/*").Count == 0,
                "fixture example unexpectedly contains saved projects");
            Check(xml.SelectNodes("/SenderSettings/ScriptShortcuts/*").Count == 0,
                "fixture example unexpectedly contains scripts");
            XmlNode active = xml.SelectSingleNode("/SenderSettings/Ns2ProjectPath");
            Check(active != null && String.IsNullOrWhiteSpace(active.InnerText),
                "fixture example unexpectedly contains an active project");
            XmlNode onTop = xml.SelectSingleNode("/SenderSettings/KeepOnTop");
            if (onTop != null) onTop.InnerText = "false";
            xml.Save(Path.Combine(directory, "settings.xml"));
        }
        return executable;
    }

    static void Cleanup()
    {
        Process[] processes;
        lock (processLock) processes = ownedProcesses.ToArray();
        foreach (Process process in processes) {
            try {
                if (Alive(process)) {
                    IntPtr window = process.MainWindowHandle;
                    if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (!process.WaitForExit(2000)) {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
            } catch (InvalidOperationException) { }
            catch (Exception) { cleanupFailures++; }
            finally { process.Dispose(); }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string report = Path.GetFullPath(args[1]);
        string result;
        int exitCode;
        try {
            string source = Path.GetFullPath(args[0]);
            Check(File.Exists(source), "application EXE is missing");
            string example = FindExample(source);
            string fixture = Path.Combine(Path.GetDirectoryName(report), "single-instance-" + Guid.NewGuid().ToString("N"));
            string firstExe = CopyApplication(source, example, Path.Combine(fixture, "first"), "TextSender.exe", true);
            string renamedExe = CopyApplication(source, example, Path.Combine(fixture, "other directory"), "RenamedSender.exe", false);
            string secondSettings = Path.Combine(Path.GetDirectoryName(renamedExe), "settings.xml");

            Process first = Start(firstExe, "");
            IntPtr firstWindow = WaitForWindow(first, "initial start");
            IntPtr editor = FillEditor(firstWindow);
            DuplicateExited(Start(firstExe, ""), "same-path second start");
            SameWindow(first, firstWindow, "same-path second start");
            EditorUnchanged(editor, "same-path second start");

            ShowWindow(firstWindow, 6); // SW_MINIMIZE
            Check(WaitUntil(delegate { return IsIconic(firstWindow); }, 3000), "main window did not minimize");
            DuplicateExited(Start(firstExe, ""), "minimized second start");
            Check(WaitUntil(delegate { return !IsIconic(firstWindow); }, 5000), "second start did not restore minimized window");
            SameWindow(first, firstWindow, "minimized second start");
            EditorUnchanged(editor, "minimized second start");

            DuplicateExited(Start(renamedExe, ""), "renamed copy in another directory");
            SameWindow(first, firstWindow, "renamed copy in another directory");
            EditorUnchanged(editor, "renamed copy in another directory");
            Check(!File.Exists(secondSettings), "duplicate created settings.xml in the second directory");

            string offlineReport = Path.Combine(fixture, "offline while running.txt");
            DuplicateExited(Start(renamedExe, "--test-offline \"" + offlineReport + "\""), "offline check while main instance runs");
            Check(File.Exists(offlineReport), "offline check was swallowed by single-instance dispatch");
            Check(File.ReadAllText(offlineReport).TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("PASS", StringComparison.Ordinal),
                "offline check did not pass while the main instance was running");
            SameWindow(first, firstWindow, "offline check while main instance runs");
            EditorUnchanged(editor, "offline check while main instance runs");
            Check(!File.Exists(secondSettings), "offline check wrote application settings in second directory");

            CloseOwned(first, "first instance close");
            Process restarted = Start(firstExe, "");
            WaitForWindow(restarted, "restart after exit");
            Check(restarted.Id != first.Id, "restart did not create a new process");
            CloseOwned(restarted, "restarted instance close");

            const int count = 3;
            var starts = new Process[count];
            var errors = new Exception[count];
            var threads = new Thread[count];
            using (var gate = new ManualResetEvent(false)) {
                for (int index = 0; index < count; index++) {
                    int slot = index;
                    threads[slot] = new Thread(delegate() {
                        try { gate.WaitOne(); starts[slot] = Start(firstExe, ""); }
                        catch (Exception error) { errors[slot] = error; }
                    });
                    threads[slot].Start();
                }
                gate.Set();
                foreach (Thread thread in threads) thread.Join();
            }
            for (int index = 0; index < count; index++)
                Check(errors[index] == null && starts[index] != null, "concurrent process launch failed");
            Process winner = null;
            Check(WaitUntil(delegate {
                int alive = 0;
                foreach (Process process in starts) if (Alive(process)) { alive++; winner = process; }
                return alive == 1 && winner.MainWindowHandle != IntPtr.Zero;
            }, 15000), "concurrent launches did not settle to exactly one main instance");
            foreach (Process process in starts)
                if (process != winner) DuplicateExited(process, "concurrent duplicate");
            IntPtr concurrentWindow = WaitForWindow(winner, "concurrent winner");
            DuplicateExited(Start(renamedExe, ""), "duplicate of concurrent winner");
            SameWindow(winner, concurrentWindow, "duplicate of concurrent winner");
            Check(!File.Exists(secondSettings), "concurrent duplicate wrote settings in second directory");
            CloseOwned(winner, "concurrent winner close");
            result = "PASS " + passed + " checks: real-process exclusivity, retained editor text, minimize restore, renamed copy, restart, simultaneous launch, and offline self-test. No serial command or user script was run.\r\n";
            exitCode = 0;
        } catch (Exception error) {
            result = "FAIL after " + passed + ": " + error + "\r\n";
            exitCode = 1;
        } finally { Cleanup(); }
        if (cleanupFailures != 0) {
            result = "FAIL: could not confirm cleanup of " + cleanupFailures + " owned process(es).\r\n" + result;
            exitCode = 1;
        }
        File.WriteAllText(report, result, new UTF8Encoding(false));
        return exitCode;
    }
}
