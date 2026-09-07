using Common;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Test.TickPlayback.WinForms.Models;
using Test.TickPlayback.WinForms.Services;

namespace Test.TickPlayback.WinForms.Engine
{
    /// <summary>
    /// 高性能 Tick 流式队列加载管道与变速回放引擎 (支持历史有界队列预取与实盘直连双模式)
    /// </summary>
    public class TickPlaybackPipeline : IDisposable
    {
        private readonly ConcurrentQueue<RawTick> _queue = new();
        private readonly TickRingBuffer _ringBuffer;
        private readonly LiveTickStreamService _liveService;
        private ParquetDataReader? _dataReader;

        private CancellationTokenSource? _producerCts;
        private CancellationTokenSource? _dispatcherCts;
        private Task? _producerTask;
        private Task? _dispatcherTask;

        private volatile PlaybackStatus _status = PlaybackStatus.Stopped;
        private volatile DataSourceMode _mode = DataSourceMode.HistoricalParquet;
        private volatile string _currentCoin = "BTCUSDT";

        private double _speedMultiplier = 10.0; // 默认 10x 倍速
        private int _stepTicksRequested = 0;
        private int _maxQueueCapacity = 200000; // 默认最大积压 20 万笔，防止内存暴涨

        private long _totalTicksPushed = 0;
        private long _totalTicksConsumed = 0;
        private long _lastTpsCalculationTicks = 0;
        private long _lastConsumedSnapshot = 0;
        private double _currentTps = 0;

        private decimal _highPrice = decimal.MinValue;
        private decimal _lowPrice = decimal.MaxValue;
        private decimal _firstPrice = decimal.Zero;
        private decimal _currentPrice = decimal.Zero;
        private decimal _totalBuyVol = 0m;
        private decimal _totalSellVol = 0m;
        private decimal _totalQuoteVol = 0m;
        private long _currentTickTimeMs = 0;
        private readonly object _statsLock = new();

        public PlaybackStatus Status => _status;
        public DataSourceMode Mode => _mode;
        public int QueueCount => _queue.Count;
        public int MaxQueueCapacity => _maxQueueCapacity;
        public double SpeedMultiplier => _speedMultiplier;
        public TickRingBuffer RingBuffer => _ringBuffer;

        public event Action<PlaybackStatus>? OnStatusChanged;
        public event Action<string, bool>? OnLogMessage; // (message, isError)

        public TickPlaybackPipeline(int ringBufferCapacity = 10000)
        {
            _ringBuffer = new TickRingBuffer(ringBufferCapacity);
            _liveService = new LiveTickStreamService();

            _liveService.OnTickReceived += tick =>
            {
                if (_mode == DataSourceMode.LiveWebSocket && _status == PlaybackStatus.LiveConnected)
                {
                    _queue.Enqueue(tick);
                    Interlocked.Increment(ref _totalTicksPushed);
                }
            };

            _liveService.OnStatusChanged += (msg, isErr) =>
            {
                OnLogMessage?.Invoke(msg, isErr);
            };
        }

        #region 控制操作 (Play, Pause, Stop, Step, Speed)

