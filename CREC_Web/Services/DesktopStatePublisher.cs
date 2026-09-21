using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CREC_Web.Services;

/// <summary>同じ OS ユーザー専用の通信で状態を通知する。プロジェクトのパスは公開の状態確認 API に含めない。</summary>
public sealed class DesktopStatePublisher : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;// 起動元への専用通知経路
    private readonly StreamWriter _writer;// 1通知を1行の JSON として送信する。

    /// <summary>同じ OS ユーザー専用の通知経路を初期化する。プロジェクトのパスは公開の状態確認 API に含めない。</summary>
    /// <param name="pipe">接続済みパイプ</param>
    private DesktopStatePublisher(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>5秒を上限にホストへ接続する。</summary>
    /// <param name="pipeName">ホストから渡されたパイプ名</param>
    /// <returns>接続済みの通知サービス。失敗時は例外</returns>
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

    /// <summary>状態変化と、停止時の最終状態を通知する。</summary>
    /// <param name="runtime">状態の取得元</param>
    /// <param name="stop">全要求の完了後に通知する停止トークン</param>
    /// <returns>最終状態の送信完了</returns>
    public async Task RunAsync(ProjectRuntime runtime, CancellationToken stop)
    {
        ProjectState? lastPublishedState = null;// 重複通知を避ける比較用
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
            // 呼び出し元は、Kestrel が処理中の要求をすべて完了してから、このループを停止する。
            await _writer.WriteLineAsync(JsonSerializer.Serialize(runtime.Current));
        }
    }

    /// <summary>通知用ライターとパイプを順に解放する。</summary>
    /// <returns>リソースの解放完了</returns>
    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _pipe.DisposeAsync();
    }
}
