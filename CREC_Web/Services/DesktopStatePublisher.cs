using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CREC_Web.Services;

/// <summary>同じ OS ユーザーのデスクトップホストへ、プロジェクトの状態を専用パイプで通知する。</summary>
public sealed class DesktopStatePublisher : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;// HTTP へ実パスを公開せず、起動元のホストだけに送る経路。
    private readonly StreamWriter _writer;// 1通知を1行の JSON として送信する。

    /// <summary>接続済みパイプに、通知を即時送信するためのライターを用意する。</summary>
    /// <param name="pipe">デスクトップホストへの接続が完了したパイプ。</param>
    private DesktopStatePublisher(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>デスクトップホストが待ち受けているパイプへ、5秒を上限に接続する。</summary>
    /// <param name="pipeName">起動元のホストが環境変数で渡したパイプ名。</param>
    /// <returns>接続済みの通知サービス。接続失敗時はパイプを破棄して例外を返す。</returns>
    public static async Task<DesktopStatePublisher> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000);
            return new(pipe);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    /// <summary>状態の変化を監視してホストに通知し、停止時には最終状態を送る。</summary>
    /// <param name="runtime">通知する現在のプロジェクト状態。</param>
    /// <param name="stop">Web サーバーの要求処理が終了した後に通知される停止トークン。</param>
    /// <returns>停止通知を受け、最後の状態を送信するまで完了しないタスク。</returns>
    public async Task RunAsync(ProjectRuntime runtime, CancellationToken stop)
    {
        ProjectState? lastPublishedState = null;// 同じ状態を200ミリ秒ごとに再送しないための比較用。
        try
        {
            while (true)
            {
                var currentState = runtime.Current;
                if (currentState != lastPublishedState)
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(currentState));
                    lastPublishedState = currentState;
                }
                await Task.Delay(200, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // 停止直前に完了した切り替えも通知し、公開設定変更後の再起動先が古いままになるのを防ぐ。
            await _writer.WriteLineAsync(JsonSerializer.Serialize(runtime.Current));
        }
    }

    /// <summary>通知用ライターとパイプを順に解放する。</summary>
    /// <returns>両方のリソースの解放完了を待つタスク。</returns>
    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _pipe.DisposeAsync();
    }
}
