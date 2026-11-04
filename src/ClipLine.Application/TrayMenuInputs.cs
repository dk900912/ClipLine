using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>托盘菜单每次打开都重建（T-05）。</summary>
public sealed record TrayMenuOpening : AppInput;

public sealed record TrayMenuItemInvoked(TrayMenuAction Action, string? Argument = null) : AppInput;

public sealed record ThemeChanged(ThemeMode Mode, bool ReduceMotion) : AppInput;

public sealed record SetLanguage(string Language) : AppInput;

public sealed record LocalizationQuery(string Key) : AppInput;
