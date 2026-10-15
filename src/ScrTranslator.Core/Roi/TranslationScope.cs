namespace ScrTranslator.Core;

/// <summary>纯判定：某行所属列。无 UI 依赖，可单测。</summary>
public static class TranslationScope
{
    /// <summary>relX = 行在 ROI 内的水平相对位置(0..1)；返回命中列语义,无列/未命中→null。区间取 [start,end)。</summary>
    public static ColumnKind? HitColumn(IReadOnlyList<RoiColumn>? columns, double relX)
    {
        if (columns is null) return null;
        foreach (var c in columns)
            if (relX >= c.RelStart && relX < c.RelEnd) return c.Kind;
        return null;
    }
}
