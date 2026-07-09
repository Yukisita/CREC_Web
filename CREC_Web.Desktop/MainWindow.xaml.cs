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
        // WPF の閉じる処理は一度止め、非同期でサーバー停止を終えてから最終的に Close する。
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

    // プロジェクト切り替え時は既存サーバーを止めてから再起動し、読み込み中表示も同時に切り替える。
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

    // WebView2 のイベント購読は 1 回だけ行い、同一ウィンドウ内ナビゲーション制御を有効にする。
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

    // localhost 系以外の遷移は埋め込み WebView に載せず、既定ブラウザへ逃がす。
    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_closeRequested || IsAllowedInAppUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenInDefaultBrowser(e.Uri);
    }

    // target=_blank / window.open でも同じ許可ルールを適用し、不要な別ウィンドウ生成を防ぐ。
    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // about:blank はブラウザ版がポップアップ確保に使う中継ページなので、
        // desktop 版で現在のビューへ遷移させると元ページの JS 実行が中断されてしまう。
        if (string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            return;
        }

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

    // 閉じる操作ではまず WebView を空表示に戻し、裏側でサーバー停止を待ってから終了する。
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

    // 別プロジェクト読込時に直前の画面が残らないよう、読み込みオーバーレイと操作可否をまとめて制御する。
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

    // アプリ内に残すのは自前のローカル UI だけに限定し、外部サイトのセッション共有を避ける。
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

    // 外部リンクは OS 既定ブラウザへ委譲し、WebView2 側には http/https のみ渡す。
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
