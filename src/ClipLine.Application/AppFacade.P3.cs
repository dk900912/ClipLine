using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>P3：手势全集、右键菜单、拖放语义、键盘借用与热键，全部经同一门面。</summary>
public sealed partial class AppFacade
{
    private readonly GestureMachine _gesture = new();
    private long _gestureToken;
    private bool _quickLookOpen;
    private string _previousShortcut = HotKeyModel.DefaultShortcut;

    private void OnCardDown(CardPointerDown e)
    {
        var item = _line.Find(e.ItemId);
        if (item is null || item.Falling || item.Held) return;
        var gesture = _gesture.Down(_nowMs, item.IsRecording);
        long token = ++_gestureToken;
        if (gesture.Kind == GestureEventKind.DoubleClick)
        {
            Emit(CommandKind.OpenDefaultApp, item.Path); // G-02
            _gesture.Reset();
            _cardPressed = false;
            return;
        }
        _cardPressed = true; // V-25 / S-05：按下期间不收起
        Schedule(Layout.LongPressMs, new GestureTimerElapsed(item.Id, GestureTimerKind.LongPress, token));
    }

    private void OnCardMove(CardPointerMove e)
    {
        var gesture = _gesture.Move(e.Dx, e.Dy);
        if (gesture.Kind != GestureEventKind.DragStart) return;
        _isDragging = true;
        var item = _line.Find(e.ItemId);
        if (item is not null) Emit(CommandKind.SetDragPreview, item.Path, GestureThresholds.DragPreviewOpacity);
    }

    private void OnCardUp(CardPointerUp e)
    {
        var gesture = _gesture.Up();
        _cardPressed = false;
        if (gesture.Kind == GestureEventKind.PendingClick)
        {
            // G-01：为区分双击，复制延迟一个系统双击间隔才执行。
            long token = ++_gestureToken;
            Schedule((long)_env.SystemDoubleClickIntervalMs,
                new GestureTimerElapsed(e.ItemId, GestureTimerKind.DoubleClickWindow, token));
        }
        else if (gesture.Kind == GestureEventKind.DragEnd)
        {
            _isDragging = false;
        }
    }

    private void OnGestureTimer(GestureTimerElapsed e)
    {
        if (e.Token != _gestureToken) return;
        var gesture = _gesture.Timer(_nowMs);
        switch (gesture.Kind)
        {
            case GestureEventKind.LongPress:
                var longPressItem = _line.Find(e.ItemId);
                if (longPressItem is not null)
                {
                    // G-03：普通图片打开标记编辑器，录屏改为剪辑编辑器。
                    Emit(longPressItem.IsRecording ? CommandKind.ShowTrimEditor : CommandKind.ShowMarkupEditor,
                        longPressItem.Path);
                }
                _cardPressed = false;
                _gesture.Reset();
                break;

            case GestureEventKind.Click when e.Kind == GestureTimerKind.DoubleClickWindow:
                _gesture.Reset();
                OnSingleClick(e.ItemId);
                break;
        }
    }

    private void OnSingleClick(Guid id)
    {
        var item = _line.Find(id);
        if (item is null || item.Falling) return;
        if (item.IsRecording) Emit(CommandKind.WriteClipboardFile, item.Path); // D-11：录屏只放文件 URL
        else Emit(CommandKind.WriteClipboardImage, item.Path, _env.SystemDoubleClickIntervalMs);
        _copiedBadges[id] = true;
        Emit(CommandKind.ShowCopiedBadge, id.ToString(), Layout.CopiedBadgeMs);
        Schedule(Layout.CopiedBadgeMs, new CopiedBadgeElapsed(id)); // V-17：1.2 秒消失
    }

    private void OnCornerClicked(Guid id)
    {
        var item = _line.Find(id);
        if (item is null || item.Falling || item.Held) return;
        switch (Line.CornerActionFor(item))
        {
            case CornerAction.Trash:
                Emit(CommandKind.MoveToRecycleBin, item.Path);
                Dispatch(new TakeDownIds(new[] { id }));
                _line.MarkTrashed(id, "RecycleBin:" + item.Path); // N-08：软删除索引供撤销
                break;
            case CornerAction.Hide:
                Dispatch(new TakeDownIds(new[] { id })); // D-03 / D-07：文件不动
                break;
            case CornerAction.Unpin:
                _line.SetPinned(id, false);
                if (_settings.SoundOn) Emit(CommandKind.PlaySound, SoundCue.Tink.ToString(), 0.35);
                Persist();
                break;
        }
    }

    private void OnContextMenuRequested(Guid id)
    {
        var item = _line.Find(id);
        if (item is null) return;
        var specs = ContextMenuModel.Build(item, _line);
        _isShowingMenu = true; // G-18：菜单打开期间线保持展开
        Emit(CommandKind.ShowContextMenu, ContextMenuModel.Serialize(specs));
    }

