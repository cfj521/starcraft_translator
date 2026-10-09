using System.IO;
using System.Text;

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
        // 显式 UTF-8 带 BOM：新建文件写 BOM（Append 到已有文件时 position>0，StreamWriter 自动跳过 BOM，不会插到中间），
        // 使 Get-Content(Windows PowerShell 5.1/7) 与记事本都能自动按 UTF-8 解码，不再乱码。
        _w = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
    }

    public void Log(string msg)
    {
        lock (_lock) _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Dispose() => _w.Dispose();
}
