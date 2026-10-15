using ScrTranslator.Core.Settings;
namespace ScrTranslator.Core.Tests.Settings;

/// <summary>
/// 两个正交轮询节拍：场景检测(默认 20ms) 与 翻译(默认 200ms)，各自钳制到 [10,1000]；
/// 外加一条关键不变量——捕获侧节流必须取两者较快值，否则识别会被翻译的慢节拍拖平。
/// </summary>
public class PollIntervalTests
{
    [Fact] public void Defaults_Are20And200()
    {
        var ocr = new OcrSettings();
        Assert.Equal(20, PollInterval.SceneDetect(ocr));
        Assert.Equal(200, PollInterval.Translate(ocr));
    }

    [Fact] public void SceneDetect_And_Translate_AreIndependent()
    {
        var ocr = new OcrSettings { SceneDetectPollMs = 120, TranslatePollMs = 800 };
        Assert.Equal(120, PollInterval.SceneDetect(ocr));
        Assert.Equal(800, PollInterval.Translate(ocr));
    }

    [Theory]
    [InlineData(0)]     // 0 → 下限，防把 CPU 打满
    [InlineData(-100)]  // 负数 → 下限
    [InlineData(10)]    // 过小 → 下限
    public void BelowMin_ClampsToMin(int ms)
    {
        var ocr = new OcrSettings { SceneDetectPollMs = ms, TranslatePollMs = ms };
        Assert.Equal(PollInterval.MinMs, PollInterval.SceneDetect(ocr));
        Assert.Equal(PollInterval.MinMs, PollInterval.Translate(ocr));
    }

    [Fact] public void AboveMax_ClampsToMax()
    {
        var ocr = new OcrSettings { SceneDetectPollMs = 999_999, TranslatePollMs = 999_999 };
        Assert.Equal(PollInterval.MaxMs, PollInterval.SceneDetect(ocr));
        Assert.Equal(PollInterval.MaxMs, PollInterval.Translate(ocr));
    }

    [Fact] public void CaptureFloor_FollowsTheFasterClock()
    {
        // 识别 200 / 翻译 500：上游必须放行到 200，否则"检测 200ms"是假的。
        Assert.Equal(200, PollInterval.CaptureFloor(new OcrSettings { SceneDetectPollMs = 200, TranslatePollMs = 500 }));
        // 反过来的配置（翻译比识别还勤）也要跟着较快的那个。
        Assert.Equal(150, PollInterval.CaptureFloor(new OcrSettings { SceneDetectPollMs = 400, TranslatePollMs = 150 }));
    }

    [Fact] public void CaptureFloor_UsesClampedValues()
    {
        // 越界值先钳制再比较，不能把 0/负数原样透传给捕获层。
        Assert.Equal(PollInterval.MinMs, PollInterval.CaptureFloor(new OcrSettings { SceneDetectPollMs = 0, TranslatePollMs = 500 }));
    }
}
