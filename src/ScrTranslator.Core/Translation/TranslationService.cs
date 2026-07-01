namespace ScrTranslator.Core.Translation;
public sealed class TranslationService
{
    private readonly Glossary _glossary; private readonly TranslationCache _cache; private readonly Func<ITranslationProvider> _active;
    public TranslationService(Glossary glossary, TranslationCache cache, Func<ITranslationProvider> activeProvider)
    { _glossary = glossary; _cache = cache; _active = activeProvider; }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> koreanLines, CancellationToken ct)
    {
        var result = new string[koreanLines.Count];
        var missIdx = new List<int>(); var missText = new List<string>();
        for (int i = 0; i < koreanLines.Count; i++)
        {
            var ko = koreanLines[i];
            if (_glossary.TryGet(ko, out var g)) result[i] = g;
            else if (_cache.TryGet(ko, out var c)) result[i] = c;
            else { missIdx.Add(i); missText.Add(ko); }
        }
        if (missText.Count > 0)
        {
            IReadOnlyList<string> translated;
            try { translated = await _active().TranslateAsync(missText, ct); }
            catch { translated = missText; } // 降级回原文
            for (int j = 0; j < missIdx.Count; j++)
            {
                var zh = j < translated.Count ? translated[j] : missText[j];
                result[missIdx[j]] = zh;
                // 仅跳过降级回原文(引用相等)的情况；合法译文即使与原文文本相同也缓存，避免反复请求
                if (!ReferenceEquals(zh, missText[j])) _cache.Set(missText[j], zh);
            }
        }
        return result;
    }
}
