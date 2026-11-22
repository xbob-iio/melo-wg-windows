using System.Windows;

namespace MeloWG.Views;

public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
    }

    public string InputText => InputBox.Text;

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>Оформленные в стиле приложения окна сообщений.</summary>
public static class Dialogs
{
    public static void Error(Window? owner, string title, string message) =>
        Show(owner, title, message, "Понятно", null, Kind.Error);

    public static bool Confirm(Window? owner, string title, string message, string confirmText, bool danger = false) =>
        Show(owner, title, message, confirmText, "Отмена", danger ? Kind.Danger : Kind.Info) == true;

    public static string? Prompt(Window? owner, string title, string label, string initial)
    {
        var dlg = Create(owner, title, label, "Сохранить", "Отмена", Kind.Info);
        dlg.InputPanel.Visibility = Visibility.Visible;
        dlg.InputBox.Text = initial;
        dlg.Loaded += (_, _) =>
        {
            dlg.InputBox.Focus();
            dlg.InputBox.SelectAll();
        };
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText) ? dlg.InputText.Trim() : null;
    }

    private enum Kind { Info, Error, Danger }

    private static bool? Show(Window? owner, string title, string message, string primary, string? secondary, Kind kind) =>
        Create(owner, title, message, primary, secondary, kind).ShowDialog();

    private static DialogWindow Create(Window? owner, string title, string message, string primary, string? secondary, Kind kind)
    {
        var dlg = new DialogWindow { Title = title };
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.PrimaryButton.Content = primary;
        if (secondary == null) dlg.SecondaryButton.Visibility = Visibility.Collapsed;
        else dlg.SecondaryButton.Content = secondary;

        if (kind != Kind.Info)
        {
            dlg.IconBadge.Background = (System.Windows.Media.Brush)dlg.FindResource("DangerSoft");
            dlg.IconGlyph.Foreground = (System.Windows.Media.Brush)dlg.FindResource("Danger");
            dlg.IconGlyph.Text = kind == Kind.Error ? "" : "";
        }
        if (kind == Kind.Danger) dlg.PrimaryButton.Style = (Style)dlg.FindResource("Btn.Danger");
        if (kind == Kind.Error) dlg.PrimaryButton.IsCancel = true;
        return dlg;
    }
}