    private void OnContextMenuItem(ContextMenuItem e)
    {
        var item = _line.Find(e.ItemId);
        if (item is null) return;
        _isShowingMenu = false;
        switch (e.Action)
        {
            case ContextAction.Copy: OnSingleClick(e.ItemId); break;
            case ContextAction.Open: Emit(CommandKind.OpenDefaultApp, item.Path); break;
            case ContextAction.QuickLook: Emit(CommandKind.ShowQuickLook, item.Path); break;
            case ContextAction.Hold:
                _line.SetHeld(e.ItemId, true);
                Emit(CommandKind.ShowHeldWindow, e.ItemId.ToString());
                Persist();
                break;
            case ContextAction.Markup:
                Emit(item.IsRecording ? CommandKind.ShowTrimEditor : CommandKind.ShowMarkupEditor, item.Path);
                break;
            case ContextAction.RevealInExplorer: Emit(CommandKind.ShowInExplorer, item.Path); break;
            case ContextAction.Pin:
                _line.SetPinned(e.ItemId, true);
                if (_settings.SoundOn) Emit(CommandKind.PlaySound, SoundCue.Tink.ToString(), 0.35);
                Persist();
                break;
            case ContextAction.Unpin:
                _line.SetPinned(e.ItemId, false);
                Persist();
                break;
            case ContextAction.StoreToDesktop:
                string desktop = "Desktop\\" + System.IO.Path.GetFileName(item.Path);
                Emit(CommandKind.MoveFile, item.Path + "->" + desktop);
                Emit(CommandKind.UpdateClipboardFileReference, desktop); // D-08：剪贴板引用跟随
                Dispatch(new TakeDownIds(new[] { e.ItemId }));
                break;
            case ContextAction.TakeDown:
                Dispatch(new TakeDownIds(new[] { e.ItemId }));
                break;
            case ContextAction.Discard:
            case ContextAction.MoveToRecycleBin:
                Emit(CommandKind.MoveToRecycleBin, item.Path);
                Dispatch(new TakeDownIds(new[] { e.ItemId }));
                _line.MarkTrashed(e.ItemId, "RecycleBin:" + item.Path);
                break;
            case ContextAction.TakeDownLeft:
            case ContextAction.TakeDownRight:
                var neighbours = _line.NeighbourIds(e.ItemId, e.Action == ContextAction.TakeDownLeft).ToArray();
                Dispatch(new TakeDownIds(neighbours)); // 一次批量动作算一步
                break;
        }
    }

    private void OnDragBegan(Guid id)
    {
        _isDragging = true;
        var item = _line.Find(id);
        if (item is not null) Emit(CommandKind.SetDragPreview, item.Path, GestureThresholds.DragPreviewOpacity);
    }

    private void OnDragDropped(DragDropped e)
    {
        var item = _line.Find(e.ItemId);
        if (item is null) return;
        _isDragging = false;
        var outcome = DragDropSemantics.Resolve(e.Target);
        string destination = e.Destination ?? item.Path;

        switch (e.Target)
        {
            case DropTargetKind.Application:
                Emit(CommandKind.CopyFile, item.Path + "->" + destination); // G-07：原图仍在绳上
                break;
            case DropTargetKind.Folder:
            case DropTargetKind.Desktop:
                Emit(CommandKind.MoveFile, item.Path + "->" + destination);
                Emit(CommandKind.UpdateClipboardFileReference, destination);
                Dispatch(new TakeDownIds(new[] { e.ItemId }));
                Schedule(outcome.RecheckDelayMs, new DragRecheckElapsed(destination)); // G-08
                break;
            case DropTargetKind.RecycleBin:
                Emit(CommandKind.MoveToRecycleBin, item.Path); // G-09：应用自己删
                Dispatch(new TakeDownIds(new[] { e.ItemId }));
                _line.MarkTrashed(e.ItemId, "RecycleBin:" + item.Path);
                break;
            default:
                Emit(CommandKind.ReloadThumbnail, item.Path); // G-06：取消时飞回原位
                break;
        }
    }

    private void OnDragRecheck(DragRecheckElapsed e) => Emit(CommandKind.RecheckDrop, e.Path);

    private void OnKeyBorrow(KeyBorrow e)
    {
        var item = _line.Find(e.ItemId);
        if (item is null) return;
        switch (e.Key)
        {
            case CardKey.Space:
                _quickLookOpen = !_quickLookOpen;
                Emit(_quickLookOpen ? CommandKind.ShowQuickLook : CommandKind.CloseQuickLook, item.Path);
                break;
            case CardKey.Delete:
                Dispatch(new CornerClicked(e.ItemId)); // K-03：等同角标
                break;
            case CardKey.Escape:
                Emit(CommandKind.CloseHeldWindow, e.ItemId.ToString());
                break;
            case CardKey.Copy:
                OnSingleClick(e.ItemId);
                break;
        }
    }

    private void OnForceClickEquivalent(Guid id)
    {
        var item = _line.Find(id);
        if (item is not null) Emit(CommandKind.ShowQuickLook, item.Path);
    }

    private void OnHotKeyConflict(HotKeyConflict e)
    {
        Emit(CommandKind.Beep);
        Emit(CommandKind.RollbackShortcut, _previousShortcut);
        _settings.Shortcut = _previousShortcut;
        Persist();
    }

    private void OnShortcutRecorded(ShortcutRecorded e)
    {
        if (HotKeyModel.IsCancelKey(e.PressedKey)) return; // Esc 取消
        _previousShortcut = _settings.Shortcut;
        if (HotKeyModel.IsClearKey(e.PressedKey))
        {
            _settings.Shortcut = HotKeyModel.NoneShortcut; // Delete 清空为「无」
            Emit(CommandKind.UnregisterHotKey);
            Persist();
            return;
        }
        if (HotKeyModel.Validate(e.Shortcut) != ShortcutVerdict.Accepted)
        {
            Emit(CommandKind.Beep); // 必须含 Ctrl / Alt / Win / Shift 之一
            return;
        }
        Emit(CommandKind.UnregisterHotKey, _settings.Shortcut);
        _settings.Shortcut = e.Shortcut;
        Emit(CommandKind.RegisterHotKey, e.Shortcut);
        Persist();
    }
}
