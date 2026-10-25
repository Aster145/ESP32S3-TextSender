using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

/// <summary>Windows Explorer-style folder picker; call from the form's STA UI thread.</summary>
public static class ExplorerFolderPicker
{
    const int Cancelled = unchecked((int)0x800704C7);
    const uint PickFolders = 0x20;
    const uint ForceFileSystem = 0x40;
    const uint AllowMultiSelect = 0x200;
    const uint PathMustExist = 0x800;
    const uint FileSystemPath = 0x80058000;

    /// <summary>Returns the chosen existing filesystem folder, or null when cancelled.</summary>
    public static string Pick(IWin32Window owner, string initialDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("请从窗口的 STA 界面线程打开文件夹选择器。");

        IFileOpenDialog dialog = null;
        IShellItem result = null;
        IntPtr displayName = IntPtr.Zero;
        try {
            Type dialogType = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"));
            dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType);
            uint options;
            dialog.GetOptions(out options);
            // Keep the native defaults, with one existing filesystem folder selected.
            dialog.SetOptions((options | PickFolders | ForceFileSystem | PathMustExist) & ~AllowMultiSelect);
            dialog.SetTitle("选择文件夹");
            dialog.SetOkButtonLabel("选择此文件夹");

            if (!String.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) {
                IShellItem initial = null;
                try {
                    Guid shellItemId = typeof(IShellItem).GUID;
                    int createResult = SHCreateItemFromParsingName(Path.GetFullPath(initialDirectory),
                        IntPtr.Zero, ref shellItemId, out initial);
                    Marshal.ThrowExceptionForHR(createResult);
                    dialog.SetFolder(initial);
                } finally {
                    if (initial != null) Marshal.ReleaseComObject(initial);
                }
            }

            int showResult = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);
            if (showResult == Cancelled) return null;
            Marshal.ThrowExceptionForHR(showResult);

            dialog.GetResult(out result);
            result.GetDisplayName(FileSystemPath, out displayName);
            string selected = Marshal.PtrToStringUni(displayName);
            if (String.IsNullOrEmpty(selected) || !Directory.Exists(selected))
                throw new InvalidOperationException("所选文件夹不存在或已无法访问，请重新选择。");
            return selected;
        } catch (Exception error) {
            throw new InvalidOperationException("无法打开或读取 Windows 文件夹选择器。也可以在路径框中直接粘贴目录。\r\n" + error.Message, error);
        } finally {
            if (displayName != IntPtr.Zero) Marshal.FreeCoTaskMem(displayName);
            if (result != null) Marshal.ReleaseComObject(result);
            if (dialog != null) Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        [In] ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    // Flattened native vtable: IModalWindow.Show, then all IFileDialog methods,
    // then IFileOpenDialog.GetResults/GetSelectedItems. Do not remove unused slots.
    // Native interface/pointer parameters not used by this helper remain IntPtr.
    // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileopendialog
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem folder);
        void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem folder);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem folder);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem item);
        void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid([In] ref Guid clientGuid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, [In] ref Guid handlerId, [In] ref Guid interfaceId, out IntPtr result);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        void GetDisplayName(uint format, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare([MarshalAs(UnmanagedType.Interface)] IShellItem item, uint hint, out int order);
    }
}
