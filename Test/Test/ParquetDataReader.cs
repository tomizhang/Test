using Binance.Net.Enums;
using DuckDB.NET.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Common
{
    #region 原始零转换数据结构体 (Zero-Allocation Raw Data Structs)

    /// <summary>
    /// 币安原始 K 线数据（紧凑内存结构体，零装箱，高性能）
    /// </summary>
    public readonly struct RawKline
    {
        public long OpenTime { get; init; }           // 开盘时间 (毫秒时间戳)
        public double Open { get; init; }             // 开盘价
        public double High { get; init; }             // 最高价
        public double Low { get; init; }              // 最低价
        public double Close { get; init; }            // 收盘价
        public double Volume { get; init; }           // 基础币成交量 (Base Asset Volume)
        public long CloseTime { get; init; }          // 收盘时间 (毫秒时间戳)
        public double QuoteVolume { get; init; }      // 计价币成交额 (Quote Asset Volume)
        public long TradeCount { get; init; }         // 这一根K线内的成交笔数
        public double TakerBuyVolume { get; init; }   // 主动买入成交量 (Taker Buy Base Asset Volume)
        public double TakerBuyQuoteVolume { get; init;}// 主动买入成交额 (Taker Buy Quote Asset Volume)

        public override string ToString()
        {
            DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(OpenTime).LocalDateTime;
            return $"[Kline {time:yyyy-MM-dd HH:mm:ss}] O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume:F4} Trades:{TradeCount}";
        }
    }

    /// <summary>
    /// 币安原始 Tick / Trade 数据（紧凑内存结构体，零装箱，高性能）
    /// </summary>
    public readonly struct RawTick
    {
        public long TradeId { get; init; }            // 逐笔成交 ID / AggTrade ID
        public double Price { get; init; }            // 成交价格
        public double Qty { get; init; }              // 成交数量
        public double QuoteQty { get; init; }         // 成交金额 (Price * Qty)
        public long Time { get; init; }               // 成交时间 (毫秒时间戳)
        public bool IsBuyerMaker { get; init; }       // 是否为买方挂单 (true = 卖方主动吃单/主动卖出, false = 买方主动吃单/主动买入)
        public bool IsBestMatch { get; init; }        // 是否为最优撮合

        public override string ToString()
        {
            DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(Time).LocalDateTime;
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
    /// 高性能币安 Parquet 数据读取帮助类 (DuckDB 向量化引擎 + 3 线程有序滑动窗口预取)
    /// 核心特性：
    /// 1. 严格对接 Config.cs 路径结构直接下推 read_parquet 文件物理路径（不进行 SQL 行级日期二次过滤）
    /// 2. 双独立队列：A: K线队列 (KlineQueue)，B: Tick队列 (TickQueue)，两队列互不阻塞完全解耦
    /// 3. Tick 数据采用 3 线程有序滑动窗口（Ordered Prefetching Pipeline），多线程并行满速读取，内存暂存保序，100% 杜绝时间乱序
    /// 4. 采用原生强类型读取直入 readonly struct，零装箱、零内存浪费
    /// 5. 内部全方位原子计数与数据统计监控
    /// </summary>
    public class ParquetDataReader : IDisposable
    {
        // 双独立队列
        public ConcurrentQueue<RawKline> KlineQueue { get; } = new ConcurrentQueue<RawKline>();
        public ConcurrentQueue<RawTick> TickQueue { get; } = new ConcurrentQueue<RawTick>();

        // 内部原子统计计数器
        private long _totalKlinesLoaded = 0;
        private long _totalTicksLoaded = 0;
        private int _loadedKlineDaysCount = 0;
        private int _loadedTickDaysCount = 0;
        private DateTime? _currentKlineDate = null;
        private DateTime? _currentTickDate = null;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly object _stateLock = new object();
        private bool _disposed = false;

        public ParquetDataReader()
        {
            Logger.EnsureStarted();
            _stopwatch.Start();
        }

        #region 实时统计属性 (Statistics Properties)

        /// <summary>
        /// 已加载的 K 线总条数
        /// </summary>
        public long TotalKlinesLoaded => Interlocked.Read(ref _totalKlinesLoaded);

        /// <summary>
        /// 已加载的 Tick/Trade 总条数
        /// </summary>
        public long TotalTicksLoaded => Interlocked.Read(ref _totalTicksLoaded);

        /// <summary>
        /// 已加载的 K 线天数
        /// </summary>
        public int LoadedKlineDaysCount => Volatile.Read(ref _loadedKlineDaysCount);

        /// <summary>
        /// 已加载的 Tick 天数
        /// </summary>
        public int LoadedTickDaysCount => Volatile.Read(ref _loadedTickDaysCount);

        /// <summary>
        /// 当前最后加载的 K 线日期
        /// </summary>
        public DateTime? CurrentKlineDate => _currentKlineDate;

        /// <summary>
        /// 当前最后加载的 Tick 日期
        /// </summary>
        public DateTime? CurrentTickDate => _currentTickDate;

        /// <summary>
        /// 当前 K 线队列积压量
        /// </summary>
        public int KlineQueueCount => KlineQueue.Count;

        /// <summary>
        /// 当前 Tick 队列积压量
        /// </summary>
        public int TickQueueCount => TickQueue.Count;

        /// <summary>
        /// 获取完整的统计快照
        /// </summary>
        public ReaderStatistics GetStatistics()
        {
            return new ReaderStatistics
            {
                TotalKlinesLoaded = TotalKlinesLoaded,
                TotalTicksLoaded = TotalTicksLoaded,
                LoadedKlineDaysCount = LoadedKlineDaysCount,
                LoadedTickDaysCount = LoadedTickDaysCount,
                CurrentKlineDate = CurrentKlineDate,
                CurrentTickDate = CurrentTickDate,
                KlineQueueCount = KlineQueueCount,
                TickQueueCount = TickQueueCount,
                ElapsedTime = _stopwatch.Elapsed
            };
        }

        /// <summary>
        /// 获取格式化统计摘要字符串
        /// </summary>
        public string GetStatusSummary()
        {
            return GetStatistics().ToString();
        }

        #endregion

        #region 单日主动加载接口 (Single Day Direct Loading)

        /// <summary>
        /// 主动读取指定币种某一天的 K 线 Parquet 文件并直接入队
        /// </summary>
        /// <param name="coin">交易对 (如 BTCUSDT)</param>
        /// <param name="date">日期</param>
        /// <param name="interval">K线周期 (默认 1m)</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>加载的 K 线条数</returns>
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
                _currentKlineDate = date.Date;
            }

            return klines.Length;
        }

        /// <summary>
        /// 主动读取指定币种某一天的 Tick/Trade Parquet 文件并直接入队
        /// </summary>
        /// <param name="coin">交易对 (如 BTCUSDT)</param>
        /// <param name="date">日期</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>加载的 Tick 条数</returns>
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
                _currentTickDate = date.Date;
            }

            return ticks.Length;
        }

        /// <summary>
        /// 主动同时读取指定币种某一天的 K 线与 Tick Parquet 文件并分别入双队列
        /// </summary>
        public async Task<(int klineCount, int tickCount)> LoadDayAsync(string coin, DateTime date, KlineInterval interval = KlineInterval.OneMinute, CancellationToken ct = default)
        {
            var klineTask = LoadKlineDayAsync(coin, date, interval, ct);
            var tickTask = LoadTickDayAsync(coin, date, ct);

            await Task.WhenAll(klineTask, tickTask).ConfigureAwait(false);
            return (klineTask.Result, tickTask.Result);
        }

        #endregion

        #region 3 线程有序滑动窗口并行流式预取 (Ordered Parallel Prefetching Stream)

        /// <summary>
        /// 启动 Tick 数据的后台 3 线程有序滑动窗口流式预取
        /// 默认开启 3 个工作线程并行读取连续 3 天的 Tick Parquet 文件并在内存暂存，
        /// 严格按照 Day 1 -> Day 2 -> Day 3 的时间顺序依次 Enqueue 入队，绝对不乱序。
        /// </summary>
        /// <param name="coin">交易对 (如 BTCUSDT)</param>
        /// <param name="startDate">起始日期 (包含)</param>
        /// <param name="endDate">结束日期 (包含)</param>
        /// <param name="parallelDays">并行预取天数窗口 (默认 3)</param>
        /// <param name="maxBufferedDays">队列最大积压天数背压阈值 (默认 3 天，防止消费慢撑爆内存)</param>
        /// <param name="ct">取消令牌</param>
        public async Task StartTickStreamingAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            int parallelDays = 3,
            int maxBufferedDays = 3,
            CancellationToken ct = default)
        {
            if (startDate > endDate) return;

            List<DateTime> allDates = new List<DateTime>();
            for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            {
                allDates.Add(d);
            }

            Logger.Log($"[ParquetDataReader] Starting Tick streaming for {coin} from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd} ({allDates.Count} days, ParallelWindow: {parallelDays}).");

            // 滑动窗口队列: 存放 (日期, 正在异步执行的读取Task)
            var slidingWindow = new Queue<(DateTime Date, Task<RawTick[]> FetchTask)>();
            int nextScheduleIndex = 0;

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
                // 出队当前时间线上最靠前的一天 (Day N)
                var (currDate, currTask) = slidingWindow.Dequeue();

                // 调度下一天 (Day N + parallelDays) 填补滑动窗口，保持始终有 parallelDays 个任务在后台并行读取
                if (nextScheduleIndex < allDates.Count && !ct.IsCancellationRequested)
                {
                    DateTime nextDate = allDates[nextScheduleIndex++];
                    string nextFilePath = Config.GetTradeFilePath(coin, nextDate, ".parquet");
                    var nextTask = Task.Run(() => ReadTickFileInternalAsync(nextFilePath, ct), ct);
                    slidingWindow.Enqueue((nextDate, nextTask));
                }

                // 等待当前 Day N 完成 (如果后续天已先读完会在内存 Task 中暂存，严格在此等待 Day N 先入队)
                RawTick[] ticks = await currTask.ConfigureAwait(false);

                // 背压控制：如果消费端积压过大（超过最大缓冲天数对应的数据量估计），适度等待消费端消化
                while (TickQueue.Count > maxBufferedDays * 2_000_000 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }

                // 将 Day N 的所有 Tick 按原始顺序严格推入 TickQueue
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

            Logger.Log($"[ParquetDataReader] Tick streaming completed for {coin}. Total Ticks: {TotalTicksLoaded:N0} across {LoadedTickDaysCount} days.");
        }

        /// <summary>
        /// 启动 K 线数据的后台异步流式快速加载（完全独立于 Tick 流，满速推进）
        /// </summary>
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

        #region DuckDB 核心原生文件下推解析逻辑 (Native DuckDB Pushdown Reader)

        /// <summary>
        /// 基于 DuckDB 原生向量化引擎直接读取 Tick/Trade Parquet 文件
        /// 直接文件路径下推 read_parquet('path')，不进行 SQL 行级日期二次过滤，原生类型强读装填
        /// </summary>
        private static async Task<RawTick[]> ReadTickFileInternalAsync(string filePath, CancellationToken ct)
        {
            if (!File.Exists(filePath))
            {
                Logger.Log($"[ParquetDataReader] Tick file not found: {filePath}, skipping.");
                return Array.Empty<RawTick>();
            }

            try
            {
                // 使用内存 DuckDB 连接 (线程隔离，纳秒级开销，安全并行)
                using var connection = new DuckDBConnection("DataSource=:memory:");
                await connection.OpenAsync(ct).ConfigureAwait(false);

                using var command = connection.CreateCommand();
                string normalizedPath = filePath.Replace('\\', '/');
                command.CommandText = $"SELECT * FROM read_parquet('{normalizedPath}')";

                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                // 智能解析列索引（自适应币安不同版本命名：trade_id, id, price, p, time, transact_time 等）
                int idIdx = FindColumnOrdinal(reader, "trade_id", "id", "tradeId", "agg_trade_id", "aggregate_trade_id");
                int priceIdx = FindColumnOrdinal(reader, "price", "p");
                int qtyIdx = FindColumnOrdinal(reader, "qty", "quantity", "q");
                int quoteQtyIdx = FindColumnOrdinal(reader, "quote_qty", "quoteQty", "quote_quantity", "quote_volume");
                int timeIdx = FindColumnOrdinal(reader, "time", "transact_time", "timestamp", "trade_time", "T");
                int isBuyerMakerIdx = FindColumnOrdinal(reader, "is_buyer_maker", "isBuyerMaker", "buyer_maker", "m");
                int isBestMatchIdx = FindColumnOrdinal(reader, "is_best_match", "isBestMatch", "M");

                var list = new List<RawTick>(200000);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    long tradeId = idIdx >= 0 ? ReadInt64(reader, idIdx) : 0;
                    double price = priceIdx >= 0 ? ReadDouble(reader, priceIdx) : 0.0;
                    double qty = qtyIdx >= 0 ? ReadDouble(reader, qtyIdx) : 0.0;
                    double quoteQty = quoteQtyIdx >= 0 ? ReadDouble(reader, quoteQtyIdx) : (price * qty);
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

                return list.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParquetDataReader] Error reading tick file {filePath}: {ex.Message}");
                return Array.Empty<RawTick>();
            }
        }

        /// <summary>
        /// 基于 DuckDB 原生向量化引擎直接读取 Kline Parquet 文件
        /// 直接文件路径下推 read_parquet('path')，不进行 SQL 行级日期二次过滤
        /// </summary>
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
                int volumeIdx = FindColumnOrdinal(reader, "volume", "v", "base_volume", "vol");
                int closeTimeIdx = FindColumnOrdinal(reader, "close_time", "closeTime", "endTime", "end_time", "T");
                int quoteVolIdx = FindColumnOrdinal(reader, "quote_volume", "quoteVolume", "quote_asset_volume", "q");
                int countIdx = FindColumnOrdinal(reader, "count", "trades", "trade_count", "number_of_trades", "n");
                int takerBuyVolIdx = FindColumnOrdinal(reader, "taker_buy_volume", "takerBuyVolume", "taker_buy_base_asset_volume", "V");
                int takerBuyQuoteVolIdx = FindColumnOrdinal(reader, "taker_buy_quote_volume", "takerBuyQuoteVolume", "taker_buy_quote_asset_volume", "Q");

                var list = new List<RawKline>(2000);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    long openTime = openTimeIdx >= 0 ? ReadInt64(reader, openTimeIdx) : 0;
                    double open = openIdx >= 0 ? ReadDouble(reader, openIdx) : 0.0;
                    double high = highIdx >= 0 ? ReadDouble(reader, highIdx) : 0.0;
                    double low = lowIdx >= 0 ? ReadDouble(reader, lowIdx) : 0.0;
                    double close = closeIdx >= 0 ? ReadDouble(reader, closeIdx) : 0.0;
                    double volume = volumeIdx >= 0 ? ReadDouble(reader, volumeIdx) : 0.0;
                    long closeTime = closeTimeIdx >= 0 ? ReadInt64(reader, closeTimeIdx) : 0;
                    double quoteVol = quoteVolIdx >= 0 ? ReadDouble(reader, quoteVolIdx) : 0.0;
                    long count = countIdx >= 0 ? ReadInt64(reader, countIdx) : 0;
                    double takerBuyVol = takerBuyVolIdx >= 0 ? ReadDouble(reader, takerBuyVolIdx) : 0.0;
                    double takerBuyQuoteVol = takerBuyQuoteVolIdx >= 0 ? ReadDouble(reader, takerBuyQuoteVolIdx) : 0.0;

                    list.Add(new RawKline
                    {
                        OpenTime = openTime,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        CloseTime = closeTime,
                        QuoteVolume = quoteVol,
                        TradeCount = count,
                        TakerBuyVolume = takerBuyVol,
                        TakerBuyQuoteVolume = takerBuyQuoteVol
                    });
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParquetDataReader] Error reading kline file {filePath}: {ex.Message}");
                return Array.Empty<RawKline>();
            }
        }

        #endregion

        #region 高性能列解析与零装箱读取辅助函数 (Fast Helpers)

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

        private static double ReadDouble(DbDataReader reader, int ordinal)
        {
            var val = reader.GetValue(ordinal);
            if (val is double d) return d;
            if (val is float f) return f;
            if (val is decimal dec) return (double)dec;
            if (val is long l) return l;
            if (val is int i) return i;
            return Convert.ToDouble(val);
        }

        private static bool ReadBoolean(DbDataReader reader, int ordinal)
        {
            var val = reader.GetValue(ordinal);
            if (val is bool b) return b;
            if (val is int i) return i != 0;
            if (val is long l) return l != 0;
            if (val is string s) return bool.TryParse(s, out var result) && result;
            return Convert.ToBoolean(val);
        }

        #endregion

        #region 队列快速消费与清空方法 (Consumer Helpers)

        /// <summary>
        /// 从 Tick 队列尝试取出一个 Tick
        /// </summary>
        public bool TryDequeueTick(out RawTick tick) => TickQueue.TryDequeue(out tick);

        /// <summary>
        /// 从 K 线队列尝试取出一根 K 线
        /// </summary>
        public bool TryDequeueKline(out RawKline kline) => KlineQueue.TryDequeue(out kline);

        /// <summary>
        /// 批量从 Tick 队列出队到数组缓冲区（极速批量消费）
        /// </summary>
        public int DequeueTickBatch(RawTick[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return 0;
            int count = 0;
            while (count < buffer.Length && TickQueue.TryDequeue(out RawTick tick))
            {
                buffer[count++] = tick;
            }
            return count;
        }

        /// <summary>
        /// 批量从 Tick 队列出队到 Span 内存缓冲区（极速批量消费）
        /// </summary>
        public int DequeueTickBatch(Span<RawTick> buffer)
        {
            int count = 0;
            while (count < buffer.Length && TickQueue.TryDequeue(out RawTick tick))
            {
                buffer[count++] = tick;
            }
            return count;
        }

        /// <summary>
        /// 批量从 K 线队列出队到数组缓冲区
        /// </summary>
        public int DequeueKlineBatch(RawKline[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return 0;
            int count = 0;
            while (count < buffer.Length && KlineQueue.TryDequeue(out RawKline kline))
            {
                buffer[count++] = kline;
            }
            return count;
        }

        /// <summary>
        /// 批量从 K 线队列出队到 Span 内存缓冲区
        /// </summary>
        public int DequeueKlineBatch(Span<RawKline> buffer)
        {
            int count = 0;
            while (count < buffer.Length && KlineQueue.TryDequeue(out RawKline kline))
            {
                buffer[count++] = kline;
            }
            return count;
        }

        /// <summary>
        /// 清空所有队列与统计状态
        /// </summary>
        public void Clear()
        {
            lock (_stateLock)
            {
                while (KlineQueue.TryDequeue(out _)) { }
                while (TickQueue.TryDequeue(out _)) { }

                Interlocked.Exchange(ref _totalKlinesLoaded, 0);
                Interlocked.Exchange(ref _totalTicksLoaded, 0);
                Volatile.Write(ref _loadedKlineDaysCount, 0);
                Volatile.Write(ref _loadedTickDaysCount, 0);
                _currentKlineDate = null;
                _currentTickDate = null;
                _stopwatch.Restart();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
            _stopwatch.Stop();
        }

        #endregion
    }
}
