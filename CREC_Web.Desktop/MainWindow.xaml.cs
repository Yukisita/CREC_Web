using System.ComponentModel;
using System.IO;
using System.Windows;
using CREC_Web.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace CREC_Web.Desktop;

public partial class MainWindow : Window
{
    private readonly WebServerHost _webServerHost = new();
    private readonly string? _startupProjectPath;
    private string? _currentProjectPath;
    private bool _browserInitialized;
    private bool _closeRequested;
    private bool _closeConfirmed;
    private bool _currentPublishToNetwork;

    public MainWindow()
    {
        InitializeComponent();

        _startupProjectPath = Array.Find(
            Environment.GetCommandLineArgs(),
            argument => argument.EndsWith(".crec", StringComparison.OrdinalIgnoreCase));
        Loaded += MainWindow_Loaded;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        IsEnabled = false;
        _ = ShutdownAndCloseAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_startupProjectPath))
        {
            await OpenProjectAsync(_startupProjectPath);
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CREC Project (*.crec)|*.crec|All files (*.*)|*.*",
            Title = "CREC プロジェクトを開く",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenProjectAsync(dialog.FileName);
        }
    }

    private async void PublishCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || !_webServerHost.IsRunning || string.IsNullOrWhiteSpace(_currentProjectPath))
        {
            return;
        }

        var requestedPublishToNetwork = PublishCheckBox.IsChecked == true;
        if (requestedPublishToNetwork == _currentPublishToNetwork)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "公開設定を反映するにはサーバーの再起動が必要です。再起動しますか？",
            "CREC Web Desktop",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            PublishCheckBox.IsChecked = _currentPublishToNetwork;
            return;
        }

        await OpenProjectAsync(_currentProjectPath);
    }

    private async Task OpenProjectAsync(string projectPath)
    {
        OpenProjectButton.IsEnabled = false;
        PublishCheckBox.IsEnabled = false;

        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Web Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var fullProjectPath = Path.GetFullPath(projectPath.Trim());
            if (!File.Exists(fullProjectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Web Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BrowserHost.Visibility = Visibility.Collapsed;
            Browser.Source = new Uri("about:blank");

            if (_webServerHost.IsRunning)
            {
                await _webServerHost.StopAsync();
            }

            var session = await _webServerHost.StartAsync(new DesktopLaunchSettings(fullProjectPath, PublishCheckBox.IsChecked == true));

            await Browser.EnsureCoreWebView2Async();
            InitializeBrowser();

            if (_closeRequested)
            {
                return;
            }

            Browser.Source = session.FrontendUri;
            BrowserHost.Visibility = Visibility.Visible;
            _currentProjectPath = fullProjectPath;
            _currentPublishToNetwork = PublishCheckBox.IsChecked == true;
            Title = $"CREC Web Desktop - {Path.GetFileNameWithoutExtension(fullProjectPath)}";
        }
        catch (Exception ex)
        {
            if (_closeRequested)
            {
                return;
            }
            BrowserHost.Visibility = Visibility.Collapsed;
            Title = "CREC Web Desktop";
            MessageBox.Show(this, ex.Message, "CREC Web Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!_closeRequested)
            {
                OpenProjectButton.IsEnabled = true;
                PublishCheckBox.IsEnabled = true;
            }
        }
    }

    private void InitializeBrowser()
    {
        if (_browserInitialized || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
        _browserInitialized = true;
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            Browser.Source = uri;
        }

        e.Handled = true;
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            Browser.Source = new Uri("about:blank");
            BrowserHost.Visibility = Visibility.Collapsed;
            await _webServerHost.StopAsync();
        }
        catch
        {
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
