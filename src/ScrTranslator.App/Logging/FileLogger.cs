using System.IO;
using System.Text;

namespace ScrTranslator.App.Logging;

/// <summary>日志级别。<see cref="Debug"/> 仅在 <see cref="FileLogger.DebugEnabled"/> 为真时落盘；其余为常规业务日志恒记。</summary>
public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 分级文件日志。业务级(Info/Warn/Error)始终写；诊断级(Debug)仅在 <see cref="DebugEnabled"/>（由 General.DebugMode 驱动）为真时写。
/// 每条带 日期+时间+毫秒+级别标签；超过 <see cref="MaxBytes"/> 轮转到 .1（保留一份，失败则跳过不崩）。
/// FileShare.ReadWrite：容忍上一实例/日志查看器占用，且允许外部实时 tail。
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<string> _recent = new();
    private StreamWriter? _w;
    private long _bytes;
    private const long MaxBytes = 5L * 1024 * 1024;
    private const int MaxRecent = 2000; // 内存缓冲：日志页实时视图，超出丢最旧

    /// <summary>诊断级开关。由 App/探测按 General.DebugMode 实时设置；volatile 保证跨线程立即可见。</summary>
    public volatile bool DebugEnabled;

    /// <summary>每写一条日志（通过级别过滤后）触发。供配置页"日志"标签实时追加；订阅方需自行 marshal 到 UI 线程。</summary>
    public event Action<string>? LogWritten;

    /// <summary>当前内存缓冲的日志行快照（含本次运行以来的全部已写行，最新在后）。供日志页打开时一次性载入历史。</summary>
    public IReadOnlyList<string> RecentLines { get { lock (_lock) return _recent.ToArray(); } }

    public FileLogger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Open();
    }

    private void Open()
    {
        var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _w = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        try { _bytes = new FileInfo(_path).Length; } catch { _bytes = 0; }
    }

    // 兼容旧调用：默认按业务级(Info)。诊断点显式改用 Debug()。
    public void Log(string msg) => Write(LogLevel.Info, msg);
    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg) => Write(LogLevel.Info, msg);
    public void Warn(string msg) => Write(LogLevel.Warn, msg);
    public void Error(string msg) => Write(LogLevel.Error, msg);
    public void Write(LogLevel level, string msg)
    {
        if (level == LogLevel.Debug && !DebugEnabled) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {msg}";
        lock (_lock)
        {
            if (_bytes > MaxBytes) Rotate();
            _w!.WriteLine(line);
            _bytes += Encoding.UTF8.GetByteCount(line) + 2;
            _recent.Add(line);
            while (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        }
        LogWritten?.Invoke(line); // 锁外触发：订阅方（UI）回写本 logger 不会死锁
    }

    /// <summary>把当前日志改名为 .1（覆盖旧的），重开新文件。查看器占用导致 Move 失败时静默跳过，继续追加不崩。</summary>
    private void Rotate()
    {
        try
        {
            _w?.Flush(); _w?.Dispose();
            string bak = _path + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(_path, bak);
            Open();
        }
        catch
        {
            // 轮转失败：确保仍有可用 writer，重置计数避免每行都重试轮转。
            if (_w is null) try { Open(); } catch { }
            _bytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock) { try { _w?.Flush(); _w?.Dispose(); } catch { } _w = null; }
    }
}
