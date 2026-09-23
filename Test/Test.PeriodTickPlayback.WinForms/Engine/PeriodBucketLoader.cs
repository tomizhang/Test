using Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Test.PeriodTickPlayback.WinForms.Models;

namespace Test.PeriodTickPlayback.WinForms.Engine
{
    /// <summary>
    /// 宏观周期桶数据加载与分片切片引擎
    /// 1. 高速并行预取指定时间范围的真实 Parquet Tick 数据
    /// 2. 严格按指定大周期 (如 30 分钟) 自然时钟对齐，将连续 Tick 切分至各独立周期桶
    /// 3. 精确预计算各桶定型后的 MacroKline (零误差，纯真实合成)
    /// </summary>
    public static class PeriodBucketLoader
    {
        public static async Task<List<PeriodBucket>> LoadBucketsAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            TimeSpan periodSpan,
            Action<string, int>? onProgress = null,
            CancellationToken ct = default)
        {
            var buckets = new List<PeriodBucket>();

            // 1. 获取所有待加载的日期列表
            var dates = new List<DateTime>();
            for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            {
                dates.Add(d);
            }

            if (dates.Count == 0) return buckets;

            onProgress?.Invoke($"[加载启动] 准备读取 {coin} 从 {startDate:yyyy-MM-dd} 到 {endDate:yyyy-MM-dd} 共 {dates.Count} 天 Tick 数据...", 5);

            // 2. 逐日流式读取 Parquet 文件中的 RawTick
            var allTicks = new List<RawTick>(dates.Count * 200000);

            using var reader = new ParquetDataReader();

            for (int i = 0; i < dates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var curDate = dates[i];
                string filePath = Config.GetTradeFilePath(coin, curDate, ".parquet");

                int progressPct = 5 + (int)((i + 0.5) / dates.Count * 65.0);
                onProgress?.Invoke($"正在读取 {coin} {curDate:yyyy-MM-dd} 逐笔成交数据 ({i + 1}/{dates.Count})...", progressPct);

                if (!File.Exists(filePath))
                {
                    // 容错检查 zip 或备用路径
                    string zipPath = Config.GetTradeFilePath(coin, curDate, ".zip");
                    if (!File.Exists(zipPath))
                    {
                        continue;
                    }
                }

                try
                {
                    await reader.LoadTickDayAsync(coin, curDate, ct).ConfigureAwait(false);
                    while (reader.TickQueue.TryDequeue(out var tick))
                    {
                        allTicks.Add(tick);
                    }
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"[警告] 读取 {curDate:yyyy-MM-dd} 失败: {ex.Message}", progressPct);
                }
            }

            if (allTicks.Count == 0)
            {
                onProgress?.Invoke($"[提示] 未在所选区间内找到有效的 Tick 数据。", 100);
                return buckets;
            }

            onProgress?.Invoke($"[分桶切片] 共读取 {allTicks.Count:N0} 笔 Tick，正在按照 {periodSpan.TotalMinutes} 分钟周期对齐切片...", 80);

            // 3. 严格按时间对齐进行宏观周期桶切分
            buckets = SliceTicksIntoBuckets(allTicks, periodSpan);

            onProgress?.Invoke($"[切片完成] 成功构建 {buckets.Count:N0} 个宏观周期桶 (总计 {allTicks.Count:N0} Ticks)！", 100);