        /// <summary>
        /// 启动历史 Parquet 流式队列回放
        /// </summary>
        public async Task StartHistoricalPlaybackAsync(string coin, DateTime startDate, DateTime endDate, int parallelDays = 3)
        {
            await StopAsync();

            _currentCoin = coin.Trim().ToUpperInvariant();
            _mode = DataSourceMode.HistoricalParquet;
            _status = PlaybackStatus.Buffering;
            OnStatusChanged?.Invoke(_status);

            ResetStatistics();
            _ringBuffer.Clear();
            while (_queue.TryDequeue(out _)) { }

            _producerCts = new CancellationTokenSource();
            _dispatcherCts = new CancellationTokenSource();

            _dataReader = new ParquetDataReader();

            OnLogMessage?.Invoke($"[回放引擎] 正在启动 {coin} 历史 Tick 队列流式预取 ({startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd})...", false);

            // 1. 启动后台生产者任务
            var prodToken = _producerCts.Token;
            _producerTask = Task.Run(async () =>
            {
                try
                {
                    await _dataReader.StartTickStreamingAsync(
                        coin,
                        startDate,
                        endDate,
                        parallelDays: parallelDays,
                        maxBufferedDays: 2,
                        prodToken).ConfigureAwait(false);

                    OnLogMessage?.Invoke($"[生产者] 历史 Tick 数据预取完成，总计载入 {_dataReader.TotalTicksLoaded:N0} 笔。", false);
                }
                catch (OperationCanceledException)
                {
                    OnLogMessage?.Invoke("[生产者] 历史数据预取已取消。", false);
                }
                catch (Exception ex)
                {
                    OnLogMessage?.Invoke($"[生产者] 预取异常: {ex.Message}", true);
                }
            }, prodToken);

            // 2. 启动中继桥接任务 (将 dataReader.TickQueue 流入本地 _queue 并做背压控制)
            _ = Task.Run(async () =>
            {
                while (!prodToken.IsCancellationRequested)
                {
                    while (_dataReader != null && _dataReader.TickQueue.TryDequeue(out var tick))
                    {
                        _queue.Enqueue(tick);
                        Interlocked.Increment(ref _totalTicksPushed);

                        // 背压控制：当本地队列堆积过大时适度等待
                        while (_queue.Count >= _maxQueueCapacity && !prodToken.IsCancellationRequested)
                        {
                            await Task.Delay(10, prodToken).ConfigureAwait(false);
                        }
                    }

                    if (_dataReader != null && _dataReader.IsTickStreamingCompleted && _dataReader.TickQueue.IsEmpty)
                    {
                        break;
                    }

                    await Task.Delay(5, prodToken).ConfigureAwait(false);
                }
            }, prodToken);

            // 3. 启动消费者回放调度器
            _status = PlaybackStatus.Playing;
            OnStatusChanged?.Invoke(_status);

            var dispToken = _dispatcherCts.Token;
            _dispatcherTask = Task.Run(() => DispatcherLoop(dispToken), dispToken);
        }

        /// <summary>
        /// 启动币安实盘 WebSocket 实时逐笔行情直连
        /// </summary>
        public async Task<bool> StartLiveStreamingAsync(string symbol, MarketType market)
        {
            await StopAsync();

            _currentCoin = symbol.Trim().ToUpperInvariant();
            _mode = DataSourceMode.LiveWebSocket;
            _status = PlaybackStatus.LiveConnecting;
            OnStatusChanged?.Invoke(_status);

            ResetStatistics();
            _ringBuffer.Clear();
            while (_queue.TryDequeue(out _)) { }

            _dispatcherCts = new CancellationTokenSource();

            bool ok = await _liveService.StartStreamingAsync(symbol, market, _dispatcherCts.Token);
            if (ok)
            {
                _status = PlaybackStatus.LiveConnected;
                OnStatusChanged?.Invoke(_status);

                var dispToken = _dispatcherCts.Token;
                _dispatcherTask = Task.Run(() => DispatcherLoop(dispToken), dispToken);
                return true;
            }
            else
            {
                _status = PlaybackStatus.LiveError;
                OnStatusChanged?.Invoke(_status);
                return false;
            }
        }

        public void Pause()
        {
            if (_status == PlaybackStatus.Playing)
            {
                _status = PlaybackStatus.Paused;
                OnStatusChanged?.Invoke(_status);
                OnLogMessage?.Invoke("⏸ 回放已暂停", false);
            }
        }

        public void Resume()
        {
            if (_status == PlaybackStatus.Paused)
            {
                _status = PlaybackStatus.Playing;
                OnStatusChanged?.Invoke(_status);
                OnLogMessage?.Invoke("▶ 恢复回放", false);
            }
        }

        public void Step(int ticksCount = 100)
        {
            if (_status == PlaybackStatus.Paused || _status == PlaybackStatus.Playing)
            {
                Interlocked.Add(ref _stepTicksRequested, Math.Max(1, ticksCount));
            }
        }

        public void SetSpeed(double multiplier)
        {
            _speedMultiplier = Math.Clamp(multiplier, 0.1, 10000.0);
        }

        public async Task StopAsync()
        {
            _status = PlaybackStatus.Stopped;
            OnStatusChanged?.Invoke(_status);

            _producerCts?.Cancel();
            _dispatcherCts?.Cancel();

            if (_producerTask != null)
            {
                try { await _producerTask.ConfigureAwait(false); } catch { }
                _producerTask = null;
            }

            if (_dispatcherTask != null)
            {
                try { await _dispatcherTask.ConfigureAwait(false); } catch { }
                _dispatcherTask = null;
            }

            _producerCts?.Dispose();
            _producerCts = null;
            _dispatcherCts?.Dispose();
            _dispatcherCts = null;

            if (_dataReader != null)
            {
                _dataReader.Dispose();
                _dataReader = null;
            }

            await _liveService.StopStreamingAsync();
        }

