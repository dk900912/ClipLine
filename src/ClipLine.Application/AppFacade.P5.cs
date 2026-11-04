using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>P5：16 项菜单 / 语言 / 主题，经同一门面复用前四阶段已测输入。</summary>
public sealed partial class AppFacade
{
    private void OnTrayMenuOpening(TrayMenuOpening e)
    {
        var context = new TrayMenuContext(
            _line.LiveCount,
            _line.Undo.CanUndo,
            _line.HeldCount > 0,
            _settings.InboxEnabled,
            _settings.ClipboardEnabled,
            _settings.RevealOnPointer,
            _settings.PeekOnNewContent,
            _settings.ShowOverFullScreen,
            _settings.KeepOnLine,
            _settings.LineSize,
            _settings.SoundOn,
            _settings.LaunchAtLogin,
            _settings.ExtraFolders.ToArray(),
            _settings.Shortcut,
            _isRevealed);
        Emit(CommandKind.ShowTrayMenu, TrayMenuModel.Serialize(TrayMenuModel.Build(context)));
        UpdateTray(); // T-04：项目数变化时即时刷新图标状态
    }

    private void OnTrayMenuItemInvoked(TrayMenuItemInvoked e)
    {
        switch (e.Action)
        {
            case TrayMenuAction.ToggleLine: Dispatch(new ToggleLine()); break;
            case TrayMenuAction.TakeDownAll: Dispatch(new TakeDownAll()); break;
            case TrayMenuAction.PutBack: Dispatch(new PutBack()); break;
            case TrayMenuAction.HangEverythingBack: Dispatch(new HangEverythingBack()); break;
            case TrayMenuAction.ToggleInbox:
                Dispatch(new SetInboxEnabled(!_settings.InboxEnabled)); break;
            case TrayMenuAction.ToggleClipboard:
                Dispatch(new SetClipboardEnabled(!_settings.ClipboardEnabled)); break;
            case TrayMenuAction.RevealOnPointer:
                Dispatch(new SetRevealOnPointer(!_settings.RevealOnPointer)); break;
            case TrayMenuAction.PeekOnNewContent:
                Dispatch(new SetPeekOnNewContent(!_settings.PeekOnNewContent)); break;
            case TrayMenuAction.ShowOverFullScreen:
                Dispatch(new SetShowOverFullScreen(!_settings.ShowOverFullScreen)); break;
            case TrayMenuAction.AddFolder:
                if (string.IsNullOrEmpty(e.Argument)) Emit(CommandKind.ShowFolderPicker);
                else Dispatch(new AddWatchedFolder(e.Argument));
                break;
            case TrayMenuAction.StopWatching:
                if (!string.IsNullOrEmpty(e.Argument)) Dispatch(new RemoveWatchedFolder(e.Argument));
                break;
            case TrayMenuAction.WatchFolder:
                if (!string.IsNullOrEmpty(e.Argument)) Emit(CommandKind.OpenFolder, e.Argument);
                break;
            case TrayMenuAction.KeepFits: Dispatch(new SetKeepOnLine(0)); break;
            case TrayMenuAction.Keep25: Dispatch(new SetKeepOnLine(25)); break;
            case TrayMenuAction.Keep50: Dispatch(new SetKeepOnLine(50)); break;
            case TrayMenuAction.Keep100: Dispatch(new SetKeepOnLine(100)); break;
            case TrayMenuAction.SizeSmall: Dispatch(new SetSize(CardSize.Small)); break;
            case TrayMenuAction.SizeMedium: Dispatch(new SetSize(CardSize.Medium)); break;
            case TrayMenuAction.SizeLarge: Dispatch(new SetSize(CardSize.Large)); break;
            case TrayMenuAction.ChangeShortcut: Emit(CommandKind.ShowShortcutRecorder); break;
            case TrayMenuAction.OpenScreenshotsFolder: Dispatch(new OpenScreenshotsFolder()); break;
            case TrayMenuAction.Sound: Dispatch(new SetSound(!_settings.SoundOn)); break;
            case TrayMenuAction.LaunchAtLogin:
                Dispatch(new SetLaunchAtLogin(!_settings.LaunchAtLogin)); break;
            case TrayMenuAction.CheckForUpdates: Dispatch(new CheckForUpdates()); break;
            case TrayMenuAction.Quit: Dispatch(new ApplicationExiting()); break;
        }
    }

    private void OnThemeChanged(ThemeChanged e)
    {
        var palette = ThemeTokens.For(e.Mode);
        Emit(CommandKind.SetTheme, e.Mode + "|" + palette.CardGlass);
        Emit(CommandKind.SetReduceMotion, e.ReduceMotion ? "1" : "0");
    }

    private void OnSetLanguage(SetLanguage e)
    {
        if (!Localization.IsSupported(e.Language))
        {
            Emit(CommandKind.Beep);
            return;
        }
        _settings.Language = e.Language;
        Emit(CommandKind.SetLanguageText, e.Language);
        Persist();
    }

    private void OnLocalizationQuery(LocalizationQuery e) =>
        Emit(CommandKind.ApplyLocalizedText, e.Key + "=" + Localization.Get(e.Key, _settings.Language));
}
