using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>P3：手势全集 / 右键菜单 / 拖放语义 / 键盘借用 / 热键。</summary>
public sealed class P3Tests : ITestSuite
{
    private static (Fixture F, Guid Id) One(bool recording = false, bool byHand = false, bool ownDirectory = true)
    {
        var f = new Fixture();
        f.Start();
        f.Send(new SetPeekOnNewContent(false));
        f.Send(new NewItem(Fixture.P(1), IsRecording: recording, ByHand: byHand, InOwnDirectory: ownDirectory));
        return (f, f.State.Items[0].Id);
    }

    public void Register(TestRunner t)
    {
        t.Case("G-01", "单击延迟一个双击间隔才复制，徽标 1.2 秒消失", () =>
        {
            var (f, id) = One();
            f.Send(new CardPointerDown(id, 40, 40));
            var up = f.Send(new CardPointerUp(id, 40, 40));
            Check.False(up.Has(CommandKind.WriteClipboardImage), "未到双击间隔不复制");
            f.Advance(449);
            Check.False(f.Sink.Has(CommandKind.ShowCopiedBadge), "449 ms 仍等待");
            var click = f.Advance(51);
            Check.True(click.Has(CommandKind.WriteClipboardImage), "500 ms 后执行复制");
            Check.True(click.Has(CommandKind.ShowCopiedBadge), "显示「已复制」");
            var badge = f.Advance(1200);
            Check.True(badge.Has(CommandKind.HideCopiedBadge), "1.2 秒后徽标消失");
        });

        t.Case("G-02", "双击打开系统默认应用且不误触发复制", () =>
        {
            var (f, id) = One();
            f.Send(new CardPointerDown(id, 40, 40));
            f.Send(new CardPointerUp(id, 40, 40));
            var second = f.Send(new CardPointerDown(id, 40, 40));
            Check.True(second.Has(CommandKind.OpenDefaultApp), "双击打开");
            var later = f.Advance(1000);
            Check.False(later.Has(CommandKind.WriteClipboardImage), "不再执行单击复制");
        });

        t.Case("G-03", "长按 0.45 秒进标记，录屏改剪辑", () =>
        {
            var (f, id) = One();
            f.Send(new CardPointerDown(id, 40, 40));
            var before = f.Advance(449);
            Check.False(before.Has(CommandKind.ShowMarkupEditor), "449 ms 不触发");
            var at = f.Advance(1);
            Check.True(at.Has(CommandKind.ShowMarkupEditor), "450 ms 触发标记");

            var (vf, vid) = One(recording: true);
            vf.Send(new CardPointerDown(vid, 40, 40));
            var trim = vf.Advance(450);
            Check.True(trim.Has(CommandKind.ShowTrimEditor), "录屏走剪辑编辑器");
            Check.False(trim.Has(CommandKind.ShowMarkupEditor), "录屏不走标记");
        });

        t.Case("G-06", "拖拽阈值 4 pt 与取消后状态复位", () =>
        {
            var (f, id) = One();
            f.Send(new CardPointerDown(id, 40, 40));
            var small = f.Send(new CardPointerMove(id, 3, 0));
            Check.False(small.Has(CommandKind.SetDragPreview), "3 pt 不触发");
            var big = f.Send(new CardPointerMove(id, 5, 0));
            Check.True(big.Has(CommandKind.SetDragPreview), "5 pt 触发拖拽");
            Check.True(f.State.IsDragging, "线不收起");
            f.Send(new CardPointerUp(id, 45, 40));
            Check.False(f.State.IsDragging, "松开复位");
        });

        t.Case("V-16", "角标命中区 26×26", () =>
        {
            Check.True(GestureThresholds.IsCornerHit(0, 0), "左上原点");
            Check.True(GestureThresholds.IsCornerHit(26, 26), "边界含");
            Check.False(GestureThresholds.IsCornerHit(27, 10), "超出");
            Check.Equal(0.95, GestureThresholds.PressScale, "按下缩到 0.95");
            Check.Equal(1.035, GestureThresholds.HoverScale, "悬停放大 1.035");
        });

        t.Case("G-05", "角标进回收站并可由撤销把文件移回", () =>
        {
            var (f, id) = One();
            var r = f.Send(new CornerClicked(id));
            Check.True(r.Has(CommandKind.MoveToRecycleBin), "自有目录进回收站");
            Check.Equal(0, f.State.LiveCount, "卡片下线");
            var back = f.Send(new PutBack());
            Check.Equal(1, f.State.LiveCount, "放回绳上");
            Check.Contains("RecycleBin:", back.Arg(CommandKind.MoveFile), "软删除索引让文件移回原目录");
        });

        t.Case("G-11", "右键菜单基础项与录屏条件显隐", () =>
        {
            var line = new Line(1280);
            var normal = new Pegged { Path = "a.png" };
            line.Hang(normal);
            var items = ContextMenuModel.Build(normal, line);
            var actions = items.Select(i => i.Action).ToHashSet();
            Check.True(actions.Contains(ContextAction.Copy), "复制");
            Check.True(actions.Contains(ContextAction.Open), "打开");
            Check.True(actions.Contains(ContextAction.QuickLook), "快速查看");
            Check.True(actions.Contains(ContextAction.Hold), "拿在手上");
            Check.True(actions.Contains(ContextAction.Markup), "标记");

            var recording = new Pegged { Path = "b.mp4", IsRecording = true };
            line.Hang(recording);
            var videoItems = ContextMenuModel.Build(recording, line).Select(i => i.Action).ToHashSet();
            Check.False(videoItems.Contains(ContextAction.Hold), "录屏不显示拿在手上");
            Check.False(videoItems.Contains(ContextAction.Markup), "录屏不显示标记");
        });

        t.Case("G-14", "固定 / 取消固定 / 手动挂上的菜单差异", () =>
        {
            var line = new Line(1280);
            var plain = new Pegged { Path = "a.png" };
            var pinned = new Pegged { Path = "b.png", Kept = true };
            var byHand = new Pegged { Path = "c.png", Kept = true, ByHand = true };
            line.Hang(plain); line.Hang(pinned); line.Hang(byHand);

            var a = ContextMenuModel.Build(plain, line).Select(i => i.Action).ToHashSet();
            Check.True(a.Contains(ContextAction.Pin), "未固定显示固定");
            var b = ContextMenuModel.Build(pinned, line).Select(i => i.Action).ToHashSet();
            Check.True(b.Contains(ContextAction.Unpin), "已固定显示取消固定");
            var c = ContextMenuModel.Build(byHand, line).Select(i => i.Action).ToHashSet();
            Check.False(c.Contains(ContextAction.Unpin), "手动挂上不显示取消固定");
            Check.True(c.Contains(ContextAction.TakeDown), "手动挂上显示取下");
            Check.False(c.Contains(ContextAction.Discard), "手动挂上绝不进回收站");
        });

        t.Case("G-18", "菜单打开期间线保持展开", () =>
        {
            var (f, id) = One();
            var r = f.Send(new ContextMenuRequested(id));
            Check.True(r.Has(CommandKind.ShowContextMenu), "发出菜单命令");
            Check.Contains("Copy", r.Arg(CommandKind.ShowContextMenu), "菜单携带本地化键");
            Check.True(f.State.IsShowingMenu, "状态标记使 CanHide 为假");
        });

        t.Case("G-17", "取下左侧邻居一次批量算一步撤销", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            for (int i = 1; i <= 5; i++) f.Send(new NewItem(Fixture.P(i)));
            var middle = f.State.Items[2].Id;
            f.Send(new ContextMenuItem(middle, ContextAction.TakeDownLeft));
            Check.Equal(3, f.State.LiveCount, "左侧 2 张一起取下");
            Check.Equal(1, f.State.UndoCount, "只算一步");
        });

