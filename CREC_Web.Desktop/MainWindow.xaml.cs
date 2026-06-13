using System.ComponentModel;
using System.IO;
using System.Windows;
using CREC_Web.Desktop.Services;
using Microsoft.Win32;

namespace CREC_Web.Desktop;

public partial class MainWindow : Window
{
    private readonly WebServerHost _webServerHost = new();

    public MainWindow()
    {
        InitializeComponent();

        var startupProjectPath = Array.Find(
            Environment.GetCommandLineArgs(),
            argument => argument.EndsWith(".crec", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(startupProjectPath))
        {
            ProjectPathTextBox.Text = startupProjectPath;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _webServerHost.StopAsync().GetAwaiter().GetResult();
        base.OnClosing(e);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CREC Project (*.crec)|*.crec|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ProjectPathTextBox.Text = dialog.FileName;
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        StartStopButton.IsEnabled = false;

        try
        {
            if (_webServerHost.IsRunning)
            {
                await _webServerHost.StopAsync();
                Browser.Source = new Uri("about:blank");
                PortTextBlock.Text = "-";
                StatusTextBlock.Text = "停止中";
                StartStopButton.Content = "サーバー起動";
                return;
            }

            var projectPath = ProjectPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Web Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusTextBlock.Text = "起動中";
            var session = await _webServerHost.StartAsync(new DesktopLaunchSettings(projectPath, PublishCheckBox.IsChecked == true));

            await Browser.EnsureCoreWebView2Async();
            Browser.Source = session.FrontendUri;
            PortTextBlock.Text = session.Port.ToString();
            StatusTextBlock.Text = "起動中";
            StartStopButton.Content = "サーバー停止";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "エラー";
            MessageBox.Show(this, ex.Message, "CREC Web Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartStopButton.IsEnabled = true;
        }
    }
}
