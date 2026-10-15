using System.Text;
using SkiaSharp;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.Tools.LayoutCheck;

/// <summary>
/// 布局预设核验器：把 layout-presets.json 里的框画到真实截图上。
///
/// 为什么需要：这些数值是"内置的、不需要用户校准"的，一旦游戏改版让框子偏移，
/// 症状是"某些行没翻/翻进隔壁列"，很难反推到预设坐标上。画出来一眼就看见。
/// 用的就是运行时同一套加载与换算代码（LayoutPresetStore + Parts），所以核验结果不会与实机脱节。
///
/// 用法：dotnet run -p tools/LayoutCheck -- "&lt;截图路径&gt;" [输出目录]
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: dotnet run -p tools/LayoutCheck -- \"<截图.png>\" [输出目录]");
            return 1;
        }
        var shot = Path.GetFullPath(args[0]);
        if (!File.Exists(shot)) { Console.Error.WriteLine($"找不到截图 {shot}"); return 1; }
        var outDir = args.Length > 1 ? args[1] : @"D:\Temp\opencode";
        Directory.CreateDirectory(outDir);

        var repo = FindRepoRoot();
        var presetPath = Path.Combine(repo, "src", "ScrTranslator.App", LayoutPresetStore.DefaultFileName);
        var store = LayoutPresetStore.Load(presetPath);
        if (!store.IsUsable) { Console.Error.WriteLine($"预设加载失败：{store.LoadError}"); return 1; }
        foreach (var w in store.Warnings) Console.WriteLine($"警告: {w}");

        using var bm = SKBitmap.Decode(shot);
        string res = $"{bm.Width}x{bm.Height}";
        string scene = InferScene(Path.GetFileNameWithoutExtension(shot));
        Console.WriteLine($"截图 {Path.GetFileName(shot)} = {res}，按文件名推断场景 = {scene}");

        var preset = store.Find(scene, bm.Width, bm.Height);
        if (preset is null)
        {
            Console.WriteLine($"  该场景没有 {res} 预设。已收录：");
            foreach (var p in store.Presets.Where(p => p.Scene == scene))
                Console.WriteLine($"    {p.Resolution}  chat={(p.Chat is null ? "-" : $"[{p.Chat.X},{p.Chat.Y},{p.Chat.W},{p.Chat.H}]")}  roomNameList={(p.RoomNameList is null ? "-" : "有")}");
            return 2;
        }

        float lw = Math.Max(2f, bm.Width / 1280f);
        using var canvas = new SKBitmap(bm.Width, bm.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var cc = new SKCanvas(canvas))
        {
            cc.DrawBitmap(bm, 0, 0);
            // 先按运行时口径展开成 parts，再乘回像素——核验的就是实机那套换算
            var (fw, fh) = (bm.Width, bm.Height);
            foreach (var (name, rect) in preset.Parts())
            {
                var box = new SKRect((float)(rect.X * fw), (float)(rect.Y * fh),
                                     (float)((rect.X + rect.W) * fw), (float)((rect.Y + rect.H) * fh));
                using var pen = Stroke(SKColors.Magenta, lw);
                using var fill = Fill(SKColors.Magenta);
                cc.DrawRect(box, fill);
                cc.DrawRect(box, pen);
                Console.WriteLine($"  {name}: 像素 [{box.Left:F0},{box.Top:F0} {box.Width:F0}×{box.Height:F0}]");
            }
        }

        var outPath = Path.Combine(outDir, $"check_{Path.GetFileNameWithoutExtension(shot)}.jpg");
        using (var img = SKImage.FromBitmap(canvas))
        using (var d = img.Encode(SKEncodedImageFormat.Jpeg, 78))
            File.WriteAllBytes(outPath, d.ToArray());
        Console.WriteLine($"✓ 叠加图 {outPath}  （品红=预设识别区域）");
        return 0;
    }

    /// <summary>按文件名前缀推断场景；认不出返回空串让调用方看到候选列表。</summary>
    static string InferScene(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("browsing")) return GameSceneIds.Browsing;
        if (n.StartsWith("room")) return GameSceneIds.Room;
        if (n.StartsWith("ingame")) return GameSceneIds.InGame;
        return "";
    }

    static SKPaint Stroke(SKColor c, float w) => new() { Color = c, StrokeWidth = w, Style = SKPaintStyle.Stroke };
    static SKPaint Fill(SKColor c) => new() { Color = new SKColor(c.Red, c.Green, c.Blue, 32), Style = SKPaintStyle.Fill };

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScrTranslator.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
