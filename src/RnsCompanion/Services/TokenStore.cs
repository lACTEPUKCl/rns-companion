using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RnsCompanion.Models;

namespace RnsCompanion.Services;

/// <summary>
/// Хранение JWT в DPAPI (CurrentUser): токен может расшифровать только
/// текущий пользователь Windows на этой машине.
/// </summary>
internal sealed class TokenStore
{
    private readonly string _path = Path.Combine(LogService.DataDir, "session.dat");

    public bool Save(AuthState state)
    {
        byte[]? clear = null;
        try
        {
            clear = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state));
            AtomicFile.WriteAllBytes(_path,
                ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LogService.Warn($"Не удалось сохранить авторизацию: {ex.Message}");
            return false;
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    public AuthState? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var clear = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthState>(clear);
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
