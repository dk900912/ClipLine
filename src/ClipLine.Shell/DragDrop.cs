using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.Shell;

/// <summary>拖拽结束后的业务结论。</summary>
public enum DragOutcome
{
    /// <summary>取消 / 目标拒收 / 原地放下 —— 卡片留在绳上。</summary>
    Cancelled = 0,

    /// <summary>目标复制了内容，文件还在原处 —— 卡片留在绳上。</summary>
    Copied = 1,

    /// <summary>文件已离开原路径（移进文件夹或被回收站收走）—— 卡片应当下线。</summary>
    Moved = 2,
}

/// <summary>
/// 把绳上的一张卡片变成一次标准 OLE 拖放（P0-5）。
///
/// 只暴露 <c>CF_HDROP</c>：资源管理器、聊天工具、邮件客户端、浏览器上传区
/// 认的都是「文件列表」，不需要再猜哪个格式更受欢迎。拖拽图用系统的
/// <c>IDragSourceHelper</c> 贴卡片自身的 32bpp 预乘位图，观感与卡片一致。
///
/// 业务判定不依赖 <c>DROPEFFECT</c>（不同目标对 MOVE 的回报并不一致），
/// 而是回到磁盘看原路径是否还在：不在就是「移走或被删」，还在就是「复制或取消」。
///
/// 互操作要点：接口全部自定义且方法只含 blittable 参数，COM 指针统一用
/// <see cref="IntPtr"/> 手工 AddRef/Release。用 <c>ComTypes.IDataObject</c> 这类
/// 「导入接口」当托管实现，CLR 生成的 CCW vtable 与 OLE 期望不一致，会在
/// <c>DoDragDrop</c> 内部把执行引擎打崩（0x80131506）。
/// </summary>
public static class DragDropBridge
{
    internal const short CfHDrop = 15;
    private const uint DropEffectNone = 0;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const int MkLButton = 0x0001;

