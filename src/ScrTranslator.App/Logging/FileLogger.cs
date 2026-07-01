using System.IO;

namespace ScrTranslator.App.Logging;

public sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();

    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // FileShare.ReadWrite：容忍上一个实例/日志查看器仍持有文件，避免启动因文件占用而崩溃。
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _w = new StreamWriter(fs) { AutoFlush = true };
    }

    public void Log(string msg)
    {
        lock (_lock) _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Dispose() => _w.Dispose();
}
