using System.Drawing;
using System.Linq;
namespace ScrTranslator.Core.Translation;

/// <summary>OCR 行级两帧共识（动态背景降噪，如 ingame 聊天浮在游戏画面上）：
/// 背景噪音是瞬时的（单位/特效穿过一两帧就过去），聊天/列表文字是持久的。
/// 一行**连续两帧以相同文本出现**才提交进下游管线（变化检测/翻译/上屏）；
/// 已提交的行允许**连续缺失两帧**（宽限，单位从文字后面穿过遮挡它），
/// 宽限期间位置沿用最近一次见到的 bounds。
/// 重复行（两条相同消息）按**出现次数**跟踪、不合并：k 次出现对应 k 个条目，
/// 聊天滚动导致 y 坐标整体变化时按出现次序对齐、数量自校正，共识不被滚动打断。
/// 一个 probe 一个实例，按 ROI 下标分状态；ROI 集变化（换场景）时调 <see cref="ResetAll"/>。</summary>
public sealed class LineStabilizer
{
    private sealed class Roi
    {
        // 已提交行（有序）：文本 + 最近一次见到的 bounds + 最近一次见到时的帧内下标（插入排序用）。
        public List<(string Text, RectangleF Bounds, int LastFrameIdx)> Stable = new();
        // 上一帧出现过、尚未提交（等次帧确认）的出现次数，按文本。
        public Dictionary<string, int> Pending = new();
        // 已提交文本连续缺失的帧数（>= DropAfterMissing 时撤掉该文本所有缺失出现）。
        public Dictionary<string, int> MissingFrames = new();
    }

    private readonly Dictionary<int, Roi> _byRoi = new();
    private readonly object _gate = new(); // Stabilize/ResetAll 可能来自不同线程（采集线程 vs 切场景）
    private const int DropAfterMissing = 2; // 缺失两帧才撤行（遮挡一两帧不掉）

    /// <summary>给定本帧候选行（已过置信度/韩文/翻译范围闸门，按 OCR 顺序），返回已提交的稳定行及其 bounds。
    /// 挂起行（首帧见到）不在返回值里；宽限行（本帧缺失但仍在宽限内）带最近 bounds 返回。</summary>
    public (List<string> Lines, List<RectangleF> Bounds) Stabilize(
        int roiIndex, IReadOnlyList<string> frameLines, IReadOnlyList<RectangleF> frameBounds)
    {
        lock (_gate) return StabilizeLocked(roiIndex, frameLines, frameBounds);
    }

