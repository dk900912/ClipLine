using System;
using System.Collections.Generic;

namespace ClipLine.Domain;

/// <summary>
/// 直译 Tendedero v1.8.1 的 <c>PeggedView.swift</c> + <c>Line.swift</c>「Breeze」段：
/// 每 7~16 s 一阵随机风，逐卡延迟 0~0.35 s、角度 1.6~3.4°；
/// nudge 先 easeOut 0.3 s 摆到位，再用欠阻尼插值弹簧摆回 0。
/// 入场从 16° 摆入并从上方 46 DIP 落下，复制时 nudge 3°。
///
/// 纯状态机 + 注入时钟，不依赖 Win32 / 文件系统 / 真实时间；随机源可注入 seed，
/// 因此冒烟与单测完全确定性。渲染层只消费 <see cref="AngleFor"/> / <see cref="DropDipFor"/>。
/// </summary>
public sealed class RopeMotion
{
    public const double GustMinMs = 7000;
    public const double GustMaxMs = 16000;
    public const double BreezeDelayMaxMs = 350;
    public const double BreezeMinDeg = 1.6;
    public const double BreezeMaxDeg = 3.4;
    public const double NudgeEaseMs = 300;
    public const double ArriveSwingDeg = 16;
    public const double ArriveStiffness = 46;
    public const double ArriveDamping = 2.6;
    public const double LandSwingDeg = 2.2;
    public const double CopiedSwingDeg = 3;
    public const double NudgeStiffness = 38;
    public const double NudgeDamping = 2.4;
    public const double ArriveDropDip = 46;
    public const double ArriveResponseS = 0.42;
    public const double ArriveDampingFraction = 0.72;

    /// <summary>弹簧最长播放时长；末段线性收尾，避免长时间每帧重绘。</summary>
    public const double SpringDurationMs = 2200;
    public const double DropDurationMs = 1200;
    private const double TailMs = 250;

    private sealed class Card
    {
        public bool Active;
        public bool EaseOutFirst;
        public long T0Ms;
        public double FromDeg;
        public double ToDeg;
        public double Stiffness;
        public double Damping;
        public long ArriveMs = long.MinValue;

        public long TotalMs => (EaseOutFirst ? (long)NudgeEaseMs : 0) + (long)SpringDurationMs;
    }

    private readonly Random _rng;
    private readonly Dictionary<Guid, Card> _cards = new();
    private readonly List<(long AtMs, Guid Id, double Deg)> _pending = new();
    private long _nextGustMs = long.MinValue;
    private bool _reduceMotion;

    /// <param name="seed">0 = 用时间种子（真实运行）；非 0 = 固定种子（测试）。</param>
    public RopeMotion(int seed = 0) => _rng = seed == 0 ? new Random() : new Random(seed);

