using ScrTranslator.Core.Settings;
namespace ScrTranslator.Core.Tests.Settings;

/// <summary>Phase 5: 轮询频率接线——场景是否含 Chat ROI 决定取哪个设置值，并钳制到合理区间。</summary>
public class PollIntervalTests
{
    [Fact] public void HasChat_UsesChatPollMs()
        => Assert.Equal(250, PollInterval.Resolve(new OcrSettings { ChatPollMs = 250, OtherPollMs = 800 }, hasChat: true));

    [Fact] public void NoChat_UsesOtherPollMs()
        => Assert.Equal(800, PollInterval.Resolve(new OcrSettings { ChatPollMs = 250, OtherPollMs = 800 }, hasChat: false));

    [Theory]
    [InlineData(0)]    // 0 → 下限
    [InlineData(-100)] // 负数 → 下限
    [InlineData(50)]   // 过小 → 下限(150)：OCR+网络再快也不该低于此，防空转
    public void BelowMin_ClampsToMin(int ms)
        => Assert.Equal(PollInterval.MinMs, PollInterval.Resolve(new OcrSettings { ChatPollMs = ms, OtherPollMs = ms }, hasChat: true));

    [Fact] public void AboveMax_ClampsToMax()
        => Assert.Equal(PollInterval.MaxMs, PollInterval.Resolve(new OcrSettings { ChatPollMs = 999_999, OtherPollMs = 999_999 }, hasChat: false));

    [Fact] public void Defaults_AreWithinRange()
    {
        var ocr = new OcrSettings(); // 300 / 800
        Assert.Equal(300, PollInterval.Resolve(ocr, hasChat: true));
        Assert.Equal(800, PollInterval.Resolve(ocr, hasChat: false));
    }
}
