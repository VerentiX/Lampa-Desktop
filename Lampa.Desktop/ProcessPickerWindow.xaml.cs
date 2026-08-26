using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lampa.Desktop;

public partial class ProcessPickerWindow : Window
{
    public sealed class ProcessItem
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public bool IsSelected { get; set; }
    }

    private readonly HashSet<string> _selectedPaths;
    public List<string> SelectedPaths { get; private set; } = [];

    public ProcessPickerWindow(IEnumerable<string>? selectedPaths = null)
    {
        InitializeComponent();
        Icon = AppIconFactory.CreateWindowIcon();
        _selectedPaths = (selectedPaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        LoadProcesses();
    }

    private void LoadProcesses()
    {
        var items = Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    return string.IsNullOrWhiteSpace(path)
                        ? null
                        : new ProcessItem
                        {
                            Name = Path.GetFileNameWithoutExtension(path),
                            Path = path,
                            IsSelected = _selectedPaths.Contains(path)
                        };
                }
                catch
                {
                    return null;
                }
            })
            .Where(x => x is not null)
            .Cast<ProcessItem>()
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => x.IsSelected)
            .ThenBy(x => x.Name)
            .ToList();
        ProcessesList.ItemsSource = items;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadProcesses();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedPaths = ProcessesList.Items.Cast<ProcessItem>()
            .Where(x => x.IsSelected)
            .Select(x => x.Path)
            .ToList();
        DialogResult = true;
    }

    private void ProcessCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox || checkBox.DataContext is not ProcessItem item) return;
        item.IsSelected = checkBox.IsChecked == true;
        _selectedPaths.Clear();
        foreach (var selected in ProcessesList.Items.Cast<ProcessItem>().Where(x => x.IsSelected))
            _selectedPaths.Add(selected.Path);
        LoadProcesses();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
