namespace ClipLine.Domain;

// ---------------------------------------------------------------- 手势状态机

public enum GestureState
{
    Idle,
    Pressed,
    Dragging,
    AwaitingDoubleClick,
    Committed,
}

public enum GestureEventKind
{
    None,
    ScaleDown,
    PendingClick,
    Click,
    DoubleClick,
    LongPress,
    DragStart,
    DragEnd,
}

public sealed record GestureEvent(GestureEventKind Kind);

public static class GestureThresholds
{
    public const long LongPressMs = 450;
    public const double DragThresholdDip = 4;
    public const int CornerHitSize = 26;
    public const double PressScale = 0.95;
    public const double HoverScale = 1.035;
    public const double DragOpacity = 0.45;
    public const double DragPreviewOpacity = 0.45;

    /// <summary>V-16：角标命中区 26×26。</summary>
    public static bool IsCornerHit(double x, double y) =>
        x >= 0 && x <= CornerHitSize && y >= 0 && y <= CornerHitSize;
}

/// <summary>
/// G-01…G-06、V-25：单击延迟一个系统双击间隔、双击打开、0.45 秒长按标记、
/// 4 pt 拖拽阈值、按下缩到 0.95。纯状态机，无时钟依赖，由门面喂 nowMs。
/// </summary>
public sealed class GestureMachine
{
    private long _downAtMs;

    public GestureState State { get; private set; } = GestureState.Idle;

    public GestureEvent Down(long nowMs, bool isRecording = false)
    {
        if (State == GestureState.AwaitingDoubleClick)
        {
            State = GestureState.Committed;
            return new GestureEvent(GestureEventKind.DoubleClick);
        }
        _downAtMs = nowMs;
        State = GestureState.Pressed;
        return new GestureEvent(GestureEventKind.ScaleDown);
    }

    public GestureEvent Move(double dx, double dy)
    {
        if (State != GestureState.Pressed) return new GestureEvent(GestureEventKind.None);
        if (Math.Abs(dx) < GestureThresholds.DragThresholdDip &&
            Math.Abs(dy) < GestureThresholds.DragThresholdDip)
            return new GestureEvent(GestureEventKind.None);
        State = GestureState.Dragging;
        return new GestureEvent(GestureEventKind.DragStart);
    }

    /// <summary>长按定时器或双击间隔到期。</summary>
    public GestureEvent Timer(long nowMs)
    {
        if (State == GestureState.Pressed && nowMs - _downAtMs >= GestureThresholds.LongPressMs)
        {
            State = GestureState.Committed;
            return new GestureEvent(GestureEventKind.LongPress);
        }
        if (State == GestureState.AwaitingDoubleClick)
        {
            State = GestureState.Idle;
            return new GestureEvent(GestureEventKind.Click);
        }
        return new GestureEvent(GestureEventKind.None);
    }

    public GestureEvent Up()
    {
        if (State == GestureState.Dragging)
        {
            State = GestureState.Idle;
            return new GestureEvent(GestureEventKind.DragEnd);
        }
        if (State != GestureState.Pressed) return new GestureEvent(GestureEventKind.None);
        State = GestureState.AwaitingDoubleClick;
        return new GestureEvent(GestureEventKind.PendingClick);
    }

    public void Reset() => State = GestureState.Idle;
}

// ---------------------------------------------------------------- 右键菜单

public enum ContextAction
{
    Copy,
    Open,
    QuickLook,
    Hold,
    Markup,
    RevealInExplorer,
    Pin,
    Unpin,
    StoreToDesktop,
    TakeDown,
    Discard,
    MoveToRecycleBin,
    TakeDownLeft,
    TakeDownRight,
}

/// <summary>菜单项：Key 为本地化键（P-03 以英文原文作 key），Enabled 自报可用性。</summary>
public sealed record ContextMenuItemSpec(ContextAction Action, string Key, bool Enabled, bool SeparatorBefore = false);