    /// <summary>系统「减少动态效果」：关闭后就地静止，不排队阵风。</summary>
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            _reduceMotion = value;
            if (!value) return;
            _pending.Clear();
            foreach (Card card in _cards.Values) card.Active = false;
        }
    }

    /// <summary>当前是否还有动画在跑；渲染层据此决定是否绕过内容指纹去重。</summary>
    public bool IsActive(long nowMs)
    {
        if (_reduceMotion) return false;
        if (_pending.Count > 0) return true;
        foreach (Card card in _cards.Values)
        {
            if (card.Active && nowMs - card.T0Ms < card.TotalMs) return true;
            if (card.ArriveMs != long.MinValue && nowMs - card.ArriveMs < DropDurationMs) return true;
        }
        return false;
    }

    /// <summary>每帧推进：同步绳上卡片、清理过期动画、派发到点的阵风、安排下一次阵风。</summary>
    public void Advance(long nowMs, IReadOnlyList<Guid> visibleIds, bool isDragging)
    {
        if (_nextGustMs == long.MinValue)
            _nextGustMs = nowMs + (long)Rand(GustMinMs, GustMaxMs);

        var live = new HashSet<Guid>(visibleIds);
        var gone = new List<Guid>();
        foreach (Guid id in _cards.Keys)
            if (!live.Contains(id)) gone.Add(id);
        foreach (Guid id in gone) _cards.Remove(id);
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (!live.Contains(_pending[i].Id)) _pending.RemoveAt(i);

        foreach (Card card in _cards.Values)
            if (card.Active && nowMs - card.T0Ms >= card.TotalMs) card.Active = false;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.AtMs > nowMs) continue;
            _pending.RemoveAt(i);
            StartNudge(p.Id, p.Deg, nowMs);
        }

        if (nowMs < _nextGustMs) return;
        // 与 Swift 一致：无论本次是否真的吹，都无条件排下一次。
        if (!_reduceMotion && !isDragging && visibleIds.Count > 0)
        {
            foreach (Guid id in visibleIds)
                _pending.Add((nowMs + (long)Rand(0, BreezeDelayMaxMs), id,
                    Rand(BreezeMinDeg, BreezeMaxDeg)));
        }
        _nextGustMs = nowMs + (long)Rand(GustMinMs, GustMaxMs);
    }

    /// <summary>新图挂上：16° 摆入 + 从上方 46 DIP 落下；飞入落地的只轻摆 2.2°。</summary>
    public void OnNewItem(Guid id, long nowMs, bool flewIn = false)
    {
        Card card = Get(id);
        card.ArriveMs = nowMs;
        if (_reduceMotion) { card.Active = false; return; }
        StartSpring(id, card,
            flewIn ? LandSwingDeg : ArriveSwingDeg,
            flewIn ? NudgeStiffness : ArriveStiffness,
            flewIn ? NudgeDamping : ArriveDamping,
            nowMs, easeOutFirst: false);
    }

    /// <summary>单击复制：nudge 3°（PeggedView.swift 的 onChange(of: copied)）。</summary>
    public void OnCopied(Guid id, long nowMs) => StartNudge(id, CopiedSwingDeg, nowMs);

    /// <summary>动态摆角，单位度；静止返回 0。静态倾角由渲染层另加。</summary>
    public double AngleFor(Guid id, long nowMs)
    {
        if (_reduceMotion || !_cards.TryGetValue(id, out Card? card) || !card.Active) return 0;
        long elapsed = nowMs - card.T0Ms;
        long springStart = card.EaseOutFirst ? (long)NudgeEaseMs : 0;
        if (elapsed < 0) return card.FromDeg;
        if (elapsed < springStart)
        {
            double t = elapsed / NudgeEaseMs;
            double eased = 1 - Math.Pow(1 - t, 3);
            return card.FromDeg + (card.ToDeg - card.FromDeg) * eased;
        }

        long st = elapsed - springStart;
        if (st >= SpringDurationMs) return 0;
        double seconds = st / 1000.0;
        double w0 = Math.Sqrt(card.Stiffness);
        double zeta = card.Damping / (2 * w0);
        double wd = w0 * Math.Sqrt(Math.Max(0, 1 - zeta * zeta));
        double x0 = card.ToDeg;
        double value = Math.Exp(-zeta * w0 * seconds) *
            (x0 * Math.Cos(wd * seconds) + (zeta * w0 * x0 / wd) * Math.Sin(wd * seconds));
        return value * TailFactor(st, SpringDurationMs);
    }

    /// <summary>入场落下偏移，负值表示还在屏幕上方；播放完返回 0。</summary>
    public double DropDipFor(Guid id, long nowMs)
    {
        if (_reduceMotion || !_cards.TryGetValue(id, out Card? card)) return 0;
        if (card.ArriveMs == long.MinValue) return 0;
        long elapsed = nowMs - card.ArriveMs;
        if (elapsed < 0 || elapsed >= DropDurationMs) return 0;
        double t = elapsed / 1000.0;
        double w0 = 2 * Math.PI / ArriveResponseS;
        double zeta = ArriveDampingFraction;
        double wd = w0 * Math.Sqrt(1 - zeta * zeta);
        double decay = Math.Exp(-zeta * w0 * t) *
            (Math.Cos(wd * t) + (zeta * w0 / wd) * Math.Sin(wd * t));
        return -ArriveDropDip * decay * TailFactor(elapsed, DropDurationMs);
    }

    private void StartNudge(Guid id, double degrees, long nowMs)
    {
        Card card = Get(id);
        if (_reduceMotion) { card.Active = false; return; }
        StartSpring(id, card, degrees, NudgeStiffness, NudgeDamping, nowMs, easeOutFirst: true);
    }

    private void StartSpring(Guid id, Card card, double degrees, double stiffness, double damping,
        long nowMs, bool easeOutFirst)
    {
        // 上一次摆动还没停时从当前角接着推，避免突然跳回 0（Swift 的 withAnimation 也是叠加）。
        double from = easeOutFirst ? AngleFor(id, nowMs) : 0;
        card.Stiffness = stiffness;
        card.Damping = damping;
        card.T0Ms = nowMs;
        card.FromDeg = from;
        card.ToDeg = degrees;
        card.EaseOutFirst = easeOutFirst;
        card.Active = true;
    }

    private Card Get(Guid id)
    {
        if (!_cards.TryGetValue(id, out Card? card))
        {
            card = new Card();
            _cards[id] = card;
        }
        return card;
    }

    private static double TailFactor(double elapsedMs, double durationMs)
    {
        double left = durationMs - elapsedMs;
        return left >= TailMs ? 1 : Math.Max(0, left / TailMs);
    }

    private double Rand(double min, double max) => min + _rng.NextDouble() * (max - min);
}
