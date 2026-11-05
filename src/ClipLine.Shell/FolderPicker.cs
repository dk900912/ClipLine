using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.Shell;

/// <summary>
/// 文件夹选择器（P1-12）。旧的 <c>SHBrowseForFolderW</c> 是 Vista 前的目录树对话框：
/// 没有搜索、没有常用位置、不能跟随深色主题。这里改用 <c>IFileOpenDialog</c>
/// + <c>FOS_PICKFOLDERS</c>（Win7 起的标准选择器），并在任何一步失败时
/// 静默退回老对话框——宁可外观旧一点，也不能让「添加文件夹」点了没反应。
/// </summary>
internal static class FolderPicker
{
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ClsctxInprocServer = 0x1;
    private const uint CoinitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName,
            [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter,
        int dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IFileOpenDialog ppv);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFOW
    {
        public IntPtr HwndOwner;
        public IntPtr PidlRoot;
        public IntPtr PszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszTitle;
        public uint UlFlags;
        public IntPtr Lpfn;
        public IntPtr LParam;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    internal static string? Pick(string title)
    {
        if (TryPick(title, out string? modern)) return modern;
        return PickLegacy(title);
    }

    /// <summary>现代选择器；返回 false 表示「这台机器上创建失败，请用回退路径」。</summary>
    private static bool TryPick(string title, out string? path)
    {
        path = null;
        bool comInitialized = false;
        int hr = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
        if (hr >= 0) comInitialized = true;
        else if (hr != RpcEChangedMode) return false;

        IFileOpenDialog? dialog = null;
        try
        {
            Guid clsid = ClsidFileOpenDialog;
            Guid iid = typeof(IFileOpenDialog).GUID;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out dialog) != 0
                || dialog is null) return false;

            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("选择此文件夹");

            hr = dialog.Show(IntPtr.Zero);
            // 用户取消是正常结果，不能再弹一个老对话框问第二遍。
            if (hr < 0) return true;
            if (hr != 0) return true;

            dialog.GetResult(out IShellItem item);
            if (item is null) return true;
            try
            {
                item.GetDisplayName(SigdnFileSysPath, out string display);
                path = string.IsNullOrWhiteSpace(display) ? null : display;
            }
            finally
            {
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (dialog is not null && Marshal.IsComObject(dialog)) Marshal.ReleaseComObject(dialog);
            if (comInitialized) CoUninitialize();
        }
    }

    private static string? PickLegacy(string title)
    {
        IntPtr display = Marshal.AllocHGlobal(260 * 2);
        try
        {
            var info = new BROWSEINFOW
            {
                HwndOwner = IntPtr.Zero,
                PidlRoot = IntPtr.Zero,
                PszDisplayName = display,
                LpszTitle = title,
                UlFlags = 0x0001, // BIF_RETURNONLYFSDIRS
            };
            IntPtr pidl = SHBrowseForFolderW(ref info);
            if (pidl == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(260);
                return SHGetPathFromIDListW(pidl, buffer) ? buffer.ToString() : null;
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(display); }
    }
}