    private const int SOk = 0;
    private const int SFalse = 1;
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);

    private static bool s_oleReady;
    private static bool s_oleAttempted;

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int DoDragDrop(IntPtr pDataObj, IntPtr pDropSource,
        uint dwOKEffects, out uint pdwEffect);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    /// <summary>COM 需要 STA。失败（例如已按 MTA 初始化过）就静默放弃拖放，不影响其他功能。</summary>
    internal static bool EnsureOle()
    {
        if (s_oleAttempted) return s_oleReady;
        s_oleAttempted = true;
        int hr = OleInitialize(IntPtr.Zero);
        // S_OK / S_FALSE 都表示本线程已可用；RPC_E_CHANGED_MODE 说明线程已按别的套间初始化。
        s_oleReady = hr is 0 or 1;
        return s_oleReady;
    }

    /// <summary>
    /// 启动一次文件拖放。返回时拖拽已经结束，调用方据此决定卡片去留。
    /// </summary>
    internal static DragOutcome DragFile(IntPtr owner, string path, IntPtr dragBitmap,
        int bitmapWidth, int bitmapHeight, int hotspotX, int hotspotY)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return DragOutcome.Cancelled;
        if (!EnsureOle()) return DragOutcome.Cancelled;

        var data = new ShellDragData(path);
        var source = new ShellDropSource();
        IntPtr pData = IntPtr.Zero;
        IntPtr pSource = IntPtr.Zero;
        try
        {
            pData = Marshal.GetComInterfaceForObject(data, typeof(IShellDataObject));
            pSource = Marshal.GetComInterfaceForObject(source, typeof(IShellDropSource));
            if (pData == IntPtr.Zero || pSource == IntPtr.Zero) return DragOutcome.Cancelled;

            // 拖拽预览只是加分项：拿不到就退回系统默认的「文件图标 + 文件名」。
            if (dragBitmap != IntPtr.Zero && bitmapWidth > 0 && bitmapHeight > 0)
            {
                try { DragImage.Attach(pData, dragBitmap, bitmapWidth, bitmapHeight, hotspotX, hotspotY); }
                catch (Exception) { /* 预览失败不影响拖放本身 */ }
            }

            uint effect = DropEffectNone;
            int hr = DoDragDrop(pData, pSource, DropEffectCopy | DropEffectMove, out effect);
            if (hr != SOk) return DragOutcome.Cancelled;
            // 磁盘是唯一可信的判据：目标说 MOVE 但没真动，或者目标说 COPY 其实搬走了，
            // 都按「文件还在不在」收敛，避免绳上留下已经失效的卡片。
            return Classify(effect, File.Exists(path));
        }
        finally
        {
            if (pSource != IntPtr.Zero) Marshal.Release(pSource);
            if (pData != IntPtr.Zero) Marshal.Release(pData);
            data.ReleaseOwnedMedia();
        }
    }

    /// <summary>
    /// 把「目标回报的效果 + 原路径是否还在」收敛成业务结论。
    /// 抽成纯函数是为了让冒烟能确定性地覆盖三种组合，不必真的把文件拖到桌面上。
    /// </summary>
    internal static DragOutcome Classify(uint dropEffect, bool fileStillExists)
    {
        if (dropEffect == DropEffectNone) return DragOutcome.Cancelled;
        return fileStillExists ? DragOutcome.Copied : DragOutcome.Moved;
    }

    /// <summary>
    /// 冒烟用：真正构造一次 CF_HDROP 载荷再读回来，确认
    /// DROPFILES 头正确、路径是 UTF-16 且双零结尾（这两点错了资源管理器会静默拒收）。
    /// </summary>
    internal static (bool Wide, bool DoubleNull, string Path) InspectPayload(string path)
    {
        IntPtr handle = BuildHDrop(path);
        if (handle == IntPtr.Zero) return (false, false, string.Empty);
        try
        {
            IntPtr target = ShellNative.GlobalLock(handle);
            if (target == IntPtr.Zero) return (false, false, string.Empty);
            try
            {
                var drop = Marshal.PtrToStructure<DROPFILES>(target);
                string text = Marshal.PtrToStringUni(target + (int)drop.PFiles) ?? string.Empty;
                int end = (int)drop.PFiles + (text.Length + 1) * 2;
                // 双零结尾：再往后两个字节必须是 0，否则资源管理器会把相邻内存当文件名。
                bool doubleNull = Marshal.ReadInt16(target + end) == 0;
                return (drop.FWide == 1, doubleNull, text);
            }
            finally
            {
                ShellNative.GlobalUnlock(handle);
            }
        }
        finally
        {
            ShellNative.GlobalFree(handle);
        }
    }

    /// <summary>构造 CF_HDROP 需要的 HGLOBAL：DROPFILES 头 + UTF-16 双零结尾路径。</summary>
    private static IntPtr BuildHDrop(string path)
    {
        byte[] name = Encoding.Unicode.GetBytes(path + "\0\0");
        int head = Marshal.SizeOf<DROPFILES>();
        int size = head + name.Length;
        IntPtr handle = ShellNative.GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)(uint)size);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        IntPtr target = ShellNative.GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            ShellNative.GlobalFree(handle);
            return IntPtr.Zero;
        }
        try
        {
            var drop = new DROPFILES { PFiles = (uint)head, FNC = 0, FWide = 1 };
            Marshal.StructureToPtr(drop, target, false);
            Marshal.Copy(name, 0, target + head, name.Length);
        }
        finally
        {
            ShellNative.GlobalUnlock(handle);
        }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint PFiles;
        public int X;
        public int Y;
        public int FNC;
        public int FWide;
    }

    // ---- 全部自定义、全 blittable 的 COM 契约 ----

    internal const int TymedHGLOBAL = 1;
    internal const int DvAspectContent = 1;
    internal const int DatadirGet = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FORMATETC
    {
        public short cfFormat;
        public IntPtr ptd;
        public int dwAspect;
        public int lindex;
        public int tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STGMEDIUM
    {
        public int tymed;
        public IntPtr unionmember;
        public IntPtr pUnkForRelease;
    }

    [ComVisible(true)]
    [Guid("0000010E-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellDataObject
    {
        [PreserveSig] int GetData(ref FORMATETC pformatetcIn, out STGMEDIUM pmedium);
        [PreserveSig] int GetDataHere(ref FORMATETC pformatetc, ref STGMEDIUM pmedium);
        [PreserveSig] int QueryGetData(ref FORMATETC pformatetc);
        [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC pformatectIn, out FORMATETC pformatetcOut);
        [PreserveSig] int SetData(ref FORMATETC pformatetc, ref STGMEDIUM pmedium,
            [MarshalAs(UnmanagedType.Bool)] bool fRelease);
        [PreserveSig] int EnumFormatEtc(uint dwDirection, out IntPtr ppenum);
        [PreserveSig] int DAdvise(ref FORMATETC pformatetc, uint advf, IntPtr pAdvSink,
            out uint pdwConnection);
        [PreserveSig] int DUnadvise(uint dwConnection);
        [PreserveSig] int EnumDAdvise(out IntPtr ppenumAdvise);
    }

    [ComVisible(true)]
    [Guid("00000103-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellEnumFormatEtc
    {
        // 数组/长度都按裸指针收发：CCW 上的数组 marshal 需要 SizeParamIndex，
        // 这里用不上那套规则，手工写内存最不容易出意外。
        [PreserveSig] int Next(int celt, IntPtr rgelt, IntPtr pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr newEnum);
    }

    [ComVisible(true)]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellDropSource
    {
        [PreserveSig] int QueryContinueDrag(int fEscapePressed, int grfKeyState);
        [PreserveSig] int GiveFeedback(int dwEffect);
    }

    /// <summary>
    /// 极简 <c>IDataObject</c>：只认 CF_HDROP。
    /// 每次 <c>GetData</c> 都新分配一个 HGLOBAL —— 所有权随返回值转移给目标，
    /// 若复用同一个句柄会有双释放崩溃的风险。
    /// </summary>
    private sealed class ShellDragData : IShellDataObject
    {
        private readonly string _path;
        private readonly List<STGMEDIUM> _owned = new();

        internal ShellDragData(string path) => _path = path;

        public int GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if (format.cfFormat != CfHDrop) return DvEFormatEtc;
            IntPtr handle = BuildHDrop(_path);
            if (handle == IntPtr.Zero) return unchecked((int)0x8007000E); // E_OUTOFMEMORY
            medium = new STGMEDIUM { tymed = TymedHGLOBAL, unionmember = handle };
            return SOk;
        }

        public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => DvEFormatEtc;

        public int QueryGetData(ref FORMATETC format) => format.cfFormat == CfHDrop ? SOk : DvEFormatEtc;

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            formatOut.ptd = IntPtr.Zero;
            return SOk;
        }

        public int SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            // IDragSourceHelper 会在这里塞 DragImageBits。只负责在拖拽结束时
            // 把这些 medium 释放掉，绝不回吐给目标，避免所有权混乱。
            if (release) _owned.Add(medium);
            return SOk;
        }

        public int EnumFormatEtc(uint dwDirection, out IntPtr ppenum)
        {
            ppenum = IntPtr.Zero;
            if (dwDirection != DatadirGet) return ENotImpl;
            var enumerator = new SingleFormatEnumerator();
            ppenum = Marshal.GetComInterfaceForObject(enumerator, typeof(IShellEnumFormatEtc));
            return ppenum == IntPtr.Zero ? ENotImpl : SOk;
        }

        public int DAdvise(ref FORMATETC pformatetc, uint advf, IntPtr pAdvSink, out uint pdwConnection)
        {
            pdwConnection = 0;
            return OleEAdviseNotSupported;
        }

        public int DUnadvise(uint dwConnection) => OleEAdviseNotSupported;

        public int EnumDAdvise(out IntPtr ppenumAdvise)
        {
            ppenumAdvise = IntPtr.Zero;
            return OleEAdviseNotSupported;
        }

        internal void ReleaseOwnedMedia()
        {
            foreach (STGMEDIUM media in _owned)
            {
                STGMEDIUM copy = media;
                ReleaseStgMedium(ref copy);
            }
            _owned.Clear();
        }
    }

    /// <summary>只有一项的格式枚举器，让 Chromium / Electron 类目标能顺利列举格式。</summary>
    private sealed class SingleFormatEnumerator : IShellEnumFormatEtc
    {
        private bool _consumed;

        public int Next(int celt, IntPtr rgelt, IntPtr pceltFetched)
        {
            int written = 0;
            if (!_consumed && celt > 0 && rgelt != IntPtr.Zero)
            {
                var format = new FORMATETC
                {
                    cfFormat = CfHDrop,
                    ptd = IntPtr.Zero,
                    dwAspect = DvAspectContent,
                    lindex = -1,
                    tymed = TymedHGLOBAL,
                };
                Marshal.StructureToPtr(format, rgelt, false);
                _consumed = true;
                written = 1;
            }
            if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, written);
            return written == celt ? SOk : SFalse;
        }

        public int Skip(int celt)
        {
            _consumed = true;
            return SOk;
        }

        public int Reset()
        {
            _consumed = false;
            return SOk;
        }

        public int Clone(out IntPtr newEnum)
        {
            var clone = new SingleFormatEnumerator();
            if (_consumed) clone.Skip(1);
            newEnum = Marshal.GetComInterfaceForObject(clone, typeof(IShellEnumFormatEtc));
            return SOk;
        }
    }

    /// <summary>左键松开 = 放下，Esc = 取消；光标交给系统默认拖拽光标。</summary>
    private sealed class ShellDropSource : IShellDropSource
    {
        private const int DragDropSDrop = 0x00040100;
        private const int DragDropSCancel = 0x00040101;
        private const int DragDropSUseDefaultCursors = 0x00040102;

        public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
        {
            if (fEscapePressed != 0) return DragDropSCancel;
            if ((grfKeyState & MkLButton) == 0) return DragDropSDrop;
            return SOk;
        }

        public int GiveFeedback(int dwEffect) => DragDropSUseDefaultCursors;
    }

    /// <summary>系统拖拽图：把卡片本体贴上去，热点对齐抓取点。</summary>
    private static class DragImage
    {
        private static readonly Guid ClsidDragDropHelper = new("4657278B-411B-11D2-839A-00C04FD918D0");
        private static readonly Guid IidDragSourceHelper = new("DE5BF786-477A-11D2-839D-00C04FD918D0");

        [ComVisible(true)]
        [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDragSourceHelper
        {
            [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE pshdi, IntPtr pDataObject);
            [PreserveSig] int InitializeFromWindow(IntPtr hwnd, IntPtr ppt, IntPtr pDataObject);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHDRAGIMAGE
        {
            public int SizeX;
            public int SizeY;
            public int OffsetX;
            public int OffsetY;
            public IntPtr HBmp;
            public uint ColorKey;
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter,
            uint dwClsContext, ref Guid riid, out IntPtr ppv);

        internal static void Attach(IntPtr pDataObject, IntPtr bitmap, int w, int h,
            int hotX, int hotY)
        {
            Guid clsid = ClsidDragDropHelper;
            Guid iid = IidDragSourceHelper;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */,
                ref iid, out IntPtr pHelper);
            if (hr != SOk || pHelper == IntPtr.Zero) return;
            IntPtr vtable = Marshal.ReadIntPtr(pHelper);
            // IDragSourceHelper 的 vtable：0..2 = IUnknown，3 = InitializeFromBitmap，
            // 4 = InitializeFromWindow。按序号取出函数指针，避免为它再定义一个
            // 「导入接口」——那正是把 CLR 打崩的那类写法。
            IntPtr fn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeFromBitmapFn>(fn);
            var image = new SHDRAGIMAGE
            {
                SizeX = w,
                SizeY = h,
                OffsetX = Math.Clamp(hotX, 0, w),
                OffsetY = Math.Clamp(hotY, 0, h),
                HBmp = bitmap,
                // CLR_NONE：不做色键透明，直接用位图自带的 alpha。
                ColorKey = 0xFFFFFFFF,
            };
            try { initialize(pHelper, ref image, pDataObject); }
            finally { Marshal.Release(pHelper); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeFromBitmapFn(IntPtr self, ref SHDRAGIMAGE image, IntPtr dataObject);
    }
}
