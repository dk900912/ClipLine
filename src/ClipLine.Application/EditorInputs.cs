using ClipLine.Domain;

namespace ClipLine.Application;

public sealed record QuickLookCommand(QuickLookAction Action) : AppInput;
public sealed record MarkupSaved(string Path, string SourceFormat) : AppInput;
public sealed record TrimRangeChanged(long StartMs, long EndMs, long DurationMs) : AppInput;
public sealed record TrimCommitted(string Path, string Extension) : AppInput;

public enum HeldAction
{
    HangBack,
    Copy,
    QuickLook,
    Markup,
    ActualSize,
    Zoom,
}

public sealed record HeldWindowCommand(HeldAction Action, Guid ItemId, double Scale = 1.0) : AppInput;

/// <summary>X-01：资源管理器右键「挂到 ClipLine」，支持多选。</summary>
public sealed record ShellExtensionHang(IReadOnlyList<string> Paths) : AppInput;