            return buckets;
        }

        /// <summary>
        /// 异步分批读取指定币种单日 (单批次) 的 Tick 数据并切分为宏观周期桶 (零内存堆积，极速响应)
        /// </summary>
        public static async Task<List<PeriodBucket>> LoadSingleDayBatchAsync(
            string coin,
            DateTime date,
            TimeSpan periodSpan,
            int startingBucketIndex = 0,
            Action<string, int>? onProgress = null,
            CancellationToken ct = default)
        {
            var buckets = new List<PeriodBucket>();
            onProgress?.Invoke($"[分批读取] 正在加载 {coin} {date:yyyy-MM-dd} 逐笔成交数据...", 20);

            var dayTicks = new List<RawTick>(200000);
            using var reader = new ParquetDataReader();

            string filePath = Config.GetTradeFilePath(coin, date, ".parquet");
            if (!File.Exists(filePath))
            {
                string zipPath = Config.GetTradeFilePath(coin, date, ".zip");
                if (!File.Exists(zipPath))
                {
                    onProgress?.Invoke($"[提示] {date:yyyy-MM-dd} 未找到 Parquet/Zip 数据文件，跳过。", 100);
                    return buckets;
                }
            }

            try
            {
                await reader.LoadTickDayAsync(coin, date, ct).ConfigureAwait(false);
                while (reader.TickQueue.TryDequeue(out var tick))
                {
                    dayTicks.Add(tick);
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"[警告] 读取 {date:yyyy-MM-dd} 失败: {ex.Message}", 50);
                return buckets;
            }

            if (dayTicks.Count == 0)
            {
                onProgress?.Invoke($"[提示] {date:yyyy-MM-dd} 未找到有效 Tick 数据。", 100);
                return buckets;
            }

            onProgress?.Invoke($"[分批切片] {date:yyyy-MM-dd} 读取 {dayTicks.Count:N0} 笔 Tick，正在构建周期桶...", 75);
            buckets = SliceTicksIntoBuckets(dayTicks, periodSpan, startingBucketIndex);
            onProgress?.Invoke($"[分批完成] {date:yyyy-MM-dd} 成功构建 {buckets.Count:N0} 个周期桶 (共 {dayTicks.Count:N0} Ticks)！", 100);
            return buckets;
        }

        /// <summary>
        /// 将 Tick 序列按时钟对齐的周期时间跨度切分为连续的周期桶 (支持任意秒级与分钟级 TimeSpan)
        /// </summary>
        public static List<PeriodBucket> SliceTicksIntoBuckets(List<RawTick> ticks, TimeSpan periodSpan, int startingBucketIndex = 0)
        {
            var buckets = new List<PeriodBucket>();
            if (ticks == null || ticks.Count == 0) return buckets;

            if (periodSpan < TimeSpan.FromSeconds(1))
            {
                periodSpan = TimeSpan.FromSeconds(1);
            }

            PeriodBucket? currentBucket = null;
            DateTime currentBucketStart = DateTime.MinValue;
            DateTime currentBucketEnd = DateTime.MinValue;

            int bucketCounter = startingBucketIndex;

            for (int i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                DateTime tickTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).UtcDateTime;

                // 计算该 Tick 所属的自然时钟对齐区间起点与终点
                DateTime expectedBucketStart = AlignTimeToPeriod(tickTime, periodSpan);
                DateTime expectedBucketEnd = expectedBucketStart.Add(periodSpan);

                // 若跨入新周期桶
                if (currentBucket == null || tickTime >= currentBucketEnd || expectedBucketStart != currentBucketStart)
                {
                    // 封闭上一个桶
                    if (currentBucket != null && currentBucket.Ticks.Count > 0)
                    {
                        FinalizeBucket(currentBucket);
                        buckets.Add(currentBucket);
                    }

                    // 创建新桶
                    currentBucketStart = expectedBucketStart;
                    currentBucketEnd = expectedBucketEnd;
                    currentBucket = new PeriodBucket
                    {
                        BucketIndex = bucketCounter++,
                        StartTime = currentBucketStart,
                        EndTime = currentBucketEnd
                    };
                }

                currentBucket.Ticks.Add(tick);
            }

            // 封闭最后一个桶
            if (currentBucket != null && currentBucket.Ticks.Count > 0)
            {
                FinalizeBucket(currentBucket);
                buckets.Add(currentBucket);
            }

            return buckets;
        }

        /// <summary>
        /// 自然时钟对齐算法：将时间向下对齐到最近的 periodSpan (支持任意秒级、分钟级或日线)
        /// </summary>
        public static DateTime AlignTimeToPeriod(DateTime dt, TimeSpan periodSpan)
        {
            if (periodSpan.TotalDays >= 1) // 日线及以上
            {
                return dt.Date;
            }

            long spanTicks = periodSpan.Ticks;
            if (spanTicks <= 0) spanTicks = TimeSpan.FromSeconds(1).Ticks;

            long ticksSinceMidnight = (dt - dt.Date).Ticks;
            long alignedTicks = (ticksSinceMidnight / spanTicks) * spanTicks;
            return dt.Date.AddTicks(alignedTicks);
        }

        /// <summary>
        /// 兼容重载：时钟对齐算法：将时间向下对齐到最近的 periodMinutes 整数倍
        /// </summary>
        public static DateTime AlignTimeToPeriod(DateTime dt, int periodMinutes)
        {
            return AlignTimeToPeriod(dt, TimeSpan.FromMinutes(periodMinutes));
        }

        /// <summary>
        /// 预计算该桶完全定型后的 MacroKline
        /// </summary>
        private static void FinalizeBucket(PeriodBucket bucket)
        {
            if (bucket.Ticks.Count == 0) return;

            decimal open = bucket.Ticks[0].Price;
            decimal high = decimal.MinValue;
            decimal low = decimal.MaxValue;
            decimal close = bucket.Ticks[^1].Price;
            decimal vol = 0m;
            decimal quoteVol = 0m;

            for (int i = 0; i < bucket.Ticks.Count; i++)
            {
                var t = bucket.Ticks[i];
                if (t.Price > high) high = t.Price;
                if (t.Price < low) low = t.Price;
                vol += t.Qty;
                quoteVol += t.QuoteQty;
            }

            long openMs = new DateTimeOffset(bucket.StartTime).ToUnixTimeMilliseconds();
            long closeMs = new DateTimeOffset(bucket.EndTime).ToUnixTimeMilliseconds();

            bucket.FinalKline = new MacroKline
            {
                BarIndex = bucket.BucketIndex,
                OpenTime = bucket.StartTime,
                CloseTime = bucket.EndTime,
                OpenTimeMs = openMs,
                CloseTimeMs = closeMs,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = vol,
                QuoteVolume = quoteVol,
                TradeCount = bucket.Ticks.Count
            };
        }
    }
}
