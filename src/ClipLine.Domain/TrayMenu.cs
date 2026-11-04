namespace ClipLine.Domain;

public enum TrayMenuAction
{
    ToggleLine,
    TakeDownAll,
    PutBack,
    HangEverythingBack,
    ToggleInbox,
    ToggleClipboard,
    RevealOnPointer,
    PeekOnNewContent,
    ShowOverFullScreen,
    WatchFolder,
    StopWatching,
    AddFolder,
    KeepFits,
    Keep25,
    Keep50,
    Keep100,
    SizeSmall,
    SizeMedium,
    SizeLarge,
    ChangeShortcut,
    OpenScreenshotsFolder,
    Sound,
    LaunchAtLogin,
    CheckForUpdates,
    Quit,
}

public sealed record TrayMenuSpec(
    int Order,
    TrayMenuAction Action,
    string Key,
    bool Enabled = true,
    bool Checked = false,
    bool SeparatorBefore = false,
    string? Shortcut = null,
    IReadOnlyList<TrayMenuSpec>? Children = null);

public sealed record TrayMenuContext(
    int LiveCount,
    bool CanPutBack,
    bool HasHeld,
    bool InboxEnabled,
    bool ClipboardEnabled,
    bool RevealOnPointer,
    bool PeekOnNewContent,
    bool ShowOverFullScreen,
    int KeepOnLine,
    CardSize Size,
    bool SoundOn,
    bool LaunchAtLogin,
    IReadOnlyList<string> ExtraFolders,
    string Shortcut,
    bool IsRevealed);

/// <summary>T-05 / T-06 / T-07：16 项菜单，每次打开重建，各项自报可用性。</summary>
public static class TrayMenuModel
{
    public const int MainItemCount = 16;

    public static IReadOnlyList<TrayMenuSpec> Build(TrayMenuContext c)
    {
        var watched = c.ExtraFolders
            .Select((path, i) => new TrayMenuSpec(80 + i, TrayMenuAction.WatchFolder, path, true, true))
            .ToList();
        watched.Add(new TrayMenuSpec(
            89, TrayMenuAction.AddFolder, "Add Folder…",
            c.ExtraFolders.Count < Settings.MaxExtraFolders));
        watched.Add(new TrayMenuSpec(90, TrayMenuAction.StopWatching, "Stop Watching ▸",
            c.ExtraFolders.Count > 0, Children: c.ExtraFolders
                .Select((path, i) => new TrayMenuSpec(91 + i, TrayMenuAction.StopWatching, path)).ToArray()));

        return new[]
        {
            new TrayMenuSpec(1, TrayMenuAction.ToggleLine,
                c.IsRevealed ? "Hide Line" : "Show Line", true, false, false, c.Shortcut),
            new TrayMenuSpec(2, TrayMenuAction.TakeDownAll, "Take Down All", c.LiveCount > 0),
            new TrayMenuSpec(3, TrayMenuAction.PutBack, "Put Back", c.CanPutBack, false, false, "Ctrl+Z"),
            new TrayMenuSpec(4, TrayMenuAction.HangEverythingBack, "Hang Everything Back", c.HasHeld),
            new TrayMenuSpec(5, TrayMenuAction.ToggleInbox, "Take Over Screenshots", true, c.InboxEnabled),
            new TrayMenuSpec(6, TrayMenuAction.ToggleClipboard, "Hang Copied Images", true, c.ClipboardEnabled),
            new TrayMenuSpec(7, TrayMenuAction.RevealOnPointer, "Lower the Line ▸", true, false, false, null,
                new[]
                {
                    new TrayMenuSpec(71, TrayMenuAction.RevealOnPointer, "Pointer at Menu Bar", true, c.RevealOnPointer),
                    new TrayMenuSpec(72, TrayMenuAction.PeekOnNewContent, "New Content", true, c.PeekOnNewContent),
                    new TrayMenuSpec(73, TrayMenuAction.ShowOverFullScreen, "Over Full Screen Apps", true, c.ShowOverFullScreen),
                }),
            new TrayMenuSpec(8, TrayMenuAction.WatchFolder, "Watch Folders ▸", true, false, false, null, watched),
            new TrayMenuSpec(9, TrayMenuAction.KeepFits, "Keep on Line ▸", true, false, false, null,
                new[]
                {
                    new TrayMenuSpec(91, TrayMenuAction.KeepFits, "As Many As Fit", true, c.KeepOnLine == 0),
                    new TrayMenuSpec(92, TrayMenuAction.Keep25, "25", true, c.KeepOnLine == 25),
                    new TrayMenuSpec(93, TrayMenuAction.Keep50, "50", true, c.KeepOnLine == 50),
                    new TrayMenuSpec(94, TrayMenuAction.Keep100, "100", true, c.KeepOnLine == 100),
                }),
            new TrayMenuSpec(10, TrayMenuAction.SizeMedium, "Size ▸", true, false, false, null,
                new[]
                {
                    new TrayMenuSpec(101, TrayMenuAction.SizeSmall, "Small", true, c.Size == CardSize.Small),
                    new TrayMenuSpec(102, TrayMenuAction.SizeMedium, "Medium", true, c.Size == CardSize.Medium),
                    new TrayMenuSpec(103, TrayMenuAction.SizeLarge, "Large", true, c.Size == CardSize.Large),
                }),
            new TrayMenuSpec(11, TrayMenuAction.ChangeShortcut, "Shortcut ▸", true, false, false, null,
                new[]
                {
                    new TrayMenuSpec(111, TrayMenuAction.ChangeShortcut, c.Shortcut, false),
                    new TrayMenuSpec(112, TrayMenuAction.ChangeShortcut, "Change Shortcut…"),
                }),
            new TrayMenuSpec(12, TrayMenuAction.OpenScreenshotsFolder, "Open Screenshots Folder"),
            new TrayMenuSpec(13, TrayMenuAction.Sound, "Sound", true, c.SoundOn, true),
            new TrayMenuSpec(14, TrayMenuAction.LaunchAtLogin, "Launch at Login", true, c.LaunchAtLogin),
            new TrayMenuSpec(15, TrayMenuAction.CheckForUpdates, "Check for Updates…"),
            new TrayMenuSpec(16, TrayMenuAction.Quit, "Quit ClipLine", true, false, true, "Ctrl+Q"),
        };
    }

    public static IEnumerable<TrayMenuSpec> Flatten(IEnumerable<TrayMenuSpec> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.Children is not null)
            {
                foreach (var child in Flatten(item.Children)) yield return child;
            }
        }
    }

    public static string Serialize(IEnumerable<TrayMenuSpec> items) =>
        string.Join("|", Flatten(items).Select(i =>
            $"{i.Order}:{i.Action}:{i.Key}:{(i.Enabled ? 1 : 0)}:{(i.Checked ? 1 : 0)}"));
}
