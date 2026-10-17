using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrTranslator.App.Logging;

namespace ScrTranslator.App.Secrets;

public sealed class SecretStore
{
    private readonly string _path;
    private readonly FileLogger? _log;

    public SecretStore(FileLogger? log = null, string? path = null)
    {
        _log = log;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "secrets.dat");
    }

    public SecretData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SecretData(); // 首跑无文件属正常，不记（Load 被高频调用，记了会刷屏）
            var enc = File.ReadAllBytes(_path);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<SecretData>(json) ?? new SecretData();
        }
        // 解密/反序列化失败＝已有密钥被静默清空成"未配置/未登录"，这是"状态不对却无处可查"的头号来源，必须恒记(Warn)。
        catch (Exception ex)
        {
            _log?.Warn($"[app][secrets] secrets.dat 读取失败，按空密钥处理（所有 provider 显示未配置/未登录）: {ex.GetType().Name}: {ex.Message}");
            return new SecretData();
        }
    }

    public void Save(SecretData data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); // 裸文件名(无目录)时跳过,避免 ArgumentException
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
            var enc = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, enc);
        }
        // 落盘失败＝用户刚填的密钥丢了。记 Error 后重新抛出：不吞（吞了会让调用方误以为保存成功），也不在这里弹框（由上层处理）。
        catch (Exception ex)
        {
            _log?.Error($"[app][secrets] secrets.dat 保存失败: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
