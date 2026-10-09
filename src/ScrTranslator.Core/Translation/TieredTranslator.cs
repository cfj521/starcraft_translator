namespace ScrTranslator.Core.Translation;

public enum TranslationStage { Direct, Provisional, Final }
public sealed record LineTranslation(int Index, string Text, TranslationStage Stage);
/// <summary><see cref="AllResolved"/>=本批每行都真翻出来了。<see cref="Error"/>=最近一次 provider 失败的原因
/// （null=没失败）。失败时编排会回落原文，调用方**必须**据此决定别把原文当译文显示出去。</summary>
public sealed record TranslateOutcome(bool AllResolved, string? Error = null);

/// <summary>
/// 两级/单级翻译编排。术语/缓存命中直出(Direct)；未命中行——两级时先快态(Provisional,不缓存)
/// 再质量态(Final,缓存);单级时仅质量态(Final)。onStage 回调携带全量快照,调用方负责 marshal UI。
/// "引用相等 == 降级"贯穿:凡回落传入引用的行一律不缓存。
/// 注:只要本次调用有 miss 行(进入翻译轮次),终态快照会把**全部**行(含本轮 Direct 命中行)
/// 统一标为 Final——整批一起"定稿"上屏；Direct 仅出现在全命中早退路径与两级临时态快照里。
/// (Direct 与 Final 渲染无差别,只有 Provisional 会被降样式,故此重标视觉上是 no-op。)
/// </summary>
public sealed class TieredTranslator
{
    private readonly Glossary _glossary;
    private readonly TranslationCache _cache;
    private readonly Func<ITranslationProvider> _fast;
    private readonly Func<ITranslationProvider> _quality;
    private readonly Func<TranslationSettings> _settings;
    private readonly Action<string>? _onError;

    /// <param name="onError">provider 异常上报（"什么消息+耗时"级别，调用方接日志）。
    /// 不给这个回调就等于把失败吞进黑洞：编排会回落原文显示，用户只会看到"韩文没翻译"而不知道为什么。</param>
    public TieredTranslator(Glossary glossary, TranslationCache cache,
        Func<ITranslationProvider> fastProvider, Func<ITranslationProvider> qualityProvider,
        Func<TranslationSettings> settings, Action<string>? onError = null)
    {
        _glossary = glossary; _cache = cache;
        _fast = fastProvider; _quality = qualityProvider; _settings = settings;
        _onError = onError;
    }

    /// <summary>当前缓存条目数（调试面板用）。</summary>
    public int CacheCount => _cache.Count;

    public async Task<TranslateOutcome> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx,
        Action<IReadOnlyList<LineTranslation>> onStage, CancellationToken ct)
    {
        int n = koreanLines.Count;
        var text = new string[n];
        var stage = new TranslationStage[n];
        var missIdx = new List<int>();
        var missText = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var ko = koreanLines[i];
            if (_glossary.TryGet(ko, out var g)) { text[i] = g; stage[i] = TranslationStage.Direct; }
            else if (_cache.TryGet(ko, out var c)) { text[i] = c; stage[i] = TranslationStage.Direct; }
            else { missIdx.Add(i); missText.Add(ko); }
        }

        if (missText.Count == 0)
        {
            onStage(Snapshot(text, stage));
            return new TranslateOutcome(true);
        }

        bool twoLevel = _settings().TwoLevel;
        IReadOnlyList<string>? fastZh = null;
        string? error = null;
        if (twoLevel)
        {
            var (fastLines, fastErr) = await CallAsync(_fast(), missText, ctx, ct);
            fastZh = fastLines; error = fastErr;
            for (int j = 0; j < missIdx.Count; j++)
            {
                text[missIdx[j]] = fastZh[j];
                stage[missIdx[j]] = TranslationStage.Provisional;
            }
            onStage(Snapshot(text, stage)); // 临时态；快态不入缓存
        }

        var (qualZh, qualErr) = await CallAsync(_quality(), missText, ctx, ct);
        error ??= qualErr;
        bool allResolved = true;
        for (int j = 0; j < missIdx.Count; j++)
        {
            int i = missIdx[j];
            string final; bool cacheIt;
            if (!ReferenceEquals(qualZh[j], missText[j])) { final = qualZh[j]; cacheIt = true; }
            else if (fastZh is not null && !ReferenceEquals(fastZh[j], missText[j])) { final = fastZh[j]; cacheIt = true; }
            else { final = missText[j]; cacheIt = false; allResolved = false; }
            text[i] = final; stage[i] = TranslationStage.Final;
            if (cacheIt) _cache.Set(missText[j], final);
        }
        // 终态快照统一标 Final（含本轮直出的 Direct 行）：一旦进入翻译轮次，整批一起"定稿"上屏。
        for (int i = 0; i < n; i++) stage[i] = TranslationStage.Final;
        onStage(Snapshot(text, stage));
        return new TranslateOutcome(allResolved, error);
    }

    /// <summary>调 provider；异常时**回落传入引用**（引用相等 == 该行没翻成），同时把原因上报。</summary>
    private async Task<(IReadOnlyList<string> Lines, string? Error)> CallAsync(
        ITranslationProvider p, IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        try { return (await p.TranslateAsync(lines, ctx, ct), null); }
        catch (Exception ex)
        {
            // 只 catch 不报 = 把失败吞进黑洞。回落的原文会被当译文显示，用户看到的症状是
            // "识别到韩文但没翻译"，且没有任何地方说明为什么（实测就是空 API Key 秒返回 401）。
            string msg = $"{p.Name} 翻译失败: {ex.Message}";
            _onError?.Invoke(msg);
            return (lines, msg);
        }
    }

    private static IReadOnlyList<LineTranslation> Snapshot(string[] text, TranslationStage[] stage)
    {
        var list = new LineTranslation[text.Length];
        for (int i = 0; i < text.Length; i++) list[i] = new LineTranslation(i, text[i], stage[i]);
        return list;
    }
}