    private (List<string> Lines, List<RectangleF> Bounds) StabilizeLocked(
        int roiIndex, IReadOnlyList<string> frameLines, IReadOnlyList<RectangleF> frameBounds)
    {
        var roi = GetOrCreate(roiIndex);
        int n = frameLines.Count;

        // 本帧每个文本的出现次数与各次出现的帧内下标（出现次序对齐用）。
        var frameCount = new Dictionary<string, int>();
        var frameOccIdx = new Dictionary<string, List<int>>();
        for (int i = 0; i < n; i++)
        {
            string t = frameLines[i];
            frameCount[t] = frameCount.GetValueOrDefault(t) + 1;
            if (!frameOccIdx.TryGetValue(t, out var li)) frameOccIdx[t] = li = new List<int>();
            li.Add(i);
        }

        // 按旧稳定顺序走。**按位置认行而非精确文本**：OCR 对同一行会因渲染/背景抖动读出不同文本
        // （同一位置、文本略变），若按精确文本匹配，旧读法被判缺失、新读法还没凑够两帧 → 整行撤销
        // 约 800ms 再重现（周期性闪烁）。改为：本帧里与该行位置重叠、且未被其它已提交行认领的读法
        // = 该行仍在（文本更新为本帧最新读法）；找不到同位置读法才算缺失。一帧内一行只占一个位置，
        // 故一对一认领即可，重复行（同文不同位置）各认各的、不串扰。
        var kept = new List<(string Text, RectangleF Bounds, int LastFrameIdx, bool Missing)>();
        var confirmedSoFar = new Dictionary<string, int>();
        var missingNow = new Dictionary<string, int>();
        var matchedReading = new HashSet<int>(); // 已被某已提交行认领的本帧读法下标
        foreach (var s in roi.Stable)
        {
            int idx = -1;
            for (int i = 0; i < n; i++)
            {
                if (matchedReading.Contains(i)) continue;
                if (Overlaps(s.Bounds, frameBounds[i])) { idx = i; break; }
            }
            if (idx >= 0)
            {
                matchedReading.Add(idx);
                string readingText = frameLines[idx]; // 该行本帧最新读法（可能与上次不同）
                confirmedSoFar[readingText] = confirmedSoFar.GetValueOrDefault(readingText) + 1;
                kept.Add((readingText, frameBounds[idx], idx, false));
            }
            else
            {
                missingNow[s.Text] = missingNow.GetValueOrDefault(s.Text) + 1;
                kept.Add((s.Text, s.Bounds, s.LastFrameIdx, true)); // 暂留，裁决见下
            }
        }

        // 缺失裁决（每文本每帧只 +1，不管缺几次）：连续缺失满 2 帧 → 撤掉该文本所有缺失出现；
        // 本帧有确认的文本 → 缺失计数归零。
        var dropAllMissing = new HashSet<string>();
        foreach (var kv in missingNow)
        {
            int frames = roi.MissingFrames.GetValueOrDefault(kv.Key) + 1;
            roi.MissingFrames[kv.Key] = frames;
            if (frames >= DropAfterMissing) dropAllMissing.Add(kv.Key);
        }

        var newStable = new List<(string, RectangleF, int)>();
        foreach (var k in kept)
            if (!k.Missing || !dropAllMissing.Contains(k.Text)) newStable.Add((k.Text, k.Bounds, k.LastFrameIdx));

        // 计数表维护：本帧确认过的文本归零（删键＝0），已不在稳定集里的文本立刻退场。
        // 旧实现只对"本帧仍在"的键清零、且从不 Remove——每个曾出现过的文本（含 OCR 抖动的不同读法）
        // 都永久留一条，长会话里字典单调增长。
        var stableTexts = new HashSet<string>(newStable.Count);
        foreach (var s in newStable) stableTexts.Add(s.Item1);
        foreach (var t in frameCount.Keys)
            if (!missingNow.ContainsKey(t)) roi.MissingFrames.Remove(t);
        if (roi.MissingFrames.Count > 0)
            foreach (var t in roi.MissingFrames.Keys.ToList())
                if (!stableTexts.Contains(t)) roi.MissingFrames.Remove(t);

        // 提升：本帧里超出"确认数"的出现，上一帧有挂起同文 → 凑满两帧，转正；
        // 插到"帧内下标比它小的已保留行"的最后一条之后（下标相等不可能：同帧内一行只占一个下标）。
        var newPending = new Dictionary<string, int>();
        foreach (var kv in frameCount)
        {
            string t = kv.Key;
            int confirmed = confirmedSoFar.GetValueOrDefault(t);
            int promote = Math.Min(roi.Pending.GetValueOrDefault(t), kv.Value - confirmed);
            int promoted = 0;
            foreach (int idx in frameOccIdx[t])
            {
                if (promoted >= promote) break;
                if (matchedReading.Contains(idx)) continue; // 已被某已提交行认领的读法不能再被提升
                int at = newStable.FindLastIndex(x => x.Item3 < idx);
                newStable.Insert(at < 0 ? 0 : at + 1, (t, frameBounds[idx], idx));
                matchedReading.Add(idx); // 防止同一读法被重复提升
                promoted++;
            }
            int rest = kv.Value - confirmed - promoted;
            if (rest > 0) newPending[t] = rest; // 首帧见到：挂起（挂起行本帧没再出现＝噪音，随 newPending 自然丢弃）
        }
        roi.Pending = newPending;
        roi.Stable = newStable;

        var lines = new List<string>(newStable.Count);
        var bounds = new List<RectangleF>(newStable.Count);
        foreach (var s in newStable) { lines.Add(s.Item1); bounds.Add(s.Item2); }
        return (lines, bounds);
    }

    /// <summary>两行矩形是否"同一行"：竖直范围重叠（含半行高余量，吸收 OCR 边界上下抖动）且水平范围相交。
    /// 用位置而非文本认行：OCR 对同一行会读出略不同的文本，但位置稳定。</summary>
    private static bool Overlaps(RectangleF a, RectangleF b)
    {
        double margin = Math.Max(a.Height, b.Height) * 0.5;
        bool vertical = a.Top < b.Bottom + margin && b.Top < a.Bottom + margin;
        bool horizontal = a.Left < b.Right && b.Left < a.Right;
        return vertical && horizontal;
    }

    /// <summary>ROI 集变化（换场景/分辨率/增删）：清掉全部 ROI 的共识状态。</summary>
    public void ResetAll()
    {
        lock (_gate) _byRoi.Clear();
    }

    private Roi GetOrCreate(int roiIndex) =>
        _byRoi.TryGetValue(roiIndex, out var r) ? r : _byRoi[roiIndex] = new Roi();
}
