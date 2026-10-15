namespace ScrTranslator.Core;

/// <summary>相对内容区的矩形（0~1 比例）。跨分辨率通用：运行时乘以当前帧宽高即得物理像素。</summary>
public record RelRect(double X, double Y, double W, double H);
