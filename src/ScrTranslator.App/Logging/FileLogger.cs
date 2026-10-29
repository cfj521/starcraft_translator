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
        // FileShare.Delete：允许轮转时把文件改名（否则外部 tail/查看器一持有文件，File.Move 必失败）。
        var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
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
            if (_w is null) return; // 轮转后重开也失败：丢掉这一行，但绝不把异常抛给调用方
            try { _w.WriteLine(line); }
            catch { try { _w = null; Open(); } catch { _w = null; } return; } // 写失败（设备满/流被关）：重开一次，失败则丢行
            _bytes += Encoding.UTF8.GetByteCount(line) + 2;
            _recent.Add(line);
            while (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        }
        LogWritten?.Invoke(line); // 锁外触发：订阅方（UI）回写本 logger 不会死锁
    }

    /// <summary>把当前日志改名为 .1（覆盖旧的），重开新文件。查看器占用导致 Move 失败时静默跳过，继续追加不崩。</summary>
    private void Rotate()
    {
        var old = _w;
        _w = null; // 先置空：失败路径必须能重新 Open，否则下文 _w.WriteLine 会打在已 Dispose 的 writer 上
        try
        {
            old?.Flush(); old?.Dispose();
            string bak = _path + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(_path, bak);
        }
        catch
        {
            // 轮转失败（文件被外部打开且未共享删除）：丢掉旧的 writer 引用，重新打开原文件继续追加。
            // 旧实现只 `if (_w is null) Open()`；但 _w 是"已 Dispose 但非 null"→ 不重开 →
            // 之后每行都 ObjectDisposedException，而且异常是从 Info/Warn/Error 抛给业务代码的
            // （大量调用点在 catch 里，二次抛出会击杀该线程/中断 Stop 流程）。
        }
        try { Open(); }
        catch { _w = null; } // 连重开都失败：本行丢弃，但不再抛异常
        _bytes = 0;
    }

    public void Dispose()
    {
        lock (_lock) { try { _w?.Flush(); _w?.Dispose(); } catch { } _w = null; }
    }
}
