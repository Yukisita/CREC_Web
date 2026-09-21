using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private Uri? _frontendUri;// このウィンドウが起動したサーバーの接続先
    private bool _isSelectingProject;// ファイル選択の多重起動を防ぐ

    /// <summary>
    /// MainWindow クラスのコンストラクタ
    /// </summary>
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
    /// <param name="state">受信したプロジェクト状態</param>
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
            Title = state.FilePath is null ? "CREC Desktop" : $"CREC Desktop - {state.Name}";
        });
    }

    /// <summary>
    /// ウィンドウの閉じる操作が要求されたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
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
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await OpenProjectAsync(_startupProjectPath);
    }

    /// <summary>
    /// 任意の場所にあるプロジェクトを選び、既存の起動処理へ渡す。
    /// </summary>
    /// <returns>選択のキャンセル、または起動・画面表示の完了</returns>
    private async Task BrowseProjectAsync()
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

    /// <summary>起動に失敗した場合、未選択状態で選択画面を開き直す。</summary>
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
    private async void RetryStartupButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenProjectAsync(null);
    }

    /// <summary>
    /// ネットワーク公開設定のチェックボックスがクリックされたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
    private async void PublishCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || _isSelectingProject || !_webServerHost.IsRunning)
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

    /// <summary>
    /// 指定されたプロジェクトファイルを開き、Web サーバーを起動して WebView2 に表示する非同期メソッド
    /// </summary>
    /// <param name="projectPath">起動する .crec のパス。未選択なら null</param>
    /// <param name="preserveCurrentProject">停止直前のプロジェクトを引き継ぐか</param>
    /// <returns>起動・画面表示の完了</returns>
    private async Task OpenProjectAsync(string? projectPath, bool preserveCurrentProject = false)
    {
        StartupErrorHost.Visibility = Visibility.Collapsed;
        try
        {
            var fullProjectPath = string.IsNullOrWhiteSpace(projectPath)
                ? null : Path.GetFullPath(projectPath.Trim());
            if (fullProjectPath is not null && !File.Exists(fullProjectPath))
            {
                MessageBox.Show(this, "起動する .crec ファイルを指定してください。", "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (!_webServerHost.IsRunning) StartupErrorHost.Visibility = Visibility.Visible;
                return;
            }

            if (!TryGetConfiguredPort(out var port))
            {
                if (!_webServerHost.IsRunning) StartupErrorHost.Visibility = Visibility.Visible;
                return;
            }

            ShowLoadingState(fullProjectPath);

            // 古い画面の監視・通信を終了してから、サーバーの世代を切り替える。
            await ClearBrowserAsync();
            if (_closeRequested)
                return;

            // プロジェクト切り替え時は既存サーバーを止めてから再起動し、読み込み中表示も同時に切り替える
            if (_webServerHost.IsRunning)
            {
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

            _frontendUri = session.FrontendUri;
            // 同じ URL でも必ず新しい画面と世代を読み込む。
            Browser.CoreWebView2.Navigate(session.FrontendUri.AbsoluteUri);
            BrowserHost.Visibility = Visibility.Visible;
            LoadingHost.Visibility = Visibility.Collapsed;
            _currentProjectPath = _webServerHost.CurrentProject?.FilePath ?? fullProjectPath;
            _currentPublishToNetwork = PublishCheckBox.IsChecked == true;
            Title = _currentProjectPath is null ? "CREC Desktop"
                : $"CREC Desktop - {_webServerHost.CurrentProject?.Name ?? Path.GetFileNameWithoutExtension(_currentProjectPath)}";
        }
        catch (Exception ex)
        {
            if (_closeRequested)
            {
                return;
            }
            BrowserHost.Visibility = Visibility.Collapsed;
            LoadingHost.Visibility = Visibility.Collapsed;
            StartupErrorHost.Visibility = Visibility.Visible;
            Title = "CREC Desktop";
            MessageBox.Show(this, ex.Message, "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideLoadingState();
        }
    }

    /// <summary>空白ページへの遷移完了を待ち、切り替え前の画面を終了する。</summary>
    /// <returns>古い画面の終了。初回起動で WebView2 が未初期化なら何もしない。</returns>
    private async Task ClearBrowserAsync()
    {
        var browser = Browser.CoreWebView2;
        if (browser is null)
            return;

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri == "about:blank") navigationId = e.NavigationId;
        }
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // 直前のページの読み込み中止通知を、空白ページの完了と取り違えない。
            if (e.NavigationId != navigationId) return;
            if (e.IsSuccess) completed.TrySetResult();
            else completed.TrySetException(new InvalidOperationException("切り替え前の画面を終了できませんでした。"));
        }

        browser.NavigationStarting += OnStarting;
        browser.NavigationCompleted += OnCompleted;
        try
        {
            browser.Navigate("about:blank");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            browser.NavigationStarting -= OnStarting;
            browser.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>
    /// WebView2 の初期化を行い、ナビゲーションイベントのハンドラを登録するメソッド
    /// </summary>
    /// <returns>なし</returns>
    private void InitializeBrowser()
    {
        if (_browserInitialized || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
        Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
        Browser.CoreWebView2.WebMessageReceived += Browser_WebMessageReceived;
        _browserInitialized = true;
    }

    /// <summary>自分で起動したサーバーの画面から届いたファイル選択要求を受け付ける。</summary>
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">送信元とメッセージ</param>
    /// <returns>なし。ファイル選択はイベント処理終了後に開始する。</returns>
    private void Browser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closeRequested || _isSelectingProject || !PortTextBox.IsEnabled
            || !IsCurrentAppPage(e.Source) || !IsCurrentAppPage(Browser.CoreWebView2.Source))
            return;

        using var message = JsonDocument.Parse(e.WebMessageAsJson);
        if (message.RootElement.ValueKind != JsonValueKind.String
            || message.RootElement.GetString() != "crec-open-project")
            return;

        _isSelectingProject = true;
        // WebView2 のイベント内でモーダルを開かず、イベントが戻った後に表示する。
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (!_closeRequested && IsCurrentAppPage(Browser.CoreWebView2.Source))
                    await BrowseProjectAsync();
            }
            catch (Exception ex)
            {
                if (!_closeRequested)
                    MessageBox.Show(this, ex.Message, "CREC Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSelectingProject = false;
            }
        });
    }

    /// <summary>現在のサーバーと同じスキーム・ホスト・ポートの画面か確認する。</summary>
    /// <param name="source">確認する画面の URL</param>
    /// <returns>現在のサーバーの画面なら true</returns>
    private bool IsCurrentAppPage(string source) => _webServerHost.IsRunning && _frontendUri is not null
        && Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.GetLeftPart(UriPartial.Authority).Equals(
            _frontendUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WebView2 でナビゲーションが開始されたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
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
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
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
    /// <returns>サーバー停止と画面終了を待つタスク</returns>
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

    /// <summary>
    /// 読み込み中オーバーレイを表示し、対象プロジェクト名に合わせて文言を更新するメソッド
    /// </summary>
    /// <param name="projectPath">プロジェクトのパス</param>
    /// <returns>なし</returns>
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

    /// <summary>
    /// 読み込み中オーバーレイを閉じ、次回表示用に既定の文言へ戻すメソッド
    /// </summary>
    /// <returns>なし</returns>
    private void HideLoadingState()
    {
        ApplyLoadingMessage();
        LoadingHost.Visibility = Visibility.Collapsed;

        if (!_closeRequested)
        {
            SetLauncherControlsEnabled(true);
        }
    }

    /// <summary>
    /// 読み込みオーバーレイの文言とタイトルを更新するメソッド
    /// </summary>
    /// <param name="projectName">表示対象のプロジェクト名</param>
    /// <returns>なし</returns>
    private void ApplyLoadingMessage(string? projectName = null)
    {
        LoadingTextBlock.Text = string.IsNullOrWhiteSpace(projectName)
            ? "読み込み中..."
            : $"{projectName} を読み込み中...";
        Title = string.IsNullOrWhiteSpace(projectName)
            ? "CREC Desktop - 読み込み中..."
            : $"CREC Desktop - {projectName} を読み込み中...";
    }

    /// <summary>
    /// 起動設定まわりの入力 UI の有効/無効をまとめて切り替えるメソッド
    /// </summary>
    /// <param name="isEnabled">有効にする場合は true</param>
    /// <returns>なし</returns>
    private void SetLauncherControlsEnabled(bool isEnabled)
    {
        PortTextBox.IsEnabled = isEnabled;
        PublishCheckBox.IsEnabled = isEnabled;
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
    /// <returns>なし</returns>
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

    /// <summary>
    /// ブラウザの戻るボタンがクリックされたときに呼び出されるイベントハンドラ
    /// </summary>
    /// <param name="sender">イベント発生元</param>
    /// <param name="e">イベント情報</param>
    /// <returns>なし</returns>
    private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack && Browser.Source?.AbsolutePath != "/")
        {
            Browser.GoBack();
        }
    }
}
