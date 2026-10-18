using System.Windows;
using Wpf.Ui.Controls;

namespace DeepUninstall.App;

public partial class MainWindow : FluentWindow
{
    public const string ProjectUrl = "https://github.com/zimba7768/DeepUninstall";

    public MainWindow()
    {
        InitializeComponent();
        App.ApplyChrome(this);
        VersionText.Text = $"Version {AppVersion}";
    }

    public static string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent, before every page exists.
        if (ProgramsPage is null || BackupsPage is null || AboutPage is null) return;

        ProgramsPage.Visibility = ProgramsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BackupsPage.Visibility = BackupsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = AboutNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (BackupsNav.IsChecked == true) BackupsPage.Reload();
    }
}
