using Common;
using System;
using System.Threading;

namespace Test.TickPlayback.WinForms.Models
{
    /// <summary>
    /// 数据源运行模式 (历史 Parquet 队列回放 vs 币安实盘 WebSocket 直连)
    /// </summary>
    public enum DataSourceMode
    {
        /// <summary>
        /// 历史 Parquet 逐日文件流式队列回放
        /// </summary>
        HistoricalParquet = 0,

        /// <summary>
        /// 币安实盘 WebSocket 实时逐笔成交直连
        /// </summary>
        LiveWebSocket = 1
    }

    /// <summary>
    /// 实盘市场类型
    /// </summary>
    public enum MarketType
    {
        /// <summary>
        /// 币安现货市场 (Spot)
        /// </summary>
        Spot = 0,

        /// <summary>
        /// 币安 USDT 本位永续合约市场 (USDT-M Futures)
        /// </summary>
        UsdtFutures = 1
    }

    /// <summary>
    /// 回放引擎运行状态
    /// </summary>
    public enum PlaybackStatus
    {
        Stopped = 0,
        Buffering = 1,
        Playing = 2,
        Paused = 3,
        Completed = 4,
        LiveConnecting = 5,
        LiveConnected = 6,
        LiveError = 7
    }

    /// <summary>
    /// Tick 图表走势线型
    /// </summary>
    public enum TickChartStyle
    {
        /// <summary>
        /// 平滑折线 (Line)
        /// </summary>
        Line = 0,

        /// <summary>
        /// 阶梯走势线 (Step Line - 真实还原订单簿成交台阶)
        /// </summary>
        StepLine = 1
    }

    /// <summary>
    /// 逐笔 Tick 表格显示轻量视图模型 (适配 VirtualMode，零 GC 压力)
    /// </summary>
    public readonly struct TickItemViewModel
    {
        public long Sequence { get; init; }
        public long Time { get; init; }
        public decimal Price { get; init; }
        public decimal Qty { get; init; }
        public decimal QuoteQty { get; init; }
        public bool IsBuyerMaker { get; init; }
        public decimal DiffFromStartPct { get; init; }

        public bool IsBuyerTaker => !IsBuyerMaker; // 买方主动吃单为 True (买入/绿色)
    }

    /// <summary>
    /// 线程安全、零堆内存分配的逐笔 Tick 环形滑动窗口缓冲区 (Ring Buffer)
    /// </summary>
    public class TickRingBuffer
    {
        private readonly RawTick[] _buffer;
        private readonly int _capacity;
        private int _head = 0;
        private int _count = 0;
        private long _totalPushed = 0;
        private readonly object _lock = new();

        public int Capacity => _capacity;
        public int Count
        {
            get { lock (_lock) return _count; }
        }
        public long TotalPushed => Interlocked.Read(ref _totalPushed);

        public TickRingBuffer(int capacity = 10000)
        {
            _capacity = Math.Max(100, capacity);
            _buffer = new RawTick[_capacity];
        }

        public void Push(in RawTick tick)
        {
            lock (_lock)
            {
                _buffer[_head] = tick;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity)
                {
                    _count++;
                }
                Interlocked.Increment(ref _totalPushed);
            }
        }

        public void PushRange(ReadOnlySpan<RawTick> ticks)
        {
            if (ticks.IsEmpty) return;
            lock (_lock)
            {
                for (int i = 0; i < ticks.Length; i++)
                {
                    _buffer[_head] = ticks[i];
                    _head = (_head + 1) % _capacity;
                    if (_count < _capacity)
                    {
                        _count++;
                    }
                }
                Interlocked.Add(ref _totalPushed, ticks.Length);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
                Interlocked.Exchange(ref _totalPushed, 0);
            }
        }

        /// <summary>
        /// 零分配快速提取当前缓冲区所有已按时间先后排序的 Tick 数据快照
        /// </summary>
        public int CopyTo(RawTick[] destination)
        {
            if (destination == null || destination.Length == 0) return 0;

            lock (_lock)
            {
                int copyCount = Math.Min(_count, destination.Length);
                if (copyCount == 0) return 0;

                int start = (_head - _count + _capacity) % _capacity;
                if (_count == copyCount)
                {
                    // 复制全部
                    for (int i = 0; i < copyCount; i++)
                    {
                        destination[i] = _buffer[(start + i) % _capacity];
                    }
                }
                else
                {
                    // 仅复制最新的 copyCount 笔
                    int offset = _count - copyCount;
                    start = (start + offset) % _capacity;
                    for (int i = 0; i < copyCount; i++)
                    {
                        destination[i] = _buffer[(start + i) % _capacity];
                    }
                }
                return copyCount;
            }
        }

        /// <summary>
        /// 获取第 index 笔 Tick (按时间先后顺序，0 为最早一笔，Count - 1 为最新一笔)
        /// </summary>
        public bool TryGetAt(int index, out RawTick tick)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _count)
                {
                    tick = default;
                    return false;
                }
                int actualIndex = (_head - _count + index + _capacity) % _capacity;
                tick = _buffer[actualIndex];
                return true;
            }
        }
    }

    /// <summary>
    /// 实时回放与微观流式统计指标看板模型
    /// </summary>
    public class PlaybackDashboardStats
    {
        public DataSourceMode Mode { get; set; } = DataSourceMode.HistoricalParquet;
        public PlaybackStatus Status { get; set; } = PlaybackStatus.Stopped;
        public string Coin { get; set; } = "BTCUSDT";

        public long TotalTicksLoaded { get; set; }
        public long TotalTicksPlayed { get; set; }
        public int QueueBufferCount { get; set; }
        public int MaxQueueCapacity { get; set; } = 200000;

        public decimal CurrentPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal FirstPrice { get; set; }
        public decimal PriceChangePct => FirstPrice > 0 ? (CurrentPrice - FirstPrice) / FirstPrice * 100m : 0m;

        public decimal TotalBuyVolume { get; set; }
        public decimal TotalSellVolume { get; set; }
        public decimal TotalQuoteVolume { get; set; }
        public decimal VWAP => (TotalBuyVolume + TotalSellVolume) > 0 ? TotalQuoteVolume / (TotalBuyVolume + TotalSellVolume) : CurrentPrice;

        public double BuyVolumeRatioPct
        {
            get
            {
                decimal totalVol = TotalBuyVolume + TotalSellVolume;
                return totalVol > 0 ? (double)(TotalBuyVolume / totalVol * 100m) : 50.0;
            }
        }

        public long CurrentTickTimeMs { get; set; }
        public double PlaybackThroughputTps { get; set; }
        public double RenderFps { get; set; }
        public int ProducerDaysLoaded { get; set; }
        public int ProducerDaysTotal { get; set; }
        public string ProducerStatusMessage { get; set; } = "就绪";
    }
}
