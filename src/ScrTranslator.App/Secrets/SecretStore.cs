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

    /// <summary>进程内读写锁：Load 与 Save 互斥。
    /// 旧实现无锁：Save 的 File.WriteAllBytes 会先把文件截断成 0 再写，
    /// 并发的 Load 读到半截 → Unprotect/反序列化失败 → 返回空 SecretData
    /// → 调用方"Load→改一个字段→Save"把它写回 → **所有密钥一次被抹掉**。</summary>
    private readonly object _ioGate = new();

    /// <summary>DPAPI 解密结果缓存：Load 在翻译热路径上被高频调用（每帧 6 个 provider 各一次 GetStatus）。
    /// 只缓存"解密后的明文 JSON 字符串"，每次仍反序列化成新对象——调用方拿到的一定是独立实例，
    /// "读→改→写"不会互相污染。文件时间/长度变化时自动失效。</summary>
    private string? _cachedJson;
    private (DateTime Utc, long Length) _cachedStamp;

    public SecretStore(FileLogger? log = null, string? path = null)
    {
        _log = log;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "secrets.dat");
    }

    public SecretData Load()
    {
        lock (_ioGate) return LoadLocked();
    }

    private SecretData LoadLocked()
    {
        try
        {
            if (!File.Exists(_path)) return new SecretData(); // 首跑无文件属正常，不记（Load 被高频调用，记了会刷屏）
            var json = ReadPlainJson();
            var data = JsonSerializer.Deserialize<SecretData>(json) ?? new SecretData();
            // JSON 里显式写 "OpenAi": null / "Aliyun": null 时 STJ 会把属性赋成 null（init 默认值失效），
            // 下游 _store.Load().OpenAi.AccessToken 直接 NRE，而 ChatGptSubscriptionProvider.GetStatus()
            // 在编排器的 try 之外调用 → 每帧穿透 TranslateAsync。这里统一补回非 null。
            data.OpenAi ??= new OpenAiTokens();
            data.Aliyun ??= new AliyunSecrets();
            data.DashScopeApiKey ??= "";
            data.OpenAiApiKey ??= "";
            data.CustomModelApiKey ??= "";
            return data;
        }
        // 解密/反序列化失败＝已有密钥被静默清空成"未配置/未登录"，这是"状态不对却无处可查"的头号来源，必须恒记(Warn)。
        catch (Exception ex)
        {
            _log?.Warn($"[app][secrets] secrets.dat 读取失败，按空密钥处理（所有 provider 显示未配置/未登录）: {ex.GetType().Name}: {ex.Message}");
            QuarantineCorruptFile(); // 坏文件挪到 .corrupt-*：既不阻断新建，也给用户留一份可手动抢救的原始数据
            return new SecretData();
        }
    }

    /// <summary>读文件并解密成明文 JSON，带 (修改时间,长度) 戳缓存。
    /// 优化点：Load 位于翻译热路径（每帧 6 个 provider 各调一次 GetStatus），
    /// 每次都 File.ReadAllBytes + DPAPI Unprotect 属纯浪费，同时每次都是一个可被并发写撞上的读窗口。</summary>
    private string ReadPlainJson()
    {
        var fi = new FileInfo(_path);
        var stamp = (fi.LastWriteTimeUtc, fi.Length);
        if (_cachedJson is not null && stamp == _cachedStamp) return _cachedJson;
        var enc = ReadWithRetry();
        _cachedJson = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
        try { var fi2 = new FileInfo(_path); _cachedStamp = (fi2.LastWriteTimeUtc, fi2.Length); } catch { _cachedStamp = stamp; }
        return _cachedJson;
    }

    /// <summary>读文件，遇共享冲突重试几次（杀软/外部工具短暂占用）。仍失败才抛出由上层按"损坏"处理。</summary>
    private byte[] ReadWithRetry()
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (i < 2)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>把读不出来的文件改名保存一份（best effort）。成功后原来那个位置空出来，
    /// 用户重新填的密钥可以正常写入；同时原始字节还在，事后可人工救回。</summary>
    private void QuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_path)) return;
            string bak = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, bak, overwrite: true);
            _cachedJson = null; // 文件已挪走：缓存必须失效，否则下一次 Load 还会返回旧明文
            _log?.Warn($"[app][secrets] 已把损坏的 secrets.dat 备份为 {Path.GetFileName(bak)}");
        }
        catch (Exception ex)
        {
            _log?.Warn($"[app][secrets] 备份损坏文件失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Save(SecretData data)
    {
        lock (_ioGate)
        {
            try
            {
                WriteLocked(data);
            }
            // 落盘失败＝用户刚填的密钥丢了。记 Error 后重新抛出：不吞（吞了会让调用方误以为保存成功），也不在这里弹框（由上层处理）。
            catch (Exception ex)
            {
                _log?.Error($"[app][secrets] secrets.dat 保存失败: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>原子写：加密后的字节先写 .tmp，再 File.Move 覆盖。
    /// 旧实现直接 WriteAllBytes 覆盖原文件：写到一半被杀/断电 → 文件截断 → 之后每次 Load 都失败 → 全部密钥丢失。</summary>
    private void WriteLocked(SecretData data)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); // 裸文件名(无目录)时跳过,避免 ArgumentException
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        var enc = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, enc);
        File.Move(tmp, _path, overwrite: true);
        _cachedJson = null; // 让下一次 Load 重新按新时间戳解密（也可以直接塞 json，但反正马上要序列化，少一份状态更安全）
    }

    /// <summary>在锁内"读→改→写"整段完成：多线程/多窗口同时改不同字段时不会互相覆盖。
    /// 旧写法 `var data = Load(); data.X = ...; Save(data);` 是两个独立加锁的步骤，
    /// 两方并发时后写者用自己读到的旧快照覆盖对方刚存的字段（lost update）。</summary>
    public void Update(Action<SecretData> mutate)
    {
        lock (_ioGate)
        {
            var data = LoadLocked();
            mutate(data);
            try
            {
                WriteLocked(data);
            }
            catch (Exception ex)
            {
                _log?.Error($"[app][secrets] secrets.dat 保存失败: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }
}
