using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Native;

namespace DynamicIsland;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadCurrentState();
    }

    private void LoadCurrentState()
    {
        HighPriorityCheckBox.IsChecked = AppSettings.Current.HighPriority;
        StartupCheckBox.IsChecked = AppSettings.Current.RunOnStartup || AppSettings.IsStartupRegistered();
        UpdateStatusText();
    }

    private void HighPriorityCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool highPriority = HighPriorityCheckBox.IsChecked == true;

        // Ketika High Priority diaktifkan, otomatis aktifkan juga Launch on Startup
        // agar saat Laptop / PC dinyalakan, Dynamic Island langsung ikut menyala
        if (highPriority)
        {
            StartupCheckBox.IsChecked = true;
        }

        bool runOnStartup = StartupCheckBox.IsChecked == true;
        AppSettings.Save(highPriority, runOnStartup);
        UpdateStatusText();
    }

    private void StartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool highPriority = HighPriorityCheckBox.IsChecked == true;
        bool runOnStartup = StartupCheckBox.IsChecked == true;

        AppSettings.Save(highPriority, runOnStartup);
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        bool hp = HighPriorityCheckBox.IsChecked == true;
        bool startup = StartupCheckBox.IsChecked == true;

        if (hp && startup)
        {
            StatusFooterText.Text = "High Priority & Auto-Start Active";
            StatusFooterText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
        }
        else if (startup)
        {
            StatusFooterText.Text = "Auto-Start on Windows Boot Active";
            StatusFooterText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
        }
        else if (hp)
        {
            StatusFooterText.Text = "High Process Priority Active";
            StatusFooterText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
        }
        else
        {
            StatusFooterText.Text = "Standard Mode";
            StatusFooterText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(136, 255, 255, 255));
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
