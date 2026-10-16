namespace ScrTranslator.Core.Translation;

/// <summary>一批翻译的结果。<see cref="Texts"/> 与传入的韩文行**按下标一一对应**。</summary>
/// <param name="AllResolved">本批每一行都真翻出来了。false 时未翻成的行在 <see cref="Texts"/> 里仍是原文，
/// 调用方**必须**按"文本==原文"过滤掉，否则原文会被当译文显示出去。</param>
/// <param name="Error">provider 失败原因（null=没失败）。池内全部 provider 都不可用/失败时才非空。</param>
public sealed record TranslateOutcome(IReadOnlyList<string> Texts, bool AllResolved, string? Error,
    int GlossaryHits = 0, int CacheHits = 0, int EngineHits = 0);

/// <summary>
/// 翻译编排：**术语表 → 缓存 → provider 池回退链**，单级，一批一次请求。
///
/// provider 池（<see cref="_pool"/>）是有序的 provider 列表。编排器从上到下取第一个
/// 「<see cref="ITranslationProvider.GetStatus"/> 为 Available」的 provider 翻译；
/// 不可用（无凭据/额度耗尽）或翻译失败（抛异常）就跳到下一个，直到成功或全部失败。
/// 默认池顺序＝先免费机器翻译（腾讯云/阿里云）、后 LLM（OpenAI/阿里云），与"先免费再 LLM"一致。
/// </summary>
public sealed class TranslationOrchestrator
{
    private readonly Glossary _glossary;
    private readonly TranslationCache _cache;
    private readonly Func<IReadOnlyList<ITranslationProvider>> _pool;
    private readonly Action<string>? _onError;
    /// <summary>最近 3 次成功翻译所用的引擎（provider Name），供调试面板展示。FIFO，超 3 丢最旧。</summary>
    private readonly Queue<string> _recentProviders = new();

    /// <param name="pool">取当前 provider 池（有序、已过滤启用项）。用 Func 是为了让"配置页拖拽排序/启用禁用"即时生效，不必重建本对象。</param>
    /// <param name="onError">provider 失败上报。不给这个回调就等于把失败吞进黑洞：
    /// 回落的原文会被当译文显示，用户只会看到"韩文没翻译"而无处可查原因。</param>
    public TranslationOrchestrator(Glossary glossary, TranslationCache cache,
        Func<IReadOnlyList<ITranslationProvider>> pool, Action<string>? onError = null)
    {
        _glossary = glossary; _cache = cache; _pool = pool; _onError = onError;
    }

    /// <summary>当前缓存条目数（调试面板用）。</summary>
    public int CacheCount => _cache.Count;

    /// <summary>当前 provider 池实例（调试面板逐个展示状态用；翻译仍走 <see cref="TranslateAsync"/> 的回退链）。</summary>
    public IReadOnlyList<ITranslationProvider> Providers => _pool();

    /// <summary>最近 3 次成功翻译所用的引擎（`名称(MT/LLM)` 形式），按时间从旧到新。调试面板展示用。</summary>
    public IReadOnlyList<string> RecentProviders => _recentProviders.ToArray();

    private void RecordProvider(ITranslationProvider p)
    {
        var name = $"{p.Name}({p.Kind})";
        _recentProviders.Enqueue(name);
        while (_recentProviders.Count > 3) _recentProviders.Dequeue();
    }

    public async Task<TranslateOutcome> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx, CancellationToken ct)
    {
        int n = koreanLines.Count;
        var text = new string[n];
        var missIdx = new List<int>();
        var missText = new List<string>();
        int glossaryHits = 0, cacheHits = 0;
        for (int i = 0; i < n; i++)
        {
            var ko = koreanLines[i];
            if (_glossary.TryGet(ko, out var g)) { text[i] = g; glossaryHits++; }        // 术语表：本地直出，零请求
            else if (_cache.TryGet(ko, out var c)) { text[i] = c; cacheHits++; }          // 缓存：同上
            else { text[i] = ko; missIdx.Add(i); missText.Add(ko); }                      // 先占位原文，翻成后覆盖
        }

        if (missText.Count == 0) return new TranslateOutcome(text, true, null, glossaryHits, cacheHits, 0);

        // 池回退链：从上到下，跳过不可用（无凭据/额度耗尽），翻译失败跳到下一个。
        var providers = _pool();
        string? error = null;
        IReadOnlyList<string>? zh = null;
        foreach (var p in providers)
        {
            var status = p.GetStatus();
            if (!status.Usable) { error = $"{p.Name}：{status.Detail}"; continue; } // 不可用，跳到下一个
            try
            {
                zh = await p.TranslateAsync(missText, ctx, ct);
                error = null;
                RecordProvider(p); // 记最近 3 次引擎（调试面板）
                break; // 成功，用这个 provider 的结果
            }
            catch (Exception ex)
            {
                error = $"{p.Name} 翻译失败: {ex.Message}";
                _onError?.Invoke(error);
                zh = null; // 继续下一个
            }
        }

        if (zh is null)
        {
            // 全部 provider 都不可用/失败：整批回落原文（miss 行已送引擎，engine 命中照计）。
            return new TranslateOutcome(text, false, error ?? "没有可用的翻译 provider", glossaryHits, cacheHits, missText.Count);
        }

        bool allResolved = true;
        for (int j = 0; j < missIdx.Count; j++)
        {
            // provider 违约返回了不完整的批次：当作未翻成，别越界读。
            if (j >= zh.Count || ReferenceEquals(zh[j], missText[j])) { allResolved = false; continue; }
            text[missIdx[j]] = zh[j];
            _cache.Set(missText[j], zh[j]);   // 只缓存真翻出来的
        }
        return new TranslateOutcome(text, allResolved, error, glossaryHits, cacheHits, missText.Count);
    }
}
