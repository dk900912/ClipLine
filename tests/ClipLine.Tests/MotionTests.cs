using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>
/// v1.1 动效：RopeMotion 的阵风调度、入场摆入、nudge 欠阻尼回弹、减少动效。
/// 全部用注入的假时间推进，不依赖真实时钟 / Win32 / 文件系统。
/// </summary>
public sealed class MotionTests : ITestSuite
{
    private static readonly Guid A = new(1, 0, 0, new byte[8]);
    private static readonly Guid B = new(2, 0, 0, new byte[8]);
    private static readonly Guid[] Both = { A, B };
    private static readonly Guid[] None = Array.Empty<Guid>();

    public void Register(TestRunner t)
    {
        t.Case("M-01", "阵风 7~16 s 随机触发并逐卡排队", () =>
        {
            var m = new RopeMotion(seed: 7);
            m.Advance(0, Both, false);
            Check.False(m.IsActive(0), "初始静止，不重绘");
            m.Advance(20_000, Both, false); // 必然越过第一次排程
            Check.True(m.IsActive(20_000), "阵风已排队（含逐卡 0~0.35 s 延迟）");
        });

        t.Case("M-02", "阵风派发后卡片产生摆角并自行收敛", () =>
        {
            var m = new RopeMotion(seed: 7);
            m.Advance(0, Both, false);
            m.Advance(20_000, Both, false);
            m.Advance(20_400, Both, false); // 所有待派发的 breeze 到点
            m.Advance(20_550, Both, false);
            Check.True(Math.Abs(m.AngleFor(A, 20_550)) > 0.01, "easeOut 段已有摆角");
            Check.True(m.IsActive(20_550), "动画期间保持活跃");
            m.Advance(23_500, Both, false);
            Check.False(m.IsActive(23_500), "播放结束后不再重绘");
            Check.Near(0, m.AngleFor(A, 23_500), 0.0001, "角度归零");
        });

        t.Case("M-03", "入场 16° 摆入 + 上方 46 DIP 落下", () =>
        {
            var m = new RopeMotion(seed: 11);
            m.OnNewItem(A, 1_000);
            Check.Near(16, m.AngleFor(A, 1_000), 0.001, "起始摆角 16°");
            Check.Near(-46, m.DropDipFor(A, 1_000), 0.001, "起始位于上方 46 DIP");
            Check.True(m.IsActive(1_200), "落下 + 摆动期间活跃");
            Check.Near(0, m.DropDipFor(A, 2_300), 0.001, "1.2 s 后落到位");
            Check.Near(0, m.AngleFor(A, 3_300), 0.001, "2.2 s 后摆停");
        });

        t.Case("M-04", "复制 nudge 3°：先 easeOut 后欠阻尼过冲", () =>
        {
            var m = new RopeMotion(seed: 13);
            m.Advance(0, Both, false);
            m.OnCopied(A, 500);
            Check.Near(0, m.AngleFor(A, 500), 0.001, "easeOut 起点");
            Check.Near(3, m.AngleFor(A, 800), 0.01, "0.3 s 到达 3°");
            Check.True(m.AngleFor(A, 1_330) < -0.1, "欠阻尼弹簧反向过冲");
        });

        t.Case("M-05", "空绳 / 拖拽中不触发阵风", () =>
        {
            var empty = new RopeMotion(seed: 17);
            empty.Advance(0, None, false);
            empty.Advance(20_000, None, false);
            Check.False(empty.IsActive(20_000), "绳上无图不吹");

            var dragging = new RopeMotion(seed: 17);
            dragging.Advance(0, Both, false);
            dragging.Advance(20_000, Both, isDragging: true);
            Check.False(dragging.IsActive(20_000), "拖拽时不吹");
        });

        t.Case("M-06", "减少动态效果时整体静止", () =>
        {
            var m = new RopeMotion(seed: 19) { ReduceMotion = true };
            m.Advance(0, Both, false);
            m.OnNewItem(A, 100);
            m.OnCopied(A, 200);
            m.Advance(20_000, Both, false);
            Check.Near(0, m.AngleFor(A, 200), 0.0001, "无摆角");
            Check.Near(0, m.DropDipFor(A, 200), 0.0001, "无落下");
            Check.False(m.IsActive(20_000), "不排队阵风");
        });

        t.Case("M-07", "卡片下线后摆动状态被清理", () =>
        {
            var m = new RopeMotion(seed: 23);
            m.Advance(0, Both, false);
            m.OnNewItem(A, 100);
            Check.True(m.IsActive(200), "先在动");
            m.Advance(8_000, new[] { B }, false); // A 已从绳上取下
            Check.Near(0, m.AngleFor(A, 8_000), 0.0001, "A 的状态已移除");
        });
    }
}
