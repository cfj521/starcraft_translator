using System.Text.RegularExpressions;

namespace ScrTranslator.Core.Tests;

/// <summary>
/// 文档一致性检查：把"代码推进了、文档没跟上"变成 CI 红，而不是靠人问。
///
/// 两类规则，都由下面的表驱动（新增功能时在表里加一行即可）：
///  1. **活符号必须出现在文档里**：新写的类/文件/工具如果文档 0 命中，说明从没写过它 → 失败。
///  2. **退役符号不得以"现役"口吻出现在文档里**：已删除的名字允许出现，但所在行必须带
///     退役标记（已移除/已删除/废弃/旧/曾/早期/不再/历史…），否则就是"文档还在描述一个不存在的东西"。
///
/// 只在源码树里跑（从测试程序集目录向上找 ScrTranslator.slnx）；找不到仓库根就跳过，
/// 免得在只有产物的机器上误报。
/// </summary>
public class DocsConsistencyTests
{
    /// <summary>退役标记：出现这些词的允许提到已删除的名字。</summary>
    private static readonly string[] RetirementMarkers =
    {
        "已移除", "已删除", "移除", "删除", "废弃", "不再", "旧", "老", "曾", "早期", "历史", "已改", "改为", "取代",
        "勿再", "已整体", "已随", "兼容", "→", "无", "没有", "回退", "恢复出厂",
    };

    /// <summary>
    /// 历史遗留键：它们**是活功能**（GameSceneIds.LegacyToNewId 与 RoiStore.Load 靠它们兼容老校准文件），
    /// 所以不参与"源码里不得出现"的检查；但文档提到时仍须带退役/兼容字样，避免被当成本名。
    /// </summary>
    private static readonly string[] LegacyAliases =
    {
        "custom_games_listing", "waiting_room", "in_game",
    };

    /// <summary>代码里存在、文档必须至少提到一次的名字。</summary>
    private static readonly string[] LiveSymbols =
    {
        // 场景识别
        "SceneRecognizer", "SceneRefStore", "SceneFingerprint", "SceneRefIntegrity", "SceneRefCapture", "FramePixels",
        "scene-refs.json", "scene-overrides.json", "tools/SceneRefGen",
        // 区域与布局预设
        "layout-presets.json", "layout-overrides.json", "LayoutPresetStore", "ToRois", "EffectiveRois",
        "tools/LayoutCheck", "ReplaceRoi", "RemoveRoi", "MoveRoi", "HitColumn",
        // 翻译与闸门
        "TranslationOrchestrator", "KoreanText", "ContainsHangul", "ShouldTranslate",
        "TranslationProviderEntry", "TencentMtProvider", "AliyunMtProvider",
        // 节拍与外观
        "SceneDetectPollMs", "TranslatePollMs", "CaptureFloor", "BackplateOpacity",
        // 热重载与校准
        "ReloadRois", "ReloadLayoutPresets", "CopyFromScreen",
    };

    /// <summary>代码里已不存在的名字：文档提到就必须带退役标记。</summary>
    private static readonly string[] RetiredSymbols =
    {
        "TieredTranslator", "TwoLevel", "FastEngine", "FastModel", "Provisional", "TranslationStage",
        "TranslationEngine",
        "ChatPollMs", "OtherPollMs", "SceneDetector", "RoiManager", "ChangeDetector",
        "RoomBrowser", "WaitingRoom", "Papago", "ScrTranslator.Overlay", "ScrTranslator.Common", "appsettings.json",
    };

    static readonly string? RepoRoot = FindRepoRoot();

    static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScrTranslator.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    static readonly string[] DocFiles = { "README.md", "CLAUDE.md", "docs/PRD.md", "docs/TECH_DESIGN.md", "docs/UI_DESIGN.md", "docs/ROADMAP.md", "docs/E2E_CHECKLIST.md" };

    static bool Has(string text, string needle) =>
        text.IndexOf(needle, StringComparison.Ordinal) >= 0;