        #endregion

        #region 消费者核心调度循环 (Dispatcher Loop)

        private void DispatcherLoop(CancellationToken ct)
        {
            long lastSimTimeMs = 0;
            long lastRealTicks = Stopwatch.GetTimestamp();
            double stopwatchFreq = Stopwatch.Frequency;

            Span<RawTick> batch = stackalloc RawTick[256];

            while (!ct.IsCancellationRequested)
            {
                // 1. 实盘直连模式：低延迟全速排空队列推入环形缓冲
                if (_mode == DataSourceMode.LiveWebSocket)
                {
                    int drained = 0;
                    while (_queue.TryDequeue(out var tick))
                    {
                        batch[drained++] = tick;
                        if (drained == batch.Length)
                        {
                            ConsumeBatch(batch);
                            drained = 0;
                        }
                    }
                    if (drained > 0)
                    {
                        ConsumeBatch(batch.Slice(0, drained));
                    }

                    Thread.Sleep(1);
                    continue;
                }

                // 2. 历史回放模式：
                if (_status == PlaybackStatus.Paused)
                {
                    // 检查是否有单步步进请求
                    int step = Interlocked.Exchange(ref _stepTicksRequested, 0);
                    if (step > 0)
                    {
                        int drained = 0;
                        while (drained < step && _queue.TryDequeue(out var tick))
                        {
                            batch[drained % batch.Length] = tick;
                            drained++;
                            if (drained % batch.Length == 0)
                            {
                                ConsumeBatch(batch);
                            }
                        }
                        if (drained % batch.Length != 0)
                        {
                            ConsumeBatch(batch.Slice(0, drained % batch.Length));
                        }
                    }
                    Thread.Sleep(10);
                    continue;
                }

                if (_status != PlaybackStatus.Playing)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // 极速回放 (Speed >= 5000x)：无等待全速消费
                if (_speedMultiplier >= 5000.0)
                {
                    int drained = 0;
                    while (_queue.TryDequeue(out var tick))
                    {
                        batch[drained++] = tick;
                        if (drained == batch.Length)
                        {
                            ConsumeBatch(batch);
                            drained = 0;
                        }
                        if (_queue.IsEmpty) break;
                    }
                    if (drained > 0)
                    {
                        ConsumeBatch(batch.Slice(0, drained));
                    }
                    if (_queue.IsEmpty && _dataReader != null && _dataReader.IsTickStreamingCompleted)
                    {
                        _status = PlaybackStatus.Completed;
                        OnStatusChanged?.Invoke(_status);
                        OnLogMessage?.Invoke("🏁 历史数据全部回放完毕！", false);
                        break;
                    }
                    Thread.Yield();
                    continue;
                }

                // 精准拟真/倍速回放：基于时间戳推进
                if (_queue.TryPeek(out var nextTick))
                {
                    if (lastSimTimeMs == 0)
                    {
                        lastSimTimeMs = nextTick.Time;
                        lastRealTicks = Stopwatch.GetTimestamp();
                    }

                    long nowTicks = Stopwatch.GetTimestamp();
                    double elapsedSec = (nowTicks - lastRealTicks) / stopwatchFreq;
                    long targetSimTimeMs = lastSimTimeMs + (long)(elapsedSec * 1000.0 * _speedMultiplier);

                    int batchCount = 0;
                    while (_queue.TryPeek(out var peekTick) && peekTick.Time <= targetSimTimeMs)
                    {
                        if (_queue.TryDequeue(out var popTick))
                        {
                            batch[batchCount++] = popTick;
                            if (batchCount == batch.Length)
                            {
                                ConsumeBatch(batch);
                                batchCount = 0;
                            }
                        }
                    }

                    if (batchCount > 0)
                    {
                        ConsumeBatch(batch.Slice(0, batchCount));
                    }

                    // 动态自适应微休眠
                    if (elapsedSec > 0.005)
                    {
                        lastSimTimeMs = targetSimTimeMs;
                        lastRealTicks = Stopwatch.GetTimestamp();
                    }
                    Thread.Sleep(1);
                }
                else
                {
                    // 队列当前为空，检查是否已全部读取完毕
                    if (_dataReader != null && _dataReader.IsTickStreamingCompleted)
                    {
                        _status = PlaybackStatus.Completed;
                        OnStatusChanged?.Invoke(_status);
                        OnLogMessage?.Invoke("🏁 历史数据全部回放完毕！", false);
                        break;
                    }
                    Thread.Sleep(5);
                }
            }
        }