        t.Case("G-07", "拖进应用送副本，原图仍在绳上", () =>
        {
            var (f, id) = One();
            var r = f.Send(new DragDropped(id, DropTargetKind.Application, "SomeApp"));
            Check.True(r.Has(CommandKind.CopyFile), "送副本");
            Check.Equal(1, f.State.LiveCount, "原图仍在绳上");
        });

        t.Case("G-08", "拖进文件夹移动并下线，0.6 秒后复查", () =>
        {
            var (f, id) = One();
            var r = f.Send(new DragDropped(id, DropTargetKind.Folder, @"D:\inbox"));
            Check.True(r.Has(CommandKind.MoveFile), "移动文件");
            Check.True(r.Has(CommandKind.UpdateClipboardFileReference), "剪贴板引用跟随（D-08）");
            Check.Equal(0, f.State.LiveCount, "从绳上取下");
            var recheck = f.Advance(600);
            Check.True(recheck.Has(CommandKind.RecheckDrop), "0.6 秒后复查落点");
        });

        t.Case("G-09", "拖到回收站由应用自己删", () =>
        {
            var (f, id) = One();
            var r = f.Send(new DragDropped(id, DropTargetKind.RecycleBin));
            Check.True(r.Has(CommandKind.MoveToRecycleBin), "应用负责删除");
            Check.Equal(0, f.State.LiveCount, "卡片下线");
            Check.True(DragDropSemantics.Resolve(DropTargetKind.RecycleBin).AppDeletesFile, "语义标记");
        });

        t.Case("G-10", "拖拽源只对外暴露 copy/move/delete", () =>
        {
            Check.Equal(3, DragDropSemantics.ExposedEffects.Length, "三种效果");
            Check.True(DragDropSemantics.IsExternalOnly, "仅应用外上下文");
            var none = DragDropSemantics.Resolve(DropTargetKind.None);
            Check.Equal(DropEffect.None, none.Effect, "空放无效果");
            Check.False(none.RemoveFromLine, "卡片飞回原位");
        });

