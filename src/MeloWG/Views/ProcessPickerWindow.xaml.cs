using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloWG.Services;

namespace MeloWG.Views;

public partial class ProcessPickerWindow : Window
{
    public sealed class Row
    {
        public Row(RunningApp app)
        {
            App = app;
            Icon = IconCache.Get(app.Path);
        }

        public RunningApp App { get; }
        public string Name => App.Name;
        public string Path => App.Path;
        public ImageSource Icon { get; }
        public Visibility WindowBadge => App.HasWindow ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly HashSet<string> _alreadyAdded;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private List<Row> _all = new();
    private bool _refreshing;

    public ProcessPickerWindow(HashSet<string> alreadyAdded)
    {
        InitializeComponent();
        _alreadyAdded = alreadyAdded;
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Пути к exe выбранных программ.</summary>
    public IReadOnlyList<string> SelectedPaths => _selected.ToList();

    private async Task LoadAsync()
    {
        var rows = await Task.Run(() => ProcessScanner.Scan()
            .Where(a => !_alreadyAdded.Contains(a.Name))
            .Select(a => new Row(a))
            .ToList());
        _all = rows;
        ApplyFilter();
        SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        string q = SearchBox.Text.Trim();
        bool showSystem = ShowSystem.IsChecked == true;
        var visible = _all
            .Where(r => showSystem || !r.App.IsSystem || r.App.HasWindow)
            .Where(r => q.Length == 0 ||
                        r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _refreshing = true;
        ProcList.ItemsSource = visible;
        foreach (var row in visible.Where(r => _selected.Contains(r.Path))) ProcList.SelectedItems.Add(row);
        _refreshing = false;

        EmptyText.Text = _all.Count == 0 ? "Нет доступных программ" : "Ничего не найдено";
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        foreach (Row r in e.AddedItems) _selected.Add(r.Path);
        foreach (Row r in e.RemovedItems) _selected.Remove(r.Path);
        UpdateCount();
    }

    private void UpdateCount()
    {
        int n = _selected.Count;
        CountText.Text = n == 0 ? "Ничего не выбрано" : "Выбрано: " + n;
        AddButton.IsEnabled = n > 0;
        AddButton.Content = n > 0 ? $"Добавить ({n})" : "Добавить";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
