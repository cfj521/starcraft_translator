using System.Threading;
using Sdcb.HyMT2Sharp.Backends;
using Sdcb.HyMT2Sharp.Backends.Vulkan;
using Sdcb.HyMT2Sharp.Model;
namespace ScrTranslator.Core.Translation.Providers;

/// <summary>本地 Hy-MT2（腾讯翻译专用模型，纯 C# 进程内推理，库模式）。离线、不限流、不 429、不 404。
/// 权重是 GGUF 文件（用户自配路径，默认 2-bit），首次翻译才懒加载（加载 1.8B 权重，耗时数秒、占 ~1GB 内存）。
/// 逐行独立翻译：每行 <see cref="HunyuanDenseModel.ResetCache"/> 复位 KV，免得第 2 行接第 1 行的上下文翻；
/// 解码用 <see cref="Sampler.ArgMax"/> 贪心（确定性、快，翻译专用模型 greedy 质量够）。
/// 模型非线程安全：本 provider 用锁串行化整批（引擎本就一批一批翻，锁是兜底防并发）。
/// CPU 推理是纯计算（无 I/O），用 <see cref="Task.Run"/> 卸载到线程池，不阻塞调用线程（UI/探测定时器）。</summary>
public sealed class HyMt2Provider : ITranslationProvider
{
    /// <summary>单行译文的 token 安全上限。聊天行很短，正常 EOS 前就停；这只是防跑飞的兜底。</summary>
    private const int MaxTokens = 256;

    /// <summary>模型卡默认翻译指令（英式提示、目标语用全名 Chinese）。按官方模型卡，指令与源文之间用两个换行分隔（源文独立成行），否则模型会把整条英文指令连同源文一起翻译成中文带出来。</summary>
    private const string PromptPrefix =
        "Translate the following text into Chinese. Note that you should only output the translated result without any additional explanation:";

    /// <summary>占位符保护提示：行内有非韩文串被换成 ⟨N⟩ 占位符时拼在指令后，要求模型原样保留占位符、只翻韩文。</summary>
    private const string PlaceholderHint =
        "The tokens like ⟨0⟩, ⟨1⟩ are placeholders for English words, numbers and punctuation; keep them exactly as-is, do not translate or modify them:";

    /// <summary>专有名词音译提示：人名/地名/房间名这类标识符不意译成中文、也不原样留韩文，而是用罗马字母音译（罗马拼音），其余普通韩文正常翻中文。
    /// 拼在翻译指令后、占位符提示前。</summary>
    private const string RomanizeHint =
        "Proper nouns (personal names, place names, room names) must be romanized into Latin letters, NOT translated to Chinese and NOT left as Korean, e.g. '도그타운' → 'Dogtown'; translate all other Korean normally:";

    private readonly Func<string> _modelPath;
    private readonly Func<string> _backend; // "cpu"/"gpu"（来自实时配置，用户可随时切换）
    private readonly Action<string>? _onError; // 加载过程异常上报（缺文件等由 GetStatus 报，这里是加载时抛错）
    private readonly Action<string>? _onInfo;  // 加载诊断（用哪个后端加载/回落）：切 GPU 后 CPU 仍高时靠它定位
    private readonly object _gate = new();
    private HunyuanDenseModel? _model; // 延迟加载单例（首次翻译或配置页冷加载才加载权重）
    private Task? _pendingUnload;      // 上一次 Unload 的后台卸载任务；EnsureModelAsync 等它完成再建新模型，避免双模型并存
    private int _inFlight;             // 正在使用模型实例做翻译的并发数；Unload 等它归零再真正 Dispose，避免销毁被翻译中的模型
    private readonly SemaphoreSlim _loadLock = new(1, 1); // 权重构造串行化（构造重活不持 _gate，见 GetOrBuildModel）
    private readonly object _inferLock = new();          // 模型推理串行化：HunyuanDenseModel 有 KV 缓存，非线程安全
    private bool _loadInProgress;      // 正在构造权重（_gate 保护）：Unload 据此登记"构造完就丢弃"
    private bool _disposeRequested;    // 构造期间收到 Unload → 构造完成后直接 Dispose，不挂上 _model
    private DateTime _retryAfter = DateTime.MinValue; // Failed 后的重试闸门：避免每拍（200ms）都重试加载 1.8B 权重
    private const int FailureCooldownSeconds = 30;
    private const int UnloadWaitSeconds = 30; // Unload 等在用翻译结束的上限；超时则放弃 Dispose（宁可漏内存也不崩进程）

