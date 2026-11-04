using ClipLine.Domain;

namespace ClipLine.Application;

public sealed record CardPointerDown(Guid ItemId, int X, int Y) : AppInput;
public sealed record CardPointerMove(Guid ItemId, double Dx, double Dy) : AppInput;
public sealed record CardPointerUp(Guid ItemId, int X, int Y) : AppInput;
public sealed record CornerClicked(Guid ItemId) : AppInput;
public sealed record ContextMenuRequested(Guid ItemId) : AppInput;
public sealed record ContextMenuItem(Guid ItemId, ContextAction Action) : AppInput;
public sealed record DragBegan(Guid ItemId) : AppInput;
public sealed record DragDropped(Guid ItemId, DropTargetKind Target, string? Destination = null) : AppInput;
public sealed record KeyBorrow(CardKey Key, Guid ItemId) : AppInput;
public sealed record HotKeyConflict(string Shortcut) : AppInput;
public sealed record ShortcutRecorded(string Shortcut, string PressedKey) : AppInput;

/// <summary>N-01：中键 / 笔压 / 长按组合替代 macOS 的力感应点按，结果对齐为快速查看。</summary>
public sealed record ForceClickEquivalent(Guid ItemId) : AppInput;

internal enum GestureTimerKind
{
    LongPress,
    DoubleClickWindow,
}

internal sealed record GestureTimerElapsed(Guid ItemId, GestureTimerKind Kind, long Token) : AppInput;
