using System;
using System.Windows;
using System.Windows.Threading;
using MeloWG.ViewModels;

namespace MeloWG.Views;

public partial class LogWindow : Window
{
    private readonly MainViewModel _vm;

    public LogWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        LogBox.Text = string.Join(Environment.NewLine, vm.LogSnapshot());
        if (LogBox.Text.Length > 0) LogBox.AppendText(Environment.NewLine);
        LogBox.ScrollToEnd();

        _vm.LogAppended += OnLine;
        Closed += (_, _) => _vm.LogAppended -= OnLine;
        StateChanged += (_, _) =>
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void OnLine(string line)
    {
        // Прокручиваем вниз, только если пользователь и так был внизу.
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > 6000)
        {
            int cut = LogBox.GetCharacterIndexFromLineIndex(1000);
            if (cut > 0) LogBox.Text = LogBox.Text[cut..];
        }
        if (atEnd) LogBox.ScrollToEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            CopyText.Text = "Скопировано";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { CopyText.Text = "Копировать"; t.Stop(); };
            t.Start();
        }
        catch
        {
            // Буфер обмена занят другой программой — не критично.
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearLog();
        LogBox.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