    public HyMt2Provider(Func<string> modelPathGetter, Func<string> backendGetter, Action<string>? onError = null, Action<string>? onInfo = null)
    { _modelPath = modelPathGetter; _backend = backendGetter; _onError = onError; _onInfo = onInfo; }

    public string Name => "hy_mt2";
    public EngineKind Kind => EngineKind.LLM;

    /// <summary>当前权重加载状态（调试面板"翻译引擎："行显示）。在 _gate 锁内读/写，外部经属性读。</summary>
    private HyMt2LoadState _state = HyMt2LoadState.NotLoaded;

    /// <summary>权重加载状态（线程安全，_gate 锁内变更）。NotLoaded＝从未加载；Loading＝后台加载进行中；
    /// Ready＝已加载可翻；Failed＝加载失败（缺文件/后端初始化异常）。</summary>
    public HyMt2LoadState LoadState
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>状态→调试面板文案（中文）。</summary>
    public string DescribeLoadState() => LoadState switch
    {
        HyMt2LoadState.Ready => "可用",
        HyMt2LoadState.Loading => "加载中",
        HyMt2LoadState.Failed => "不可用（加载失败）",
        _ => "未加载",
    };

    public ProviderStatus GetStatus()
    {
        string path = _modelPath();
        if (string.IsNullOrWhiteSpace(path)) return ProviderStatus.NoCreds("本地 Hy-MT2 未配置（模型路径为空）");
        if (!System.IO.File.Exists(path)) return ProviderStatus.NoCreds($"本地 Hy-MT2 模型文件不存在：{path}");
        // Failed 必须在这里拦住：否则编排器每拍（默认 200ms）都选它 → EnsureModelLocked 再 new 一次 1.8B 权重
        // → 再失败刷日志，CPU/IO 打满而翻译永久不可用。冷却期报 NoCredentials（不联网、不发请求，与"加载不了"语义一致）。
        lock (_gate)
        {
            if (_state == HyMt2LoadState.Failed)
            {
                var left = _retryAfter - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                    return ProviderStatus.NoCreds(
                        $"本地 Hy-MT2 加载失败，{Math.Ceiling(left.TotalSeconds):0} 秒后重试（换模型或检查内存/后端）");
            }
        }
        return ProviderStatus.Ok();
    }

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        return Task.Run(async () =>
        {
            var model = await AcquireModelAsync(ct); // 延迟加载 + 原子宣告"正在用"（_inFlight 加一后才可能 Unload）
            try
            {
                // 不持 _gate 推理：模型不会被 Dispose（_inFlight > 0），而持锁会让 UI 线程读 LoadState、
                // Unload、并发翻译全部排队等一整轮生成长达数秒甚至更久。
                var results = new string[lines.Count];
                lock (_inferLock) // 模型有 KV 缓存，推理必须串行（其余线程只等推理，不等加载）
                {
                    for (int i = 0; i < lines.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string? zh = TranslateLine(model, lines[i], ct);
                        // null＝真失败（空生成/占位符丢失）→ 引用相等原文，编排器据此判未翻成、下一拍重试
                        // 非 null＝真翻出，或回声保留的原文新字符串（引用不等）→ 编排器判 Resolved
                        results[i] = zh ?? lines[i];
                    }
                }
                return (IReadOnlyList<string>)results;
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }, ct);
    }

    /// <summary>冷加载：立即在后台加载 GGUF 权重（不阻塞调用线程）。幂等，已加载则直接返回。
    /// app 启动完成后（选中内部组时）与配置页选中内部组并保存时调用。</summary>
    public void Load()
    {
        Task.Run(async () =>
        {
            try { await GetOrBuildModelAsync(CancellationToken.None); }
            catch (Exception ex) { _onError?.Invoke($"本地 Hy-MT2 加载失败: {ex.Message}"); }
        });
    }

