using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using MeloWG.ViewModels;

namespace MeloWG.Views;

public partial class LogWindow : Window
{
    private const int MaxLines = 6000;

    private readonly MainViewModel _vm;
    private int _lines;

    public LogWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Reload();

        _vm.LogAppended += OnLines;
        Closed += (_, _) => _vm.LogAppended -= OnLines;
        StateChanged += (_, _) =>
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void Reload()
    {
        var all = _vm.TakeLogSnapshot();
        LogBox.Text = all.Count == 0 ? "" : string.Join(Environment.NewLine, all) + Environment.NewLine;
        _lines = all.Count;
        LogBox.ScrollToEnd();
    }

    private void OnLines(IReadOnlyList<string> batch)
    {
        // Прокручиваем вниз, только если пользователь и так был внизу.
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        _lines += batch.Count;
        if (_lines > MaxLines)
        {
            // Текст разросся (подробный режим) — перечитываем хвост из журнала, а не режем TextBox построчно.
            Reload();
            return;
        }
        LogBox.AppendText(string.Join(Environment.NewLine, batch) + Environment.NewLine);
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
        _lines = 0;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
