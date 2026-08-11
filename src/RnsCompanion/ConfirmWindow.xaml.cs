using System.Windows;

namespace RnsCompanion;

/// <summary>Модальное подтверждение в стиле приложения (вместо системного MessageBox).</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText, string cancelText = "Отмена")
    {
        InitializeComponent();

        TxtTitle.Text = title;
        TxtMessage.Text = message;
        BtnConfirm.Content = confirmText;
        BtnCancel.Content = cancelText;

        // Кастомный chrome — как в SettingsWindow.
        RootBorder.SizeChanged += (_, _) => RootBorder.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 16, 16);
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        };
        BtnClose.Click += (_, _) => DialogResult = false;
        BtnCancel.Click += (_, _) => DialogResult = false;
        BtnConfirm.Click += (_, _) => DialogResult = true;
    }

    /// <summary>Показать диалог поверх owner. true — пользователь подтвердил.</summary>
    public static bool Ask(Window owner, string title, string message, string confirmText)
    {
        var w = new ConfirmWindow(title, message, confirmText) { Owner = owner };
        return w.ShowDialog() == true;
    }
}
