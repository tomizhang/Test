using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common
{
    /// <summary>
    /// 高性能异步落盘日志引擎 (Thread-Safe Async File Logger)
    /// 实现全自动异步日志落盘，自动按日期创建日志文件 (logs/trading_replay_yyyy-MM-dd.log)，0 界面阻塞与 0 主线程开销。
    /// </summary>
    public static class Logger
    {
        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private static readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private static bool _isStarted = false;
        private static readonly object _lock = new object();

        public static void EnsureStarted()
        {
            if (_isStarted) return;
            lock (_lock)
            {
                if (_isStarted) return;
                _isStarted = true;
                Task.Run(() => LogWriterLoopAsync(_cts.Token));
            }
        }

        public static void Log(string message)
        {
            EnsureStarted();
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string formattedLine = $"[{timestamp}] {message}";
            _logQueue.Enqueue(formattedLine);
            _signal.Set();
        }

        private static async Task LogWriterLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_logQueue.IsEmpty)
                {
                    _signal.WaitOne(500);
                }

                if (_logQueue.IsEmpty) continue;

                try
                {
                    string logsDir = Config.GetLogsPath();
                    string fileName = $"trading_replay_{DateTime.Now:yyyy-MM-dd}.log";
                    string filePath = Path.Combine(logsDir, fileName);

                    using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 65536);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);

                    int count = 0;
                    while (_logQueue.TryDequeue(out string logLine) && count < 1000)
                    {
                        writer.WriteLine(logLine);
                        count++;
                    }

                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略文件日志写入临时异常
                }
            }
        }

        public static void Stop()
        {
            _cts.Cancel();
        }
    }
}
