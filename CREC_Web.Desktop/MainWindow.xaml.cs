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
    /// <summary>
    /// ブラウザの遷移モードを表す列挙型
    /// </summary>
    private enum BrowserNavigationMode
    {
        Ignore,// ナビゲーションを無視する
        InApp,// アプリ内でナビゲーションする
        External// 外部ブラウザでナビゲーションする
    }

    private readonly WebServerHost _webServerHost = new();// WebServerHost のインスタンスを作成
    private readonly string? _startupProjectPath;// コマンドライン引数から取得した起動時の .crec ファイルパス
    private string? _currentProjectPath;// 現在開いているプロジェクトのパス
    private bool _browserInitialized;// WebView2 の初期化が完了したかどうかを示すフラグ
    private bool _closeRequested;// ウィンドウの閉じる操作が要求されたかどうかを示すフラグ
    private bool _closeConfirmed;// ウィンドウの閉じる操作が確認されたかどうかを示すフラグ
    private bool _currentPublishToNetwork;// 現在の公開設定がネットワーク公開かどうかを示すフラグ

    /// <summary>
    /// MainWindow クラスのコンストラクタ
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        _startupProjectPath = Array.Find(
            Environment.GetCommandLineArgs(),
            argument => argument.EndsWith(".crec", StringComparison.OrdinalIgnoreCase));
        Loaded += MainWindow_Loaded;
    }

    /// <summary>
    /// ウィンドウの閉じる操作が要求されたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="e"></param>
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

    /// <summary>
    /// ウィンドウが読み込まれたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_startupProjectPath))
        {
            await OpenProjectAsync(_startupProjectPath);
        }
    }

    /// <summary>
    /// 「プロジェクトを開く」ボタンがクリックされたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
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

    /// <summary>
    /// ネットワーク公開設定のチェックボックスがクリックされたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
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

    /// <summary>
    /// 指定されたプロジェクトファイルを開き、Web サーバーを起動して WebView2 に表示する非同期メソッド
    /// </summary>
    /// <param name="projectPath"></param>
    /// <returns></returns>
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

            if (!TryGetConfiguredPort(out var port))
            {
                return;
            }

            SetLoadingState(true, fullProjectPath);

            // プロジェクト切り替え時は既存サーバーを止めてから再起動し、読み込み中表示も同時に切り替える
            if (_webServerHost.IsRunning)
            {
                await _webServerHost.StopAsync();
            }

            var session = await _webServerHost.StartAsync(new DesktopLaunchSettings(fullProjectPath, port, PublishCheckBox.IsChecked == true));

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

    /// <summary>
    /// WebView2 の初期化を行い、ナビゲーションイベントのハンドラを登録するメソッド
    /// </summary>
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

    /// <summary>
    /// WebView2 でナビゲーションが開始されたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
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

    /// <summary>
    /// WebView2 で新しいウィンドウが要求されたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
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

    /// <summary>
    /// Web サーバーを停止し、ウィンドウを閉じる非同期メソッド
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    /// 読み込み中の状態を設定するメソッド
    /// </summary>
    /// <param name="isLoading">読み込み中のフラグ  </param>
    /// <param name="projectPath">プロジェクトのパス</param>
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
                PortTextBox.IsEnabled = true;
                PublishCheckBox.IsEnabled = true;
            }
        }

        if (!_closeRequested)
        {
            OpenProjectButton.IsEnabled = !isLoading;
            PortTextBox.IsEnabled = !isLoading;
            PublishCheckBox.IsEnabled = !isLoading;
        }
    }

    /// <summary>
    /// ユーザーが入力したポート番号を取得し、1 から 65534 の範囲内であるかを検証するメソッド
    /// </summary>
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

    /// <summary>
    /// 指定された URI を解析し、ブラウザの遷移モードを決定するメソッド
    /// </summary>
    /// <param name="uriText">解析対象の URI 文字列</param>
    /// <param name="ignoreAboutBlankPopup">"about:blank" のポップアップを無視するかどうか</param>
    /// <param name="targetUri">解析結果の URI</param>
    /// <returns>ブラウザの遷移モード</returns>
    private static BrowserNavigationMode ResolveBrowserNavigation(string? uriText, bool ignoreAboutBlankPopup, out Uri? targetUri)
    {
        targetUri = null;

        if (string.IsNullOrWhiteSpace(uriText))// URI が null または空文字の場合は無視する
        {
            return BrowserNavigationMode.Ignore;
        }

        if (string.Equals(uriText, "about:blank", StringComparison.OrdinalIgnoreCase))// "about:blank" の場合は、ignoreAboutBlankPopup フラグに応じて遷移モードを決定する
        {
            return ignoreAboutBlankPopup ? BrowserNavigationMode.Ignore : BrowserNavigationMode.InApp;
        }

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out targetUri))// URI の解析に失敗した場合は無視する
        {
            return BrowserNavigationMode.Ignore;
        }

        if (targetUri.IsLoopback && (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps))// ループバックアドレスの場合はアプリ内で遷移する
        {
            return BrowserNavigationMode.InApp;
        }

        if (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps)// ループバックアドレス以外の HTTP/HTTPS の場合は外部ブラウザで遷移する
        {
            return BrowserNavigationMode.External;
        }

        targetUri = null;
        return BrowserNavigationMode.Ignore;
    }

    /// <summary>
    /// 指定された URI を OS 既定のブラウザで開くメソッド
    /// </summary>
    /// <param name="uri">開く対象の URI</param>
    private static void OpenInDefaultBrowser(Uri uri)
    {
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

    /// <summary>
    /// ブラウザの戻るボタンがクリックされたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack && Browser.Source?.AbsolutePath != "/")
        {
            Browser.GoBack();
        }
    }
}