        t.Case("K-03", "空格 / Delete / Esc / Ctrl+C 的临时借用", () =>
        {
            var (f, id) = One();
            var quick = f.Send(new KeyBorrow(CardKey.Space, id));
            Check.True(quick.Has(CommandKind.ShowQuickLook), "空格打开快速查看");
            var closed = f.Send(new KeyBorrow(CardKey.Space, id));
            Check.True(closed.Has(CommandKind.CloseQuickLook), "再按关闭");
            var copy = f.Send(new KeyBorrow(CardKey.Copy, id));
            Check.True(copy.Has(CommandKind.WriteClipboardImage), "Ctrl+C 复制");
            var del = f.Send(new KeyBorrow(CardKey.Delete, id));
            Check.True(del.Has(CommandKind.MoveToRecycleBin), "Delete 等同角标（卡片随后下线）");
            var esc = f.Send(new KeyBorrow(CardKey.Escape, id));
            Check.True(esc.Has(CommandKind.CloseHeldWindow), "Esc 放回");
        });

        t.Case("K-01", "快捷键录制规则：需修饰键 / Delete 清空 / Esc 取消 / 冲突回滚", () =>
        {
            var f = new Fixture();
            f.Start();
            Check.True(f.Send(new ShortcutRecorded("T", "T")).Has(CommandKind.Beep), "缺修饰键蜂鸣");
            var ok = f.Send(new ShortcutRecorded("Ctrl+Alt+S", "S"));
            Check.True(ok.Has(CommandKind.UnregisterHotKey), "先注销旧值");
            Check.True(ok.Has(CommandKind.RegisterHotKey), "注册新值");
            Check.Equal("Ctrl+Alt+S", f.State.Shortcut, "设置生效");

            var cleared = f.Send(new ShortcutRecorded(string.Empty, "Delete"));
            Check.Equal("无", f.State.Shortcut, "Delete 清空为「无」");
            Check.True(cleared.Has(CommandKind.UnregisterHotKey), "注销");

            f.Send(new ShortcutRecorded("Ctrl+Alt+T", "T"));
            string current = f.State.Shortcut;
            f.Send(new ShortcutRecorded("Ctrl+Shift+U", "U"));
            var conflict = f.Send(new HotKeyConflict("Ctrl+Shift+U"));
            Check.True(conflict.Has(CommandKind.Beep), "被占用时蜂鸣");
            Check.True(conflict.Has(CommandKind.RollbackShortcut), "回滚");
            Check.Equal(current, f.State.Shortcut, "回到旧值");

            string beforeEsc = f.State.Shortcut;
            var esc = f.Send(new ShortcutRecorded("Ctrl+X", "Escape"));
            Check.Equal(beforeEsc, f.State.Shortcut, "Esc 取消不改值");
            Check.Equal(0, esc.Commands.Count, "Esc 无副作用");
        });

        t.Case("K-06", "⌘→Ctrl、⌥→Alt 的 Windows 映射", () =>
        {
            Check.Equal("Ctrl+Z", HotKeyModel.MacToWindows("⌘Z"), "⌘");
            Check.Equal("Alt+Z", HotKeyModel.MacToWindows("⌥Z"), "⌥");
            Check.Equal("Ctrl+Alt+T", HotKeyModel.MacToWindows("⌃⌥T"), "⌃⌥T");
        });

        t.Case("N-01", "力感应点按的等价替代结果对齐", () =>
        {
            Check.Equal(3, HotKeyModel.ForceClickEquivalents.Length, "中键 / 笔压 / 长按");
            var (f, id) = One();
            var r = f.Send(new ForceClickEquivalent(id));
            Check.True(r.Has(CommandKind.ShowQuickLook), "结果同为快速查看");
        });

        t.Case("D-08", "存储到桌面后剪贴板文件引用跟随更新", () =>
        {
            var (f, id) = One();
            var r = f.Send(new ContextMenuItem(id, ContextAction.StoreToDesktop));
            Check.True(r.Has(CommandKind.MoveFile), "移动文件");
            Check.True(r.Has(CommandKind.UpdateClipboardFileReference), "更新引用");
        });

        t.Case("G-16", "其他位置文件的菜单为取下 + 移到回收站", () =>
        {
            var line = new Line(1280);
            var external = new Pegged { Path = @"D:\a.png", InOwnDirectory = false };
            line.Hang(external);
            var actions = ContextMenuModel.Build(external, line).Select(i => i.Action).ToHashSet();
            Check.True(actions.Contains(ContextAction.TakeDown), "取下");
            Check.True(actions.Contains(ContextAction.MoveToRecycleBin), "移到回收站");
            Check.False(actions.Contains(ContextAction.StoreToDesktop), "非自有目录不显示存储到桌面");
        });
    }
}
