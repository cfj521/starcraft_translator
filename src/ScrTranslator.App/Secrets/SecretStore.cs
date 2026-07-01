using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScrTranslator.App.Secrets;

public sealed class SecretStore
{
    private readonly string _path;

    public SecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "secrets.dat");
    }

    public SecretData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SecretData();
            var enc = File.ReadAllBytes(_path);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<SecretData>(json) ?? new SecretData();
        }
        catch { return new SecretData(); } // 损坏/无法解密→空
    }

    public void Save(SecretData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        var enc = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, enc);
    }
}
