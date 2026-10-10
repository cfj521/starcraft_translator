using System.Drawing;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;

/// <summary>行级两帧共识：出现两帧才提交、缺失两帧才撤、重复行按次数、新行按帧内顺序插入。</summary>
public class LineStabilizerTests
{
    private static readonly RectangleF B1 = new(0, 10, 100, 10);
    private static readonly RectangleF B2 = new(0, 30, 100, 10);
    private static readonly RectangleF B3 = new(0, 50, 100, 10);

    private static (List<string> Lines, List<RectangleF> Bounds) Run(LineStabilizer s, params (string text, RectangleF bounds)[] frame)
    {
        var lines = frame.Select(f => f.text).ToList();
        var bounds = frame.Select(f => f.bounds).ToList();
        return s.Stabilize(0, lines, bounds);
    }

    [Fact]
    public void NewLine_PendingFirstFrame_CommitsOnSecond()
    {
        var s = new LineStabilizer();
        Assert.Empty(Run(s, ("A", B1)).Lines);                 // 首帧：挂起，不上屏
        var r = Run(s, ("A", B2));
        Assert.Equal(new[] { "A" }, r.Lines);                  // 次帧：提交，bounds 用本帧的
        Assert.Equal(B2, r.Bounds[0]);
    }

    [Fact]
    public void NoiseLine_AppearsOnce_NeverCommits()
    {
        // 背景乱行：只出现一帧，第二帧就消失 → 永远不提交（不会刷池请求、不会闪上屏）。
        var s = new LineStabilizer();
        Run(s, ("A", B1), ("N", B2));
        var r1 = Run(s, ("A", B1));
        Assert.Equal(new[] { "A" }, r1.Lines);                 // A 提交；N 噪音消失
        var r2 = Run(s, ("A", B1));
        Assert.Equal(new[] { "A" }, r2.Lines);
    }

    [Fact]
    public void StableLine_MissingOneFrame_GracedWithLastSeenBounds_MissingTwoFrames_Dropped()
    {
        // 单位从文字后面穿过：遮挡一帧不掉行（bounds 沿用最近见到），遮挡两帧才撤。
        var s = new LineStabilizer();
        Run(s, ("A", B1), ("B", B2));
        Run(s, ("A", B1), ("B", B2));                          // 都提交
        var grace = Run(s, ("A", B1));                         // B 缺失第 1 帧：宽限
        Assert.Equal(new[] { "A", "B" }, grace.Lines);
        Assert.Equal(B2, grace.Bounds[1]);                     // 位置沿用最近一次见到的
        var after = Run(s, ("A", B1));                         // B 缺失第 2 帧：撤掉
        Assert.Equal(new[] { "A" }, after.Lines);
    }

    [Fact]
    public void StableLine_ReappearsAfterGrace_StaysCommitted()
    {
        // 遮挡一帧后文字回来 → 宽限计数清零，仍是稳定行（不会再等两帧）。
        var s = new LineStabilizer();
        Run(s, ("A", B1), ("B", B2));
        Run(s, ("A", B1), ("B", B2));
        Run(s, ("A", B1));                                     // B 缺失 1 帧（宽限）
        var back = Run(s, ("A", B1), ("B", B2));
        Assert.Equal(new[] { "A", "B" }, back.Lines);          // B 直接恢复提交态
    }

    [Fact]
    public void DuplicateLines_TrackedByOccurrenceCount()
    {
        // 两条相同消息：两帧后各提交一个；其一消失（滚动出屏）→ 另一个保持提交、数量自校正。
        var s = new LineStabilizer();
        Run(s, ("gg", B1), ("gg", B2));                        // 两条挂起
        var both = Run(s, ("gg", B1), ("gg", B2));
        Assert.Equal(new[] { "gg", "gg" }, both.Lines);        // 两个都提交
        var oneGone = Run(s, ("gg", B1));                      // 第二条缺失第 1 帧：宽限
        Assert.Equal(new[] { "gg", "gg" }, oneGone.Lines);
        Assert.Equal(B1, oneGone.Bounds[0]);
        Assert.Equal(B2, oneGone.Bounds[1]);                   // 缺失那条沿用旧位置
        var dropped = Run(s, ("gg", B1));                      // 缺失第 2 帧：撤
        Assert.Equal(new[] { "gg" }, dropped.Lines);
    }

    [Fact]
    public void NewLineInsertedInMiddle_FollowsFrameOrder()
    {
        // 稳定集 [A,C]，B 连续两帧出现在 A 与 C 之间（帧内下标排序）→ 提交后按帧顺序插入中间。
        var s = new LineStabilizer();
        Run(s, ("A", B1), ("C", B3));
        Run(s, ("A", B1), ("C", B3));                          // 提交
        Run(s, ("A", B1), ("B", B2), ("C", B3));               // B 首帧：挂起
        var r = Run(s, ("A", B1), ("B", B2), ("C", B3));       // B 次帧：提交，插到 A 后 C 前
        Assert.Equal(new[] { "A", "B", "C" }, r.Lines);
        Assert.Equal(B2, r.Bounds[1]);
    }

    [Fact]
    public void NewLine_AtTop_InsertsFirst()
    {
        // 房间列表式：新行出现在最上面（帧内下标最小）→ 提交后插到首位。
        var s = new LineStabilizer();
        Run(s, ("B", B2), ("C", B3));
        Run(s, ("B", B2), ("C", B3));
        Run(s, ("A", B1), ("B", B2), ("C", B3));               // A 首帧：挂起
        var r = Run(s, ("A", B1), ("B", B2), ("C", B3));
        Assert.Equal(new[] { "A", "B", "C" }, r.Lines);
    }

    [Fact]
    public void EmptyFrame_KeepsStableLinesInGrace()
    {
        // 整个 ROI 一帧 OCR 为空（瞬时）→ 已提交行进宽限不撤；连续两帧空才清。
        var s = new LineStabilizer();
        Run(s, ("A", B1), ("B", B2));
        Run(s, ("A", B1), ("B", B2));
        var g1 = Run(s);
        Assert.Equal(new[] { "A", "B" }, g1.Lines);
        var g2 = Run(s);
        Assert.Empty(g2.Lines);
    }

    [Fact]
    public void ResetAll_ClearsConsensus()
    {
        var s = new LineStabilizer();
        Run(s, ("A", B1));
        Run(s, ("A", B1));
        Assert.Single(Run(s, ("A", B1)).Lines);
        s.ResetAll();
        Assert.Empty(Run(s, ("A", B1)).Lines);                 // 复位后重新走两帧共识
    }

    [Fact]
    public void RoisAreIndependent()
    {
        // 不同 ROI 的共识互不串扰：各自数各自的帧，ROI1 不借 ROI0 已攒的帧。
        var s = new LineStabilizer();
        Assert.Empty(s.Stabilize(0, new[] { "X" }, new[] { B1 }).Lines);   // ROI0 第 1 帧：挂起
        Assert.Single(s.Stabilize(0, new[] { "X" }, new[] { B1 }).Lines);  // ROI0 第 2 帧：提交
        Assert.Empty(s.Stabilize(1, new[] { "X" }, new[] { B1 }).Lines);   // ROI1 第 1 帧：仍挂起（不借 ROI0 的帧）
    }
}