        private void ConsumeBatch(ReadOnlySpan<RawTick> ticks)
        {
            if (ticks.IsEmpty) return;

            _ringBuffer.PushRange(ticks);
            Interlocked.Add(ref _totalTicksConsumed, ticks.Length);

            lock (_statsLock)
            {
                for (int i = 0; i < ticks.Length; i++)
                {
                    var t = ticks[i];
                    if (_firstPrice == decimal.Zero) _firstPrice = t.Price;
                    _currentPrice = t.Price;
                    if (t.Price > _highPrice) _highPrice = t.Price;
                    if (t.Price < _lowPrice) _lowPrice = t.Price;

                    _totalQuoteVol += t.QuoteQty;
                    if (!t.IsBuyerMaker)
                    {
                        _totalBuyVol += t.Qty;
                    }
                    else
                    {
                        _totalSellVol += t.Qty;
                    }
                    _currentTickTimeMs = t.Time;
                }
            }
        }

        #endregion

        #region 仪表盘状态快照获取 (Dashboard Statistics)

        public PlaybackDashboardStats GetDashboardStats(double renderFps = 60.0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (_lastTpsCalculationTicks == 0)
            {
                _lastTpsCalculationTicks = nowTicks;
                _lastConsumedSnapshot = _totalTicksConsumed;
            }

            double elapsedSec = (nowTicks - _lastTpsCalculationTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec >= 0.25)
            {
                long currentConsumed = Interlocked.Read(ref _totalTicksConsumed);
                _currentTps = (currentConsumed - _lastConsumedSnapshot) / elapsedSec;
                _lastConsumedSnapshot = currentConsumed;
                _lastTpsCalculationTicks = nowTicks;
            }

            var stats = new PlaybackDashboardStats
            {
                Mode = _mode,
                Status = _status,
                Coin = _currentCoin,
                TotalTicksLoaded = Interlocked.Read(ref _totalTicksPushed),
                TotalTicksPlayed = Interlocked.Read(ref _totalTicksConsumed),
                QueueBufferCount = _queue.Count,
                MaxQueueCapacity = _maxQueueCapacity,
                PlaybackThroughputTps = _currentTps,
                RenderFps = renderFps,
                CurrentTickTimeMs = _currentTickTimeMs
            };

            lock (_statsLock)
            {
                stats.CurrentPrice = _currentPrice;
                stats.FirstPrice = _firstPrice;
                stats.HighPrice = _highPrice == decimal.MinValue ? 0m : _highPrice;
                stats.LowPrice = _lowPrice == decimal.MaxValue ? 0m : _lowPrice;
                stats.TotalBuyVolume = _totalBuyVol;
                stats.TotalSellVolume = _totalSellVol;
                stats.TotalQuoteVolume = _totalQuoteVol;
            }

            if (_dataReader != null)
            {
                stats.ProducerDaysLoaded = _dataReader.LoadedTickDaysCount;
                stats.ProducerStatusMessage = _dataReader.IsTickStreamingCompleted ? "已全部读入" : "流式预取中...";
            }

            return stats;
        }

        private void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalTicksPushed, 0);
            Interlocked.Exchange(ref _totalTicksConsumed, 0);
            _lastTpsCalculationTicks = 0;
            _lastConsumedSnapshot = 0;
            _currentTps = 0;

            lock (_statsLock)
            {
                _firstPrice = decimal.Zero;
                _currentPrice = decimal.Zero;
                _highPrice = decimal.MinValue;
                _lowPrice = decimal.MaxValue;
                _totalBuyVol = 0m;
                _totalSellVol = 0m;
                _totalQuoteVol = 0m;
                _currentTickTimeMs = 0;
            }
        }

        public void Dispose()
        {
            _ = StopAsync();
            _liveService.Dispose();
        }

        #endregion
    }
}
