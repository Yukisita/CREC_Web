using System.ComponentModel;
using System.Diagnostics;
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
            "CREC Desktop",
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
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var fullProjectPath = Path.GetFullPath(projectPath.Trim());
            if (!File.Exists(fullProjectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetLoadingState(true, fullProjectPath);

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
            LoadingHost.Visibility = Visibility.Collapsed;
            _currentProjectPath = fullProjectPath;
            _currentPublishToNetwork = PublishCheckBox.IsChecked == true;
            Title = $"CREC Desktop - {Path.GetFileNameWithoutExtension(fullProjectPath)}";
        }
        catch (Exception ex)
        {
            if (_closeRequested)
            {
                return;
            }
            BrowserHost.Visibility = Visibility.Collapsed;
            LoadingHost.Visibility = Visibility.Collapsed;
            Title = "CREC Desktop";
            MessageBox.Show(this, ex.Message, "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void InitializeBrowser()
    {
        if (_browserInitialized || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
        Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
        _browserInitialized = true;
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_closeRequested || IsAllowedInAppUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenInDefaultBrowser(e.Uri);
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (IsAllowedInAppUri(e.Uri) && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            Browser.Source = uri;
        }
        else
        {
            OpenInDefaultBrowser(e.Uri);
        }

        e.Handled = true;
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            Browser.Source = new Uri("about:blank");
            BrowserHost.Visibility = Visibility.Collapsed;
            LoadingHost.Visibility = Visibility.Collapsed;
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

    private void SetLoadingState(bool isLoading, string? projectPath = null)
    {
        if (isLoading)
        {
            var projectName = string.IsNullOrWhiteSpace(projectPath)
                ? null
                : Path.GetFileNameWithoutExtension(projectPath);
            LoadingTextBlock.Text = string.IsNullOrWhiteSpace(projectName)
                ? "読み込み中..."
                : $"{projectName} を読み込み中...";
            BrowserHost.Visibility = Visibility.Collapsed;
            LoadingHost.Visibility = Visibility.Visible;
            Browser.Source = new Uri("about:blank");
            Title = string.IsNullOrWhiteSpace(projectName)
                ? "CREC Desktop - 読み込み中..."
                : $"CREC Desktop - {projectName} を読み込み中...";
        }
        else
        {
            LoadingTextBlock.Text = "読み込み中...";
            LoadingHost.Visibility = Visibility.Collapsed;
            if (!_closeRequested)
            {
                OpenProjectButton.IsEnabled = true;
                PublishCheckBox.IsEnabled = true;
            }
        }

        if (!_closeRequested)
        {
            OpenProjectButton.IsEnabled = !isLoading;
            PublishCheckBox.IsEnabled = !isLoading;
        }
    }

    private static bool IsAllowedInAppUri(string? uriText)
    {
        if (string.IsNullOrWhiteSpace(uriText))
        {
            return false;
        }

        if (string.Equals(uriText, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
            && uri.IsLoopback
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void OpenInDefaultBrowser(string? uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack && Browser.Source?.AbsolutePath != "/")
        {
            Browser.GoBack();
        }
    }
}
