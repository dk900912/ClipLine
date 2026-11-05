using System;
using System.Collections.Generic;
using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// 掉落层：一个全屏宽、有限高的自管分层 HWND，画被取下的卡片从绳上翻落。
/// 对齐 Tendedero 的 <c>CaptureFlight.fall</c> —— 必须是独立窗口，
/// 否则 520 DIP 的下落会被顶部那条窄 strip 裁掉。
///
/// 复用 RopeWindow 已注册的窗口类（显式箭头光标，杜绝等待小圆圈）；
/// 无活跃掉落时隐藏窗口，不占合成器、不做任何重绘。
/// </summary>
internal sealed class FallWindow : IDisposable
{
    private readonly double _scale;
    private IntPtr _hwnd;
    private RopeCanvas? _canvas;
    private bool _disposed;

    public FallWindow(double scale) => _scale = scale <= 0 ? 1.0 : scale;

    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);

    public bool Present(IReadOnlyList<FallCard> falls)
    {
        RopeWindow.EnsureClass();
        int widthPx = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
        // 起点约 22 DIP + 卡片高 + 520 DIP 下落 + 旋转横向溢出余量。
        int heightDip = Layout.RopeTop + 12 + Layout.PhotoMaxHeight(CardSize.Large)
            + (int)CaptureFlight.FallDistanceDip + 60;
        int heightPx = (int)Math.Round(heightDip * _scale);
        if (widthPx <= 0 || heightPx <= 0) return false;

        if (!Exists)
        {
            uint exStyle = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_TRANSPARENT
                | ShellNative.WS_EX_NOACTIVATE | ShellNative.WS_EX_TOOLWINDOW;
            _hwnd = ShellNative.CreateWindowExW(exStyle, RopeWindow.ClassName, "ClipLine fall",
                ShellNative.WS_POPUP, 0, 0, widthPx, heightPx, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, 0, 0, 0, 0,
                ShellNative.SWP_NOMOVE | ShellNative.SWP_NOSIZE | ShellNative.SWP_NOACTIVATE);
        }

        if (_canvas is null || _canvas.WidthPx != widthPx || _canvas.HeightPx != heightPx)
        {
            _canvas?.Dispose();
            _canvas = new RopeCanvas(widthPx, heightPx, _scale);
        }

        _canvas.RenderFall(falls);
        if (!_canvas.Present(_hwnd)) return false;
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOWNOACTIVATE);
        return true;
    }

    /// <summary>把当前掉落帧导出成 PNG，作为确定性的渲染证据。</summary>
    public bool DumpFrame(string path) => _canvas?.SavePng(path) ?? false;

    public void Hide()
    {
        if (Exists) ShellNative.ShowWindow(_hwnd, ShellNative.SW_HIDE);
    }

    public void DestroyNow()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShellNative.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyNow();
        _canvas?.Dispose();
        _canvas = null;
    }
}
