using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualBasic.ApplicationServices;

// The .NET Framework application model handles simultaneous starts and
// activation IPC before a second process can create a form or load settings.
sealed class SenderApplication : WindowsFormsApplicationBase
{
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    public SenderApplication()
    {
        IsSingleInstance = true;
        EnableVisualStyles = true;
        ShutdownStyle = ShutdownMode.AfterMainFormCloses;
    }

    protected override void OnCreateMainForm()
    {
        MainForm = new TextSender(new List<string>(CommandLineArgs).ToArray());
    }

    protected override void OnStartupNextInstance(StartupNextInstanceEventArgs e)
    {
        // SW_RESTORE also restores a previously maximized window correctly.
        if (MainForm.WindowState == FormWindowState.Minimized)
            ShowWindow(MainForm.Handle, 9);
        e.BringToForeground = true;
        base.OnStartupNextInstance(e);
        // Keep an open modal editor usable when the user launches us again.
        foreach (Form owned in MainForm.OwnedForms)
            if (owned.Visible) { owned.Activate(); break; }
    }
}

static class ApplicationIcon
{
    public static readonly Icon Value = Load();

    static Icon Load()
    {
        using (Stream stream = typeof(ApplicationIcon).Assembly.GetManifestResourceStream("TextSender.ico"))
        using (var icon = new Icon(stream))
            return (Icon)icon.Clone();
    }
}
