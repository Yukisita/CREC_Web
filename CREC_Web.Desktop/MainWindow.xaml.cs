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
    /// <summary>ブラウザの遷移先。</summary>
    private enum BrowserNavigationMode
    {
        Ignore,// ナビゲーションを無視する
        InApp,// アプリ内でナビゲーションする
        External// 外部ブラウザでナビゲーションする
    }

    private readonly WebServerHost _webServerHost = new();// 子サーバーとプロジェクト状態の管理。
    private readonly string? _startupProjectPath;// コマンドライン引数から取得した起動時の .crec ファイルパス
    private string? _currentProjectPath;// 現在開いているプロジェクトのパス
    private bool _browserInitialized;// 遷移イベントの二重登録を防ぐ。
    private bool _closeRequested;// 二重終了と、終了中の画面更新を防ぐ。
    private bool _closeConfirmed;// サーバー停止後の Close を許可する。
    private bool _currentPublishToNetwork;// 稼働中の公開設定。変更の取り消し時に使う。

    /// <summary>画面とプロジェクト変更通知を初期化する。</summary>
    public MainWindow()
    {
        InitializeComponent();
        _webServerHost.ProjectChanged += HandleProjectChanged;

        _startupProjectPath = Array.Find(
            Environment.GetCommandLineArgs(),
            argument => argument.EndsWith(".crec", StringComparison.OrdinalIgnoreCase));
        Loaded += MainWindow_Loaded;
    }

    /// <summary>切り替え結果を UI スレッドへ反映する。</summary>
    /// <param name="state">受信したプロジェクト状態。</param>
    /// <returns>なし。画面更新は UI スレッドへ予約する。</returns>
    private void HandleProjectChanged(DesktopProjectState state)
    {
        // パイプの受信スレッドから直接 WPF の表示を書き換えない。
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_closeRequested)
            {
                return;
            }

            _currentProjectPath = state.FilePath;
            Title = $"CREC Desktop - {state.Name}";
        });
    }

    /// <summary>サーバー停止を待ってから画面を閉じる。</summary>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
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
        // WPF の閉じる処理は一度止め、非同期でサーバー停止を終えてから最終的にCloseする
        _ = ShutdownAndCloseAsync();
    }

    /// <summary>引数で指定されたプロジェクトを起動する。</summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_startupProjectPath))
        {
            await OpenProjectAsync(_startupProjectPath);
        }
    }

    /// <summary>ファイル選択ダイアログからプロジェクトを開く。</summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
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

    /// <summary>確認後にサーバーを再起動し、公開設定を反映する。</summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
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

        await OpenProjectAsync(_currentProjectPath, preserveCurrentProject: true);
    }

    /// <summary>プロジェクトを起動し、WebView2 に表示する。</summary>
    /// <param name="projectPath">起動する .crec のパス。</param>
    /// <param name="preserveCurrentProject">停止直前のプロジェクトを引き継ぐか。</param>
    /// <returns>起動・画面表示の完了。</returns>
    private async Task OpenProjectAsync(string projectPath, bool preserveCurrentProject = false)
    {
        try
        {
            var fullProjectPath = string.IsNullOrWhiteSpace(projectPath)
                ? string.Empty : Path.GetFullPath(projectPath.Trim());
            if (!File.Exists(fullProjectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetConfiguredPort(out var port))
            {
                return;
            }

            ShowLoadingState(fullProjectPath);

            if (_webServerHost.IsRunning)
            {
                // 停止待ちの間に切り替わる場合もあるため、最終通知を受けてから起動先を選ぶ。
                await _webServerHost.StopAsync();
                if (preserveCurrentProject && _webServerHost.CurrentProject is { } finalState)
                    fullProjectPath = finalState.FilePath;
            }

            var launchSettings = new DesktopLaunchSettings(fullProjectPath, port, PublishCheckBox.IsChecked == true);
            var session = await _webServerHost.StartAsync(launchSettings);

            await Browser.EnsureCoreWebView2Async();
            InitializeBrowser();

            if (_closeRequested)
            {
                return;
            }

            Browser.Source = session.FrontendUri;
            BrowserHost.Visibility = Visibility.Visible;
            LoadingHost.Visibility = Visibility.Collapsed;
            _currentProjectPath = _webServerHost.CurrentProject?.FilePath ?? fullProjectPath;
            _currentPublishToNetwork = PublishCheckBox.IsChecked == true;
            Title = $"CREC Desktop - {_webServerHost.CurrentProject?.Name ?? Path.GetFileNameWithoutExtension(fullProjectPath)}";
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
            HideLoadingState();
        }
    }

    /// <summary>WebView2 の遷移イベントを登録する。</summary>
    /// <returns>なし。</returns>
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

    /// <summary>外部リンクを既定のブラウザへ渡す。</summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_closeRequested)
        {
            return;
        }

        if (ResolveBrowserNavigation(e.Uri, ignoreAboutBlankPopup: false, out var targetUri) != BrowserNavigationMode.External
            || targetUri is null)
        {
            return;
        }

        e.Cancel = true;
        OpenInDefaultBrowser(targetUri);
    }

    /// <summary>新規ウィンドウの要求を適切な遷移先へ振り分ける。</summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        switch (ResolveBrowserNavigation(e.Uri, ignoreAboutBlankPopup: true, out var targetUri))
        {
            case BrowserNavigationMode.InApp when targetUri is not null:
                Browser.Source = targetUri;
                break;
            case BrowserNavigationMode.External when targetUri is not null:
                OpenInDefaultBrowser(targetUri);
                break;
        }

        e.Handled = true;
    }

    /// <summary>サーバーを停止し、画面を閉じる。</summary>
    /// <returns>サーバー停止と画面終了を待つタスク。</returns>
    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            Browser.Source = new Uri("about:blank");
            BrowserHost.Visibility = Visibility.Collapsed;
            LoadingHost.Visibility = Visibility.Collapsed;
            await _webServerHost.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"サーバーの停止中にエラーが発生しました。\n{ex.Message}",
                "CREC Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>読み込み中の表示へ切り替える。</summary>
    /// <param name="projectPath">プロジェクトのパス</param>
    /// <returns>なし。</returns>
    private void ShowLoadingState(string? projectPath = null)
    {
        var projectName = string.IsNullOrWhiteSpace(projectPath)
            ? null
            : Path.GetFileNameWithoutExtension(projectPath);

        ApplyLoadingMessage(projectName);
        BrowserHost.Visibility = Visibility.Collapsed;
        LoadingHost.Visibility = Visibility.Visible;

        if (!_closeRequested)
        {
            SetLauncherControlsEnabled(false);
        }
    }

    /// <summary>読み込み中の表示を解除する。</summary>
    /// <returns>なし。</returns>
    private void HideLoadingState()
    {
        ApplyLoadingMessage();
        LoadingHost.Visibility = Visibility.Collapsed;

        if (!_closeRequested)
        {
            SetLauncherControlsEnabled(true);
        }
    }

    /// <summary>読み込み中の文言とタイトルを更新する。</summary>
    /// <param name="projectName">表示対象のプロジェクト名</param>
    /// <returns>なし。</returns>
    private void ApplyLoadingMessage(string? projectName = null)
    {
        LoadingTextBlock.Text = string.IsNullOrWhiteSpace(projectName)
            ? "読み込み中..."
            : $"{projectName} を読み込み中...";
        Title = string.IsNullOrWhiteSpace(projectName)
            ? "CREC Desktop - 読み込み中..."
            : $"CREC Desktop - {projectName} を読み込み中...";
    }

    /// <summary>起動設定の入力可否を切り替える。</summary>
    /// <param name="isEnabled">有効にする場合は true</param>
    /// <returns>なし。</returns>
    private void SetLauncherControlsEnabled(bool isEnabled)
    {
        OpenProjectButton.IsEnabled = isEnabled;
        PortTextBox.IsEnabled = isEnabled;
        PublishCheckBox.IsEnabled = isEnabled;
    }

    /// <summary>入力されたポート番号を検証する。</summary>
    /// <param name="port">取得したポート番号</param>
    /// <returns>ポート番号が有効な範囲内であるかどうか</returns>
    private bool TryGetConfiguredPort(out int port)
    {
        port = 0;
        var portText = PortTextBox.Text?.Trim();
        if (!int.TryParse(portText, out port) || port < 1 || port > 65534)
        {
            MessageBox.Show(this, "ポート番号は 1 から 65534 の範囲で入力してください。", "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortTextBox.Focus();
            PortTextBox.SelectAll();
            return false;
        }

        return true;
    }

    /// <summary>URI からブラウザの遷移先を決める。</summary>
    /// <param name="uriText">解析対象の URI 文字列</param>
    /// <param name="ignoreAboutBlankPopup">"about:blank" のポップアップを無視するかどうか</param>
    /// <param name="targetUri">解析結果の URI</param>
    /// <returns>ブラウザの遷移モード</returns>
    private static BrowserNavigationMode ResolveBrowserNavigation(string? uriText, bool ignoreAboutBlankPopup, out Uri? targetUri)
    {
        targetUri = null;

        if (string.IsNullOrWhiteSpace(uriText))
        {
            return BrowserNavigationMode.Ignore;
        }

        if (string.Equals(uriText, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return ignoreAboutBlankPopup ? BrowserNavigationMode.Ignore : BrowserNavigationMode.InApp;
        }

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out targetUri))
        {
            return BrowserNavigationMode.Ignore;
        }

        if (targetUri.IsLoopback && (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps))
        {
            return BrowserNavigationMode.InApp;
        }

        if (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps)
        {
            return BrowserNavigationMode.External;
        }

        targetUri = null;
        return BrowserNavigationMode.Ignore;
    }

    /// <summary>URI を既定のブラウザで開く。</summary>
    /// <param name="uri">開く対象の URI</param>
    /// <returns>なし。</returns>
    private void OpenInDefaultBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"既定のブラウザでリンクを開けませんでした。\n{ex.Message}",
                "CREC Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>ブラウザを前のページへ戻す。</summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">イベント情報。</param>
    /// <returns>なし。</returns>
    private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack && Browser.Source?.AbsolutePath != "/")
        {
            Browser.GoBack();
        }
    }
}
