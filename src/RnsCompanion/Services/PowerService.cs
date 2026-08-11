using System.Runtime.InteropServices;

namespace RnsCompanion.Services;

/// <summary>Энергосбережение: гашение мониторов и сон ПК.</summary>
internal static class PowerService
{
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSysCommand = 0x0112;
    private static readonly IntPtr ScMonitorPower = new(0xF170);
    private static readonly IntPtr MonitorPowerOff = new(2);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    /// <summary>Погасить мониторы (любой ввод их разбудит).</summary>
    public static void MonitorsOff()
    {
        try
        {
            SendMessage(HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorPowerOff);
            LogService.Info("Мониторы погашены (SC_MONITORPOWER).");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Не удалось погасить мониторы: {ex.Message}");
        }
    }

    /// <summary>Увести ПК в сон. Wake-таймеры не отключаем — задача планировщика сможет разбудить ПК.</summary>
    public static void Sleep()
    {
        LogService.Info("ПК уходит в сон (SetSuspendState).");
        try
        {
            if (!SetSuspendState(false, false, false))
                LogService.Warn($"SetSuspendState вернул ошибку {Marshal.GetLastWin32Error()}.");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Не удалось увести ПК в сон: {ex.Message}");
        }
    }
}
