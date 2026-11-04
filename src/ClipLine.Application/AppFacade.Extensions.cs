namespace ClipLine.Application;

/// <summary>
/// 门面扩展点：P2–P5 在此追加新的输入分支。
/// 刻意用 partial 方法而不是新接口 —— 保证整个 v1 只有一个测试接缝。
/// 注意：扩展在主 switch 之后执行，因此也能观察已有输入（如批量取下后清剪贴板去重）。
/// </summary>
public sealed partial class AppFacade
{
    partial void DispatchExtension(AppInput input)
    {
        switch (input)
        {
            // ---- P2 捕获链路 ----
            case FileCreated e: OnFileCreated(e); break;
            case FileSizeChanged e: _integrity.ReportSize(e.Path, e.Size, _nowMs); break;
            case FileIntegrityResolved e: OnIntegrityResolved(e); break;
            case ClipboardChanged e: OnClipboardChanged(e); break;
            case CaptureRegion e:
                Emit(CommandKind.SetFlightOrigin, $"{e.X},{e.Y},{e.Width},{e.Height}");
                break;
            case IntegrityRetryElapsed e: OnIntegrityRetry(e); break;
            case TakeDownAll or TakeDownIds or FileDisappeared:
                _clipboardDedupe.Forget(); // C-06：被取下后同一张图可再次挂上
                break;

            // ---- P3 交互全集 ----
            case CardPointerDown e: OnCardDown(e); break;
            case CardPointerMove e: OnCardMove(e); break;
            case CardPointerUp e: OnCardUp(e); break;
            case GestureTimerElapsed e: OnGestureTimer(e); break;
            case CornerClicked e: OnCornerClicked(e.ItemId); break;
            case ContextMenuRequested e: OnContextMenuRequested(e.ItemId); break;
            case ContextMenuItem e: OnContextMenuItem(e); break;
            case DragBegan e: OnDragBegan(e.ItemId); break;
            case DragDropped e: OnDragDropped(e); break;
            case DragRecheckElapsed e: OnDragRecheck(e); break;
            case KeyBorrow e: OnKeyBorrow(e); break;
            case HotKeyConflict e: OnHotKeyConflict(e); break;
            case ShortcutRecorded e: OnShortcutRecorded(e); break;
            case ForceClickEquivalent e: OnForceClickEquivalent(e.ItemId); break;

            // ---- P4 系统替身 ----
            case QuickLookCommand e: OnQuickLookCommand(e); break;
            case MarkupSaved e: OnMarkupSaved(e); break;
            case TrimRangeChanged e: OnTrimRangeChanged(e); break;
            case TrimCommitted e: OnTrimCommitted(e); break;
            case HeldWindowCommand e: OnHeldWindowCommand(e); break;
            case ShellExtensionHang e: OnShellExtensionHang(e); break;

            default: break;
        }
    }
}