/// <summary>G-11…G-18：菜单项与条件显隐。</summary>
public static class ContextMenuModel
{
    public static IReadOnlyList<ContextMenuItemSpec> Build(Pegged item, Line line)
    {
        var items = new List<ContextMenuItemSpec>
        {
            new(ContextAction.Copy, "Copy", true),
            new(ContextAction.Open, "Open", true),
            new(ContextAction.QuickLook, "Quick Look", true),
        };

        if (!item.IsRecording) // G-12
        {
            items.Add(new ContextMenuItemSpec(ContextAction.Hold, "Hold", true));
            items.Add(new ContextMenuItemSpec(ContextAction.Markup, "Markup", true));
        }

        items.Add(new ContextMenuItemSpec(ContextAction.RevealInExplorer, "Reveal in Explorer", true));

        // G-14：未固定 → 固定；已固定且非手动挂上 → 取消固定；手动挂上的固定项不显示取消固定。
        if (!item.Kept && !item.ByHand)
            items.Add(new ContextMenuItemSpec(ContextAction.Pin, "Pin", true));
        else if (item.Kept && !item.ByHand)
            items.Add(new ContextMenuItemSpec(ContextAction.Unpin, "Unpin", true));

        if (item.InOwnDirectory) // G-15
            items.Add(new ContextMenuItemSpec(ContextAction.StoreToDesktop, "Store to Desktop", true));

        // G-16：分隔符后的动作组。
        if (item.ByHand)
        {
            items.Add(new ContextMenuItemSpec(ContextAction.TakeDown, "Take Down", true, true));
        }
        else if (!item.Kept && item.InOwnDirectory)
        {
            items.Add(new ContextMenuItemSpec(ContextAction.Discard, "Discard", true, true));
        }
        else if (!item.Kept)
        {
            items.Add(new ContextMenuItemSpec(ContextAction.TakeDown, "Take Down", true, true));
            items.Add(new ContextMenuItemSpec(ContextAction.MoveToRecycleBin, "Move to Recycle Bin", true));
        }

        // G-17：无邻居时禁用。
        int left = line.NeighbourIds(item.Id, true).Count();
        int right = line.NeighbourIds(item.Id, false).Count();
        items.Add(new ContextMenuItemSpec(ContextAction.TakeDownLeft, "Take Down Left", left > 0, true));
        items.Add(new ContextMenuItemSpec(ContextAction.TakeDownRight, "Take Down Right", right > 0));

        return items;
    }

    public static string Serialize(IReadOnlyList<ContextMenuItemSpec> items) =>
        string.Join("|", items.Select(i => $"{(int)i.Action}:{i.Key}:{(i.Enabled ? 1 : 0)}:{(i.SeparatorBefore ? 1 : 0)}"));
}

// ---------------------------------------------------------------- 拖放语义

public enum DropTargetKind
{
    None,
    Application,
    Folder,
    Desktop,
    RecycleBin,
}

public enum DropEffect
{
    None,
    Copy,
    Move,
    Delete,
}

public sealed record DropOutcome(
    DropEffect Effect,
    bool RemoveFromLine,
    bool AppDeletesFile,
    long RecheckDelayMs,
    string Description);

/// <summary>G-07…G-10、N-08：拖放目标语义，Windows 上用落点 PIDL 判定目标类型。</summary>
public static class DragDropSemantics
{
    public static readonly DropEffect[] ExposedEffects = { DropEffect.Copy, DropEffect.Move, DropEffect.Delete };

    /// <summary>G-10：只在应用外上下文暴露这些效果。</summary>
    public static bool IsExternalOnly => true;

    public static DropOutcome Resolve(DropTargetKind target) => target switch
    {
        DropTargetKind.Application => new DropOutcome(DropEffect.Copy, false, false, 0, "送副本，原图仍在绳上"),
        DropTargetKind.Folder or DropTargetKind.Desktop => new DropOutcome(
            DropEffect.Move, true, false, Layout.DragRecheckMs, "移动文件并下线，0.6 秒后复查"),
        DropTargetKind.RecycleBin => new DropOutcome(
            DropEffect.Delete, true, true, 0, "应用自己把文件移入回收站"),
        _ => new DropOutcome(DropEffect.None, false, false, 0, "取消，卡片飞回原位"),
    };
}

// ---------------------------------------------------------------- 热键

public enum CardKey
{
    Space,
    Delete,
    Escape,
    Copy,
}

public enum ShortcutVerdict
{
    Accepted,
    RequiresModifier,
    Empty,
}

/// <summary>K-01 / K-06：快捷键录制规则与 macOS 符号映射。</summary>
public static class HotKeyModel
{
    public const string DefaultShortcut = "Ctrl+Alt+T";
    public const string NoneShortcut = "无";

    public static ShortcutVerdict Validate(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return ShortcutVerdict.Empty;
        bool hasModifier = shortcut.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                           shortcut.Contains("Alt", StringComparison.OrdinalIgnoreCase) ||
                           shortcut.Contains("Win", StringComparison.OrdinalIgnoreCase) ||
                           shortcut.Contains("Shift", StringComparison.OrdinalIgnoreCase);
        return hasModifier ? ShortcutVerdict.Accepted : ShortcutVerdict.RequiresModifier;
    }

    public static bool IsClearKey(string key) => key.Equals("Delete", StringComparison.OrdinalIgnoreCase);
    public static bool IsCancelKey(string key) => key.Equals("Escape", StringComparison.OrdinalIgnoreCase);

    /// <summary>K-06：⌘→Ctrl、⌥→Alt、⌃→Ctrl。</summary>
    public static string MacToWindows(string mac) => mac
        .Replace("⌘", "Ctrl+", StringComparison.Ordinal)
        .Replace("⌥", "Alt+", StringComparison.Ordinal)
        .Replace("⌃", "Ctrl+", StringComparison.Ordinal)
        .TrimEnd('+');

    /// <summary>N-01：力感应点按没有 Windows 物理等价，用中键 / 笔压 / 长按组合替代。</summary>
    public static readonly string[] ForceClickEquivalents = { "MiddleClick", "PenPressure", "LongPress" };
}
