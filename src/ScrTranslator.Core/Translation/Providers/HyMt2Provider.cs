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

    private readonly Func<string> _modelPath;
    private readonly Func<string> _backend; // "cpu"/"gpu"（来自实时配置，用户可随时切换）
    private readonly Action<string>? _onError; // 加载过程异常上报（缺文件等由 GetStatus 报，这里是加载时抛错）
    private readonly Action<string>? _onInfo;  // 加载诊断（用哪个后端加载/回落）：切 GPU 后 CPU 仍高时靠它定位
    private readonly object _gate = new();
    private HunyuanDenseModel? _model; // 延迟加载单例（首次翻译或配置页冷加载才加载权重）

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
        return ProviderStatus.Ok();
    }

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        return Task.Run(() =>
        {
            lock (_gate)
            {
                var model = EnsureModelLocked(); // 延迟加载：启动后台预加载或首次翻译才加载权重
                var results = new string[lines.Count];
                for (int i = 0; i < lines.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string? zh = TranslateLine(model, lines[i], ct);
                    // null＝没翻成（空生成/回声原文）→ 引用相等原文，编排器据此判未翻成、整批回退
                    results[i] = zh ?? lines[i];
                }
                return (IReadOnlyList<string>)results;
            }
        }, ct);
    }

    /// <summary>冷加载：立即在后台加载 GGUF 权重（不阻塞调用线程）。EnsureModelLocked 幂等，已加载则直接返回。
    /// app 启动完成后（选中内部组时）与配置页选中内部组并保存时调用。</summary>
    public void Load()
    {
        lock (_gate) { _state = HyMt2LoadState.Loading; }
        Task.Run(() =>
        {
            try { lock (_gate) EnsureModelLocked(); }
            catch (Exception ex)
            {
                lock (_gate) _state = HyMt2LoadState.Failed;
                _onError?.Invoke($"本地 Hy-MT2 加载失败: {ex.Message}");
            }
        });
    }

    /// <summary>卸载：销毁模型、释放权重内存（~1GB）。配置页选中外部组并保存时调用。未加载则无操作。</summary>
    public void Unload()
    {
        lock (_gate)
        {
            _model?.Dispose();
            _model = null;
            _state = HyMt2LoadState.NotLoaded;
        }
    }

    /// <summary>延迟加载入口：首次调用才加载权重。须在 _gate 锁内调用。</summary>
    private HunyuanDenseModel EnsureModelLocked()
    {
        if (_model is null)
        {
            // "gpu" → Vulkan 后端（需系统有可用 Vulkan 设备，IsSupported() 为假则回落 CPU 并上报）；"cpu" → 纯 CPU SIMD。
            string backendName = "cpu";
            if (_backend() == "gpu")
            {
                if (VulkanBackend.IsSupported())
                    backendName = "vulkan";
                else
                    _onError?.Invoke("本地 Hy-MT2 选中 GPU 但无可用 Vulkan 设备，已回落 CPU");
            }
            _onInfo?.Invoke($"本地 Hy-MT2 以 {backendName} 后端加载模型（权重路径：{_modelPath()}）");
            _state = HyMt2LoadState.Loading;
            try
            {
                _model = new HunyuanDenseModel(_modelPath(), backend: BackendFactory.Create(backendName));
                _state = HyMt2LoadState.Ready;
            }
            catch
            {
                _state = HyMt2LoadState.Failed;
                throw;
            }
        }
        return _model;
    }

    private static string? TranslateLine(HunyuanDenseModel model, string line, CancellationToken ct)
    {
        // 只翻韩文保护：非韩文串（英文/数字/标点）换 ⟨N⟩ 占位符，只翻韩文；回填时还原。占位符丢了则回退原文。
        // 与外部 LLM 引擎共用 KoreanOnlyProtector（这里单行，传一行列表）。
        var (protectedLines, tokenSets) = KoreanOnlyProtector.Protect([line]);
        string protectedLine = protectedLines[0];
        string[] tokens = tokenSets[0];
        // 指令与源文用两个换行分隔（官方模型卡要求，源文独立成行），否则模型会把指令一起翻成中文带出来。
        string instruction = tokens.Length > 0 ? PromptPrefix + " " + PlaceholderHint : PromptPrefix;
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
        if (text.Length == 0 || text == protectedLine) return null; // 空＝没生成；回声＝没真翻（模型把占位符原文当译文吐了）
        // 回填占位符；任一占位符丢失（模型改坏）→ 回退原文（返回 null，编排器回落原文）
        string? restored = KoreanOnlyProtector.Restore(text, tokens);
        if (restored is null) return null;
        return restored;
    }
}

/// <summary>HY-MT2 权重加载状态（调试面板"翻译引擎："行显示）。
/// NotLoaded＝从未加载；Loading＝后台加载进行中；Ready＝已加载可翻；Failed＝加载失败（缺文件/后端初始化异常）。</summary>
public enum HyMt2LoadState { NotLoaded, Loading, Ready, Failed }
