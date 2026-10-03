using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Windowsyikefu.Services;

/// <summary>
/// 单实例参数转发：第二个进程（例如从浏览器点 easykefu:// 链接冷启动）
/// 通过命名管道把命令行参数交给已经在跑的实例，然后自己退出。
/// 首实例收到后由 App 决定是打开绑定窗口还是把主界面提到最前。
/// </summary>
public static class SingleInstanceIpc
{
    private const string PipeName = "WindowsYiKeFu-DeepLink";

    /// <summary>参数分隔符：用不可打印字符，避免和 URL 里的内容冲突</summary>
    private const char Separator = '\u0001';

    private static CancellationTokenSource? _cts;

    /// <summary>在首实例中启动监听（非阻塞）</summary>
    public static void StartServer(Action<string[]> onArgs)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadLineAsync().ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(payload))
                    {
                        var args = payload.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                        if (args.Length > 0) onArgs(args);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { Thread.Sleep(200); } // 管道偶发异常时退避重试，避免空转
            }
        }, token);
    }

    /// <summary>尝试把参数转发给已运行的实例，成功返回 true</summary>
    public static bool TrySendToExisting(string[] args, int timeoutMs = 2500)
    {
        if (args == null || args.Length == 0) return false;

        var payload = string.Join(Separator, args.Where(a => !string.IsNullOrEmpty(a)));
        if (payload.Length == 0) return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(payload);
            return true;
        }
        catch { return false; }
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
