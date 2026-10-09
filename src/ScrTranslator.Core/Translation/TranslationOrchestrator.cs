namespace ScrTranslator.Core.Translation;

/// <summary>一批翻译的结果。<see cref="Texts"/> 与传入的韩文行**按下标一一对应**。</summary>
/// <param name="AllResolved">本批每一行都真翻出来了。false 时未翻成的行在 <see cref="Texts"/> 里仍是原文，
/// 调用方**必须**按"文本==原文"过滤掉，否则原文会被当译文显示出去。</param>
/// <param name="Error">provider 失败原因（null=没失败）。</param>
public sealed record TranslateOutcome(IReadOnlyList<string> Texts, bool AllResolved, string? Error);

/// <summary>
/// 翻译编排：**术语表 → 缓存 → 当前活跃 provider**，单级，一批一次请求。
///
/// 为什么没有"两级"了：曾经的设计是快引擎先出临时译文（灰斜体）→ 主力引擎再原地替换。
/// 但这是个很窄的任务（游戏聊天/房间名短句），低价模型单发就够好，两级只让每个未命中批次多打一次请求、
/// 并多出一整套"临时态"的视觉与状态分支 —— 复杂度换来的收益接近零，故整体移除。
///
/// 失败语义："引用相等 == 降级"。provider 抛异常时回落到传入的那一份引用，
/// 据此判定哪些行没翻成：不写缓存、也不该被当译文显示。
/// </summary>
public sealed class TranslationOrchestrator
{
    private readonly Glossary _glossary;
    private readonly TranslationCache _cache;
    private readonly Func<ITranslationProvider> _provider;
    private readonly Action<string>? _onError;

    /// <param name="provider">取当前活跃 provider（用 Func 是为了让"托盘切引擎"即时生效，不必重建本对象）。</param>
    /// <param name="onError">provider 异常上报。不给这个回调就等于把失败吞进黑洞：
    /// 回落的原文会被当译文显示，用户只会看到"韩文没翻译"而无处可查原因。</param>
    public TranslationOrchestrator(Glossary glossary, TranslationCache cache,
        Func<ITranslationProvider> provider, Action<string>? onError = null)
    {
        _glossary = glossary; _cache = cache; _provider = provider; _onError = onError;
    }

    /// <summary>当前缓存条目数（调试面板用）。</summary>
    public int CacheCount => _cache.Count;

    public async Task<TranslateOutcome> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx, CancellationToken ct)
    {
        int n = koreanLines.Count;
        var text = new string[n];
        var missIdx = new List<int>();
        var missText = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var ko = koreanLines[i];
            if (_glossary.TryGet(ko, out var g)) text[i] = g;            // 术语表：本地直出，零请求
            else if (_cache.TryGet(ko, out var c)) text[i] = c;          // 缓存：同上
            else { text[i] = ko; missIdx.Add(i); missText.Add(ko); }     // 先占位原文，翻成后覆盖
        }

        if (missText.Count == 0) return new TranslateOutcome(text, true, null);

        var provider = _provider();
        string? error = null;
        IReadOnlyList<string> zh;
        try { zh = await provider.TranslateAsync(missText, ctx, ct); }
        catch (Exception ex)
        {
            error = $"{provider.Name} 翻译失败: {ex.Message}";
            _onError?.Invoke(error);
            zh = missText; // 整体回落：下面逐行按"引用相等"判为未翻成
        }

        bool allResolved = true;
        for (int j = 0; j < missIdx.Count; j++)
        {
            // provider 违约返回了不完整的批次：当作未翻成，别越界读。
            if (j >= zh.Count || ReferenceEquals(zh[j], missText[j])) { allResolved = false; continue; }
            text[missIdx[j]] = zh[j];
            _cache.Set(missText[j], zh[j]);   // 只缓存真翻出来的
        }
        return new TranslateOutcome(text, allResolved, error);
    }
}
