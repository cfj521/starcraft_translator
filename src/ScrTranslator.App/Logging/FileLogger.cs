using System.IO;

namespace ScrTranslator.App.Logging;

public sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();

    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _w = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Log(string msg)
    {
        lock (_lock) _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Dispose() => _w.Dispose();
}
