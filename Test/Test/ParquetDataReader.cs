using Binance.Net.Enums;
using Common.Helper;
using DuckDB.NET.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Common
{
    #region 原始零转换数据结构体 (Zero-Allocation Raw Data Structs - Decimal 高精度版)

    /// <summary>
    /// 币安原始 K 线数据（紧凑内存结构体，采用 decimal 杜绝浮点精度丢失）
    /// </summary>
    public readonly struct RawKline
    {
        public long OpenTime { get; init; }           // 开盘时间 (毫秒时间戳)
        public decimal Open { get; init; }            // 开盘价                   
        public decimal High { get; init; }            // 最高价
        public decimal Low { get; init; }             // 最低价
        public decimal Close { get; init; }           // 收盘价
        public decimal Volume { get; init; }          // 基础币成交量 (Base Asset Volume)
        public long CloseTime { get; init; }          // 收盘时间 (毫秒时间戳)
        public decimal QuoteVolume { get; init; }     // 计价币成交额 (Quote Asset Volume)
        public long TradeCount { get; init; }         // 这一根K线内的成交笔数
        public decimal TakerBuyVolume { get; init; }  // 主动买入成交量 (Taker Buy Base Asset Volume)
        public decimal TakerBuyQuoteVolume { get; init;}// 主动买入成交额 (Taker Buy Quote Asset Volume)

        public override string ToString()
        {
            DateTime time = TimeHelper.FromUnixTimeMilliseconds(OpenTime);
            return $"[Kline {time:yyyy-MM-dd HH:mm:ss}] O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume:F4} Trades:{TradeCount}";
        }
    }

    /// <summary>
    /// 币安原始 Tick / Trade 数据（紧凑内存结构体，采用 decimal 杜绝浮点精度丢失）
    /// </summary>
    public readonly struct RawTick
    {
        public long TradeId { get; init; }            // 逐笔成交 ID / AggTrade ID
        public decimal Price { get; init; }           // 成交价格
        public decimal Qty { get; init; }             // 成交数量
        public decimal QuoteQty { get; init; }        // 成交金额 (Price * Qty)
        public long Time { get; init; }               // 成交时间 (毫秒时间戳)
        public bool IsBuyerMaker { get; init; }       // 是否为买方挂单 (true = 卖方主动吃单/主动卖出, false = 买方主动吃单/主动买入)
        public bool IsBestMatch { get; init; }        // 是否为最优撮合

        public override string ToString()
        {
            DateTime time = TimeHelper.FromUnixTimeMilliseconds(Time);
            string side = IsBuyerMaker ? "SELL" : "BUY";
            return $"[Tick {time:yyyy-MM-dd HH:mm:ss.fff}] Id:{TradeId} Side:{side} Price:{Price:F2} Qty:{Qty:F4} Quote:{QuoteQty:F2}";
        }
    }

    #endregion

    #region 统计与监控模型 (Statistics Tracking)

    /// <summary>
    /// 数据加载与队列监控统计指标
    /// </summary>
    public class ReaderStatistics
    {
        public long TotalKlinesLoaded { get; internal set; }
        public long TotalTicksLoaded { get; internal set; }
        public int LoadedKlineDaysCount { get; internal set; }
        public int LoadedTickDaysCount { get; internal set; }
        public DateTime? CurrentKlineDate { get; internal set; }
        public DateTime? CurrentTickDate { get; internal set; }
        public int KlineQueueCount { get; internal set; }
        public int TickQueueCount { get; internal set; }
        public TimeSpan ElapsedTime { get; internal set; }
        public double TicksPerSecond => ElapsedTime.TotalSeconds > 0 ? TotalTicksLoaded / ElapsedTime.TotalSeconds : 0;
        public double KlinesPerSecond => ElapsedTime.TotalSeconds > 0 ? TotalKlinesLoaded / ElapsedTime.TotalSeconds : 0;

        public override string ToString()
        {
            return $"[DataReader Stats] Klines: {TotalKlinesLoaded:N0} ({LoadedKlineDaysCount} days, Current: {CurrentKlineDate:yyyy-MM-dd}, Queue: {KlineQueueCount:N0}) | " +
                   $"Ticks: {TotalTicksLoaded:N0} ({LoadedTickDaysCount} days, Current: {CurrentTickDate:yyyy-MM-dd}, Queue: {TickQueueCount:N0}) | " +
                   $"Throughput: {TicksPerSecond:N0} ticks/s, Elapsed: {ElapsedTime.TotalSeconds:F2}s";
        }
    }

    #endregion

    /// <summary>
    /// 高性能币安 Parquet 数据读取帮助类 (DuckDB 向量化引擎 + 3 线程有序滑动窗口预取 + 生产者-消费者流式流水线)
    /// </summary>
    public class ParquetDataReader : IDisposable
    {
        // 双独立队列
        public ConcurrentQueue<RawKline> KlineQueue { get; } = new ConcurrentQueue<RawKline>();
        public ConcurrentQueue<RawTick> TickQueue { get; } = new ConcurrentQueue<RawTick>();

        // 内部原子统计计数器与状态
        private long _totalKlinesLoaded = 0;
        private long _totalTicksLoaded = 0;
        private int _loadedKlineDaysCount = 0;
        private int _loadedTickDaysCount = 0;
        private DateTime? _currentKlineDate = null;
        private DateTime? _currentTickDate = null;
        private volatile bool _isTickStreamingCompleted = false;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private bool _disposed = false;

        public ParquetDataReader()
        {
            Logger.EnsureStarted();
            _stopwatch.Start();
        }

        #region 实时统计属性 (Statistics Properties)

        public long TotalKlinesLoaded => Interlocked.Read(ref _totalKlinesLoaded);
        public long TotalTicksLoaded => Interlocked.Read(ref _totalTicksLoaded);
        public int LoadedKlineDaysCount => Volatile.Read(ref _loadedKlineDaysCount);
        public int LoadedTickDaysCount => Volatile.Read(ref _loadedTickDaysCount);
        public DateTime? CurrentKlineDate => _currentKlineDate;
        public DateTime? CurrentTickDate => _currentTickDate;
        public bool IsTickStreamingCompleted => _isTickStreamingCompleted;
        public int KlineQueueCount => KlineQueue.Count;
        public int TickQueueCount => TickQueue.Count;

        public ReaderStatistics GetStatistics()
        {
            return new ReaderStatistics
            {
                TotalKlinesLoaded = TotalKlinesLoaded,
                TotalTicksLoaded = TotalTicksLoaded,
                LoadedKlineDaysCount = LoadedKlineDaysCount,
                LoadedTickDaysCount = LoadedTickDaysCount,
                CurrentKlineDate = _currentKlineDate,
                CurrentTickDate = _currentTickDate,
                KlineQueueCount = KlineQueue.Count,
                TickQueueCount = TickQueue.Count,
                ElapsedTime = _stopwatch.Elapsed
            };
        }

        public string GetStatusSummary() => GetStatistics().ToString();

        #endregion

        #region 单日与多日加载入口方法

        public async Task<int> LoadKlineDayAsync(string coin, DateTime date, KlineInterval interval = KlineInterval.OneMinute, CancellationToken ct = default)
        {
            string filePath = Config.GetKlineFilePath(coin, interval, date, ".parquet");
            RawKline[] klines = await ReadKlineFileInternalAsync(filePath, ct).ConfigureAwait(false);

            if (klines.Length > 0)
            {
                for (int i = 0; i < klines.Length; i++)
                {
                    KlineQueue.Enqueue(klines[i]);
                }
                Interlocked.Add(ref _totalKlinesLoaded, klines.Length);
                Interlocked.Increment(ref _loadedKlineDaysCount);
                _currentKlineDate = date;
            }

            return klines.Length;
        }

        public async Task<int> LoadTickDayAsync(string coin, DateTime date, CancellationToken ct = default)
        {
            string filePath = Config.GetTradeFilePath(coin, date, ".parquet");
            RawTick[] ticks = await ReadTickFileInternalAsync(filePath, ct).ConfigureAwait(false);

            if (ticks.Length > 0)
            {
                for (int i = 0; i < ticks.Length; i++)
                {
                    TickQueue.Enqueue(ticks[i]);
                }
                Interlocked.Add(ref _totalTicksLoaded, ticks.Length);
                Interlocked.Increment(ref _loadedTickDaysCount);
                _currentTickDate = date;
            }

            return ticks.Length;
        }

        public async Task<(int klineCount, int tickCount)> LoadDateRangeAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            KlineInterval interval = KlineInterval.OneMinute,
            int parallelDays = 3,
            CancellationToken ct = default)
        {
            var klineTask = StartKlineStreamingAsync(coin, startDate, endDate, interval, ct);
            var tickTask = StartTickStreamingAsync(coin, startDate, endDate, parallelDays, maxBufferedDays: 3, ct);

            await Task.WhenAll(klineTask, tickTask).ConfigureAwait(false);
            return ((int)TotalKlinesLoaded, (int)TotalTicksLoaded);
        }

        public async Task<int> LoadKlineRangeAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            KlineInterval interval = KlineInterval.OneMinute,
            CancellationToken ct = default)
        {
            await StartKlineStreamingAsync(coin, startDate, endDate, interval, ct).ConfigureAwait(false);
            return (int)TotalKlinesLoaded;
        }

        #endregion

        #region 3 线程有序滑动窗口并行流式预取 (Ordered Parallel Prefetching Stream)

        /// <summary>
        /// 启动 Tick 数据的后台 3 线程有序滑动窗口流式预取（生产者任务）
        /// 并行读取连续天数的 Tick Parquet 文件并在内存暂存，按时间顺序依次 Enqueue 入队
        /// </summary>
        public async Task StartTickStreamingAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            int parallelDays = 3,
            int maxBufferedDays = 3,
            CancellationToken ct = default)
        {
            _isTickStreamingCompleted = false;

            if (startDate > endDate)
            {
                _isTickStreamingCompleted = true;
                return;
            }

            List<DateTime> allDates = new List<DateTime>();
            for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            {
                allDates.Add(d);
            }

            Logger.Log($"[ParquetDataReader] Starting Tick streaming for {coin} from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd} ({allDates.Count} days, ParallelWindow: {parallelDays}).");

            var slidingWindow = new Queue<(DateTime Date, Task<RawTick[]> FetchTask)>();
            int nextScheduleIndex = 0;

            try
            {
                // 1. 初始化滑动窗口：并发启动前 parallelDays 天的读取任务
                while (slidingWindow.Count < parallelDays && nextScheduleIndex < allDates.Count)
                {
                    DateTime dateToFetch = allDates[nextScheduleIndex++];
                    string filePath = Config.GetTradeFilePath(coin, dateToFetch, ".parquet");
                    var fetchTask = Task.Run(() => ReadTickFileInternalAsync(filePath, ct), ct);
                    slidingWindow.Enqueue((dateToFetch, fetchTask));
                }

                // 2. 顺序提交循环 (Ordered Committer Loop)
                while (slidingWindow.Count > 0 && !ct.IsCancellationRequested)
                {
                    var (currDate, currTask) = slidingWindow.Dequeue();

                    // 调度下一天填补滑动窗口
                    if (nextScheduleIndex < allDates.Count && !ct.IsCancellationRequested)
                    {
                        DateTime nextDate = allDates[nextScheduleIndex++];
                        string nextFilePath = Config.GetTradeFilePath(coin, nextDate, ".parquet");
                        var nextTask = Task.Run(() => ReadTickFileInternalAsync(nextFilePath, ct), ct);
                        slidingWindow.Enqueue((nextDate, nextTask));
                    }

                    // 等待当前 Day N 完成
                    RawTick[] ticks = await currTask.ConfigureAwait(false);

                    // 背压控制：当队列积压超过阈值时适度让步，防止撑爆内存
                    while (TickQueue.Count > maxBufferedDays * 2_000_000 && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }

                    // 将 Day N 的所有 Tick 按原始顺序推入 TickQueue
                    if (ticks.Length > 0)
                    {
                        for (int i = 0; i < ticks.Length; i++)
                        {
                            TickQueue.Enqueue(ticks[i]);
                        }
                        Interlocked.Add(ref _totalTicksLoaded, ticks.Length);
                        Interlocked.Increment(ref _loadedTickDaysCount);
                        _currentTickDate = currDate;
                    }
                }
            }
            finally
            {
                _isTickStreamingCompleted = true;
                Logger.Log($"[ParquetDataReader] Tick streaming completed for {coin}. Total Ticks: {TotalTicksLoaded:N0} across {LoadedTickDaysCount} days.");
            }
        }

        public async Task StartKlineStreamingAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            KlineInterval interval = KlineInterval.OneMinute,
            CancellationToken ct = default)
        {
            if (startDate > endDate) return;

            Logger.Log($"[ParquetDataReader] Starting Kline streaming for {coin} from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd} (Interval: {interval}).");

            for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            {
                if (ct.IsCancellationRequested) break;

                string filePath = Config.GetKlineFilePath(coin, interval, d, ".parquet");
                RawKline[] klines = await ReadKlineFileInternalAsync(filePath, ct).ConfigureAwait(false);

                if (klines.Length > 0)
                {
                    for (int i = 0; i < klines.Length; i++)
                    {
                        KlineQueue.Enqueue(klines[i]);
                    }
                    Interlocked.Add(ref _totalKlinesLoaded, klines.Length);
                    Interlocked.Increment(ref _loadedKlineDaysCount);
                    _currentKlineDate = d;
                }
            }

            Logger.Log($"[ParquetDataReader] Kline streaming completed for {coin}. Total Klines: {TotalKlinesLoaded:N0} across {LoadedKlineDaysCount} days.");
        }

        #endregion

        #region DuckDB 极速原生文件下推解析 (High-Speed Pushdown Parsing)

        private static async Task<RawTick[]> ReadTickFileInternalAsync(string filePath, CancellationToken ct)
        {
            if (!File.Exists(filePath))
            {
                Logger.Log($"[ParquetDataReader] Tick file not found: {filePath}, skipping.");
                return Array.Empty<RawTick>();
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                await connection.OpenAsync(ct).ConfigureAwait(false);

                using var command = connection.CreateCommand();
                string normalizedPath = filePath.Replace('\\', '/');
                command.CommandText = $"SELECT * FROM read_parquet('{normalizedPath}')";

                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                int idIdx = FindColumnOrdinal(reader, "trade_id", "id", "tradeId", "agg_trade_id", "aggregate_trade_id");
                int priceIdx = FindColumnOrdinal(reader, "price", "p");
                int qtyIdx = FindColumnOrdinal(reader, "qty", "quantity", "q");
                int quoteQtyIdx = FindColumnOrdinal(reader, "quote_qty", "quoteQty", "quote_quantity", "quote_volume");
                int timeIdx = FindColumnOrdinal(reader, "time", "transact_time", "timestamp", "trade_time", "T");
                int isBuyerMakerIdx = FindColumnOrdinal(reader, "is_buyer_maker", "isBuyerMaker", "buyer_maker", "m");
                int isBestMatchIdx = FindColumnOrdinal(reader, "is_best_match", "isBestMatch", "M");

                var list = new List<RawTick>(300000);

                // 极速同步行迭代，杜绝数百万次 await Task 状态机调度损耗
                while (reader.Read())
                {
                    long tradeId = idIdx >= 0 ? ReadInt64(reader, idIdx) : 0;
                    decimal price = priceIdx >= 0 ? ReadDecimal(reader, priceIdx) : 0m;
                    decimal qty = qtyIdx >= 0 ? ReadDecimal(reader, qtyIdx) : 0m;
                    decimal quoteQty = quoteQtyIdx >= 0 ? ReadDecimal(reader, quoteQtyIdx) : (price * qty);
                    long time = timeIdx >= 0 ? ReadInt64(reader, timeIdx) : 0;
                    bool isBuyerMaker = isBuyerMakerIdx >= 0 && ReadBoolean(reader, isBuyerMakerIdx);
                    bool isBestMatch = isBestMatchIdx >= 0 && ReadBoolean(reader, isBestMatchIdx);

                    list.Add(new RawTick
                    {
                        TradeId = tradeId,
                        Price = price,
                        Qty = qty,
                        QuoteQty = quoteQty,
                        Time = time,
                        IsBuyerMaker = isBuyerMaker,
                        IsBestMatch = isBestMatch
                    });
                }

                if (list.Count > 1)
                {
                    list.Sort((a, b) => a.Time == b.Time ? a.TradeId.CompareTo(b.TradeId) : a.Time.CompareTo(b.Time));
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParquetDataReader] Error reading tick file {filePath}: {ex.Message}");
                return Array.Empty<RawTick>();
            }
        }

        private static async Task<RawKline[]> ReadKlineFileInternalAsync(string filePath, CancellationToken ct)
        {
            if (!File.Exists(filePath))
            {
                Logger.Log($"[ParquetDataReader] Kline file not found: {filePath}, skipping.");
                return Array.Empty<RawKline>();
            }

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                await connection.OpenAsync(ct).ConfigureAwait(false);

                using var command = connection.CreateCommand();
                string normalizedPath = filePath.Replace('\\', '/');
                command.CommandText = $"SELECT * FROM read_parquet('{normalizedPath}')";

                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                int openTimeIdx = FindColumnOrdinal(reader, "open_time", "openTime", "startTime", "start_time", "t", "timestamp");
                int openIdx = FindColumnOrdinal(reader, "open", "o");
                int highIdx = FindColumnOrdinal(reader, "high", "h");
                int lowIdx = FindColumnOrdinal(reader, "low", "l");
                int closeIdx = FindColumnOrdinal(reader, "close", "c");
                int volumeIdx = FindColumnOrdinal(reader, "volume", "v");
                int closeTimeIdx = FindColumnOrdinal(reader, "close_time", "closeTime", "endTime", "end_time", "T");
                int quoteVolumeIdx = FindColumnOrdinal(reader, "quote_volume", "quoteVolume", "q");
                int tradeCountIdx = FindColumnOrdinal(reader, "count", "trades", "tradeCount", "n");
                int tbVolumeIdx = FindColumnOrdinal(reader, "taker_buy_volume", "takerBuyBaseAssetVolume", "V");
                int tbQuoteVolumeIdx = FindColumnOrdinal(reader, "taker_buy_quote_volume", "takerBuyQuoteAssetVolume", "Q");

                var list = new List<RawKline>(1500);

                while (reader.Read())
                {
                    long openTime = openTimeIdx >= 0 ? ReadInt64(reader, openTimeIdx) : 0;
                    decimal open = openIdx >= 0 ? ReadDecimal(reader, openIdx) : 0m;
                    decimal high = highIdx >= 0 ? ReadDecimal(reader, highIdx) : 0m;
                    decimal low = lowIdx >= 0 ? ReadDecimal(reader, lowIdx) : 0m;
                    decimal close = closeIdx >= 0 ? ReadDecimal(reader, closeIdx) : 0m;
                    decimal volume = volumeIdx >= 0 ? ReadDecimal(reader, volumeIdx) : 0m;
                    long closeTime = closeTimeIdx >= 0 ? ReadInt64(reader, closeTimeIdx) : 0;
                    decimal quoteVolume = quoteVolumeIdx >= 0 ? ReadDecimal(reader, quoteVolumeIdx) : 0m;
                    long tradeCount = tradeCountIdx >= 0 ? ReadInt64(reader, tradeCountIdx) : 0;
                    decimal tbVolume = tbVolumeIdx >= 0 ? ReadDecimal(reader, tbVolumeIdx) : 0m;
                    decimal tbQuoteVolume = tbQuoteVolumeIdx >= 0 ? ReadDecimal(reader, tbQuoteVolumeIdx) : 0m;

                    list.Add(new RawKline
                    {
                        OpenTime = openTime,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        CloseTime = closeTime,
                        QuoteVolume = quoteVolume,
                        TradeCount = tradeCount,
                        TakerBuyVolume = tbVolume,
                        TakerBuyQuoteVolume = tbQuoteVolume
                    });
                }

                if (list.Count > 1)
                {
                    list.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParquetDataReader] Error reading kline file {filePath}: {ex.Message}");
                return Array.Empty<RawKline>();
            }
        }

        private static int FindColumnOrdinal(DbDataReader reader, params string[] candidateNames)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                foreach (var cand in candidateNames)
                {
                    if (string.Equals(name, cand, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static long ReadInt64(DbDataReader reader, int ordinal)
        {
            var val = reader.GetValue(ordinal);
            if (val is long l) return l;
            if (val is int i) return i;
            if (val is ulong ul) return (long)ul;
            if (val is double d) return (long)d;
            if (val is DateTime dt) return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
            return Convert.ToInt64(val);
        }

        private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        {
            var val = reader.GetValue(ordinal);
            if (val is decimal dec) return dec;
            if (val is double d) return (decimal)d;
            if (val is float f) return (decimal)f;
            if (val is long l) return l;
            if (val is int i) return i;
            if (val is string s && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return Convert.ToDecimal(val, CultureInfo.InvariantCulture);
        }

        private static bool ReadBoolean(DbDataReader reader, int ordinal)
        {
            var val = reader.GetValue(ordinal);
            if (val is bool b) return b;
            if (val is int i) return i != 0;
            if (val is long l) return l != 0;
            if (val is string s && bool.TryParse(s, out var result)) return result;
            return Convert.ToBoolean(val);
        }

        #endregion

        #region 队列快速消费与清空方法

        public bool TryDequeueTick(out RawTick tick) => TickQueue.TryDequeue(out tick);
        public bool TryDequeueKline(out RawKline kline) => KlineQueue.TryDequeue(out kline);

        public void Clear()
        {
            while (KlineQueue.TryDequeue(out _)) { }
            while (TickQueue.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                _stopwatch.Stop();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