    /// <summary>把整篇文档按"行"展开成 (行号, 文本)，便于报出可定位的位置。</summary>
    static IEnumerable<(string File, int Line, string Text)> Lines()
    {
        if (RepoRoot is null) yield break;
        foreach (var rel in DocFiles)
        {
            var path = Path.Combine(RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var text = File.ReadAllLines(path);
            for (int i = 0; i < text.Length; i++) yield return (rel, i + 1, text[i]);
        }
    }

    [Fact]
    public void RepoRoot_FoundOrTestSkipped()
    {
        // 找不到源码树（例如只拷了产物）时整组检查没有意义，直接放行而不是误报。
        if (RepoRoot is null) return;
        Assert.True(DocFiles.All(f => File.Exists(Path.Combine(RepoRoot, f.Replace('/', Path.DirectorySeparatorChar)))),
            "部分文档文件缺失，检查表本身可能过期了");
    }

    [Fact]
    public void EveryLiveFeature_IsDocumented()
    {
        if (RepoRoot is null) return;
        var all = string.Join("\n", Lines().Select(l => l.Text));
        var missing = LiveSymbols.Where(s => !Has(all, s)).ToList();
        Assert.True(missing.Count == 0,
            "这些代码里的东西在文档里一次都没提到（新功能写完漏了文档）：\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void RetiredNames_AreOnlyMentionedWithRetirementMarker()
    {
        if (RepoRoot is null) return;
        var watch = RetiredSymbols.Concat(LegacyAliases);
        var bad = Lines()
            .Where(l => watch.Any(s => Has(l.Text, s)) && !RetirementMarkers.Any(m => Has(l.Text, m)))
            .Select(l => $"  {l.File}:{l.Line}  {l.Text.Trim()}")
            .ToList();
        Assert.True(bad.Count == 0,
            "文档以『现役』口吻提到已删除的名字（要么补『已移除/旧/曾…』说明，要么删掉这段）：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void RetiredSymbols_ReallyAreGoneFromCode()
    {
        if (RepoRoot is null) return;
        // 反向守卫：表里标"退役"的名字若又出现在源码里（除了注释），说明有人把它加回来了 ——
        // 要么是真的复活了死功能，要么是这张表过期了，两种都得处理。
        var srcDir = Path.Combine(RepoRoot, "src");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Contains(".g.cs", StringComparison.Ordinal)) continue;
            foreach (var retired in RetiredSymbols)
            {
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("//") && !line.StartsWith("///") && Has(line, retired) &&
                        !RetirementMarkers.Any(m => Has(line, m)))
                    {
                        offenders.Add($"  {Path.GetRelativePath(srcDir, file)}: {line}");
                        break;
                    }
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "标为退役的名字出现在源码非注释行里（死功能复活？还是表过期？）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void DocFileReferences_ThatLookLikePaths_Exist()
    {
        if (RepoRoot is null) return;
        // 文档里写的 tools/xxx、docs/xxx.md、src/... 这类路径，指向的文件必须真的在，
        // 否则就是"文档教用户去一个不存在的地方"。
        var pattern = new Regex(@"(tools/[A-Za-z0-9_.\-]+|docs/[A-Za-z0-9_.\-]+\.md|src/[A-Za-z0-9_./\-]+\.cs|models/[A-Za-z0-9_.\-]+)");
        var missing = new List<string>();
        foreach (var (file, lineNo, text) in Lines())
        {
            // 带退役标记的行允许提到已删除的路径（例如"删除了 tools/Xxx"这类历史记录），
            // 否则每次删工具都要回去改 ROADMAP，反而逼人少写记录。
            if (RetirementMarkers.Any(m => Has(text, m))) continue;
            foreach (Match m in pattern.Matches(text))
            {
                var rel = m.Value.TrimEnd('.', '，', ',', ')', '）', '`');
                if (rel.EndsWith("/")) rel = rel.TrimEnd('/');
                var abs = Path.Combine(RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs) || Directory.Exists(abs)) continue;
                // 允许带扩展名省略的目录写法（tools/LayoutCheck 目录存在即可）与通配示例
                if (rel.Contains('*')) continue;
                missing.Add($"  {file}:{lineNo}  {rel}");
            }
        }
        Assert.True(missing.Distinct().Count() == 0,
            "文档引用了不存在的文件/目录路径：\n" + string.Join("\n", missing.Distinct()));
    }
}
