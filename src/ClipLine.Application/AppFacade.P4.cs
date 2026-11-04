using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>P4：预览 / 标注 / 修剪 / 拿在手上 / 右键扩展，仍共用唯一门面。</summary>
public sealed partial class AppFacade
{
    private readonly QuickLookModel _quickLook = new();
    private TrimRange _trimRange = new(0, 0, 0);

    private void OnQuickLookCommand(QuickLookCommand e)
    {
        var live = _line.Items.Where(i => i.IsLive).ToArray();
        _quickLook.Apply(e.Action, live.Length);
        if (e.Action is QuickLookAction.Close || !_quickLook.IsOpen)
        {
            Emit(CommandKind.CloseQuickLook);
            return;
        }
        if (live.Length == 0)
        {
            Emit(CommandKind.Beep);
            Emit(CommandKind.CloseQuickLook);
            return;
        }
        int index = Math.Clamp(_quickLook.Index, 0, live.Length - 1);
        Emit(CommandKind.UpdateQuickLookIndex, $"{index}/{live.Length}");
        Emit(CommandKind.ShowQuickLook, live[index].Path);
    }

    private void OnMarkupSaved(MarkupSaved e)
    {
        // E-01：按原格式重新编码，避免 Markup 默认低压缩 PNG 让文件体积膨胀。
        if (MarkupModel.NeedsRecode(e.SourceFormat))
            Emit(CommandKind.RewriteInOriginalFormat, e.Path + "|" + MarkupModel.TargetFormat(e.SourceFormat));
        Emit(CommandKind.ReloadThumbnail, e.Path); // E-02：保存后立即刷新绳上与「拿在手上」窗口
        Persist();
    }

    private void OnTrimRangeChanged(TrimRangeChanged e)
    {
        _trimRange = TrimModel.Clamp(new TrimRange(e.StartMs, e.EndMs, e.DurationMs));
        Emit(CommandKind.SetTrimRange, $"{_trimRange.StartMs}-{_trimRange.EndMs}/{_trimRange.DurationMs}");
    }

    private void OnTrimCommitted(TrimCommitted e)
    {
        string temp = TrimModel.TempFileName(e.Extension);
        // E-03：passthrough 不重编码 → 隐藏临时文件 → 原子替换，watcher 会忽略临时文件。
        Emit(CommandKind.ExportTrimPassthrough, e.Path + "->" + temp, _trimRange.LengthMs);
        Emit(CommandKind.ReplaceFileAtomically, temp + "->" + e.Path);
        Emit(CommandKind.ReloadThumbnail, e.Path);
    }

    private void OnHeldWindowCommand(HeldWindowCommand e)
    {
        var item = _line.Find(e.ItemId);
        if (item is null) return;
        switch (e.Action)
        {
            case HeldAction.HangBack:
                _line.SetHeld(e.ItemId, false);
                Emit(CommandKind.CloseHeldWindow, e.ItemId.ToString());
                Reveal(false);
                SetPeek(Layout.PeekMs);
                Persist();
                break;
            case HeldAction.Copy:
                OnSingleClick(e.ItemId);
                break;
            case HeldAction.QuickLook:
                Emit(CommandKind.ShowQuickLook, item.Path);
                break;
            case HeldAction.Markup:
                Emit(item.IsRecording ? CommandKind.ShowTrimEditor : CommandKind.ShowMarkupEditor, item.Path);
                break;
            case HeldAction.ActualSize:
                Emit(CommandKind.SetHeldWindowActualSize, e.ItemId.ToString());
                break;
            case HeldAction.Zoom:
                Emit(CommandKind.SetHeldWindowScale, e.ItemId.ToString(), HeldWindowModel.ClampScale(e.Scale));
                break;
        }
    }

    private void OnShellExtensionHang(ShellExtensionHang e)
    {
        var accepted = ShellExtensionModel.Accept(e.Paths);
        if (ShellExtensionModel.ShouldBeep(accepted.Count))
        {
            Emit(CommandKind.Beep); // X-01：一个都没成功则蜂鸣
            return;
        }
        foreach (string path in accepted)
        {
            // X-02：手动挂上的图片永远固定、不因满绳被挤掉、取下时文件保持原样。
            Dispatch(new NewItem(path, ItemOrigin.ShellExtension, false, true, false));
        }
        Reveal(true); // 绳自动展开
        Persist();
    }
}
