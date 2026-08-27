using System.Windows;

namespace Lampa.Desktop;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Доступна Lampa {version}";
    }

    private void Update_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Defer_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