    /// <summary>给翻译用：确保模型就绪 + 把 _inFlight 加一（同一把 _gate 内校验仍是同一个实例）。
    /// 关键：加载完成与"宣告在用"不能分开——否则中间窗口里 Unload 读到 _inFlight==0 就 Dispose，
    /// 翻译随即在已释放的原生模型上推理 → AccessViolation 杀进程。</summary>
    private async Task<HunyuanDenseModel> AcquireModelAsync(CancellationToken ct)
    {
        while (true)
        {
            var model = await GetOrBuildModelAsync(ct);
            lock (_gate)
            {
                if (ReferenceEquals(_model, model))
                {
                    Interlocked.Increment(ref _inFlight); // 与校验同锁：Unload 拿不到锁就不会误判"没人用"
                    return model;
                }
            }
            // 刚刚被 Unload 换掉：重来一轮（会先等卸载任务完成再建新模型）。
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>取模型，没有就在**锁外**构造权重。构造 1.8B 权重耗时数秒到数十秒，
    /// 必须在 _gate 外做——旧实现在 lock(_gate) 里 new，期间 UI 线程读 LoadState（调试面板每拍刷新）、
    /// Unload、其它翻译线程全部阻塞，界面卡死。
    /// _loadLock 保证同一时刻只有一个线程构造，避免并发 new 出两份权重（~2GB 与 native 资源打架）。</summary>
    private async Task<HunyuanDenseModel> GetOrBuildModelAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate) { if (_model is not null) return _model; }

            await _loadLock.WaitAsync(ct);
            try
            {
                lock (_gate) { if (_model is not null) return _model; } // 等锁期间别人已建好

                Task? pending;
                lock (_gate) pending = _pendingUnload;
                if (pending is not null) { try { await pending; } catch { } } // 等旧模型卸载完，再建新的

                bool gpuWanted = _backend() == "gpu";
                // Vulkan 探测可能较慢，放在 _gate 外：探测期间 UI 线程读 LoadState 不能被拖住。
                string backendName = gpuWanted && VulkanBackend.IsSupported() ? "vulkan" : "cpu";
                if (gpuWanted && backendName != "vulkan")
                    _onError?.Invoke("本地 Hy-MT2 选中 GPU 但无可用 Vulkan 设备，已回落 CPU");
                lock (_gate)
                {
                    if (_model is not null) return _model; // 探测期间别人已建好
                    _loadInProgress = true;
                    _state = HyMt2LoadState.Loading;
                }

                _onInfo?.Invoke($"本地 Hy-MT2 以 {backendName} 后端加载模型（权重路径：{_modelPath()}）");
                HunyuanDenseModel built;
                try
                {
                    built = new HunyuanDenseModel(_modelPath(), backend: BackendFactory.Create(backendName));
                }
                catch (Exception)
                {
                    lock (_gate)
                    {
                        _loadInProgress = false;
                        _disposeRequested = false;
                        _state = HyMt2LoadState.Failed;
                        _retryAfter = DateTime.UtcNow.AddSeconds(FailureCooldownSeconds);
                    }
                    throw;
                }

                bool discard;
                lock (_gate)
                {
                    _loadInProgress = false;
                    discard = _disposeRequested; // 构造期间有 Unload：这份白建了，直接销毁
                    _disposeRequested = false;
                    if (!discard) { _model = built; _state = HyMt2LoadState.Ready; }
                }
                if (!discard) return built;

                try { built.Dispose(); } catch { /* 丢弃路径，忽略 */ }
            }
            finally { _loadLock.Release(); }

            // 构造期间被 Unload 了：释放锁后重来一轮（不能持锁递归，SemaphoreSlim 不可重入）。
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>卸载：销毁模型、释放权重内存（~1GB）。后台线程执行（不阻塞调用线程/UI），
    /// 且等正在使用该模型的翻译（_inFlight）结束才真正 Dispose，避免销毁被翻译中的模型。
    /// 配置页选中外部组并保存时调用。未加载则无操作。</summary>
    public void Unload()
    {
        HunyuanDenseModel? m;
        lock (_gate)
        {
            m = _model;
            _model = null;
            if (_loadInProgress) _disposeRequested = true; // 正在构造：构造完就地销毁，别挂上来
            _state = HyMt2LoadState.NotLoaded;
            if (m is null) return; // 未加载且没在构造：无操作
            // 先登记"卸载中"再启动任务：否则中间窗口里并发的 GetOrBuildModelAsync 会再 new 一份
            // 1.8B 权重，两份同时驻留（~2GB）且 native 资源打架。
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingUnload = done.Task;
            var victim = m;
            Task.Run(() =>
            {
                try
                {
                    // 等在用翻译结束，但有上限：无限等会让 done 永不完成、后续加载永久等待。
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (Volatile.Read(ref _inFlight) > 0)
                    {
                        if (sw.Elapsed > TimeSpan.FromSeconds(UnloadWaitSeconds))
                        {
                            // 宁可漏一份权重内存（进程退出即回收），也不能 Dispose 正在推理的模型（会 AccessViolation 杀进程）。
                            _onError?.Invoke($"本地 Hy-MT2 卸载超时（仍有翻译在用），已跳过释放以免崩溃");
                            return;
                        }
                        Thread.Sleep(10);
                    }
                    victim.Dispose();
                }
                catch (Exception ex) { _onError?.Invoke($"本地 Hy-MT2 卸载失败: {ex.Message}"); }
                finally { done.TrySetResult(); }
            });
        }
    }

    private static string? TranslateLine(HunyuanDenseModel model, string line, CancellationToken ct)
    {
        // 只翻韩文保护：非韩文串（英文/数字/标点）换 ⟨N⟩ 占位符，只翻韩文；回填时还原。占位符丢了则回退原文。
        // 与外部 LLM 引擎共用 KoreanOnlyProtector（这里单行，传一行列表）。
        var (protectedLines, tokenSets) = KoreanOnlyProtector.Protect([line]);
        string protectedLine = protectedLines[0];
        string[] tokens = tokenSets[0];
        // 指令与源文用两个换行分隔（官方模型卡要求，源文独立成行），否则模型会把指令一起翻成中文带出来。
        string instruction = PromptPrefix + " " + RomanizeHint + (tokens.Length > 0 ? " " + PlaceholderHint : "");
        string prompt = instruction + "\n\n" + protectedLine;
        model.ResetCache(); // 每行独立：清 KV，别让第 2 行接着第 1 行的上下文翻
        int[] encoded = model.Tokenizer.Encode(ChatTemplate.RenderHunyuanDense([new ChatMessage("user", prompt)]));
        float[] logits = model.Forward(model.AlignPrompt(encoded).Suffix);
        var generated = new List<int>();
        for (int i = 0; i < MaxTokens; i++)
        {
            ct.ThrowIfCancellationRequested();
            int token = Sampler.ArgMax(logits); // 贪心：确定性、快
            if (model.Tokenizer.IsStop(token)) break;
            generated.Add(token);
            logits = model.Forward([token]);
        }
        string text = model.Tokenizer.DecodeVisible(generated).Trim();
        if (text.Length == 0) return null; // 空生成＝真失败 → 原始引用，编排器判未翻成、下一拍重试
        if (text == protectedLine) return new string(line.ToCharArray()); // 回声＝专有名词故意保留 → 原文的新字符串（引用不等），编排器判 Resolved
        if (KoreanOnlyProtector.IsInstructionEcho(text)) return new string(line.ToCharArray()); // 指令回声（模型把 PlaceholderHint 改述成中文当译文）→ 回退原文
        // 回填占位符；任一占位符丢失（模型改坏）→ 回退原文（返回 null，编排器回落原文）
        string? restored = KoreanOnlyProtector.Restore(text, tokens);
        if (restored is null) return null;
        return restored;
    }
}

/// <summary>HY-MT2 权重加载状态（调试面板"翻译引擎："行显示）。
/// NotLoaded＝从未加载；Loading＝后台加载进行中；Ready＝已加载可翻；Failed＝加载失败（缺文件/后端初始化异常）。</summary>
public enum HyMt2LoadState { NotLoaded, Loading, Ready, Failed }
