using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfApplication = System.Windows.Application;

namespace RnsCompanion.Services;

/// <summary>
/// Доступ к логотипу (звезда РНС): PNG для UI и многослойный ICO для трея.
/// Ресурсы вшиты в сборку (Assets/logo.png, Assets/app.ico).
/// </summary>
internal static class LogoService
{
    private static ImageSource? _logo;

    /// <summary>Логотип для шапки окна / страницы «О программе».</summary>
    public static ImageSource Logo => _logo ??= LoadLogo();

    private static ImageSource LoadLogo()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri("pack://application:,,,/Assets/logo.png", UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Иконка трея из многослойного app.ico (16…256).</summary>
    public static System.Drawing.Icon CreateTrayIcon()
    {
        var streamInfo = WpfApplication.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("Ресурс Assets/app.ico не найден.");
        using var stream = streamInfo.Stream;
        // Копируем: Icon должен пережить освобождение потока.
        using var temp = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)temp.Clone();
    }
}
