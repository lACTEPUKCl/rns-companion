using Microsoft.Win32;

namespace RnsCompanion.Services;

/// <summary>
/// Регистрация custom protocol rnscompanion:// в HKCU\Software\Classes —
/// per-user, прав администратора не требует.
/// </summary>
internal static class ProtocolRegistration
{
    public static void Register(string scheme)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь приложения.");

        using var protocol = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
        protocol.SetValue(string.Empty, "URL:RNS Companion Protocol");
        protocol.SetValue("URL Protocol", string.Empty);
        using var command = protocol.CreateSubKey(@"shell\open\command");
        command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
    }
}
