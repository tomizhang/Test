using Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Test.PercentageBar.WinForms.Models;

namespace Test.PercentageBar.WinForms.Engine
{
    /// <summary>
    /// 百分比 / 固定价格 K 线生成统计指标
    /// </summary>
    public class PercentBarGenerationStats
    {
        public int TotalTicks { get; set; }
        public int TotalBars { get; set; }
        public decimal MaxPrice { get; set; }
        public decimal MinPrice { get; set; }
        public TimeSpan TotalTimeSpan { get; set; }
        public TimeSpan AverageBarDuration { get; set; }
        public TimeSpan MinBarDuration { get; set; }
        public TimeSpan MaxBarDuration { get; set; }
        public double AverageTicksPerBar { get; set; }
        public decimal TotalVolume { get; set; }
        public decimal TotalQuoteVolume { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public double TicksPerSecond => ElapsedMilliseconds > 0 ? (double)TotalTicks / (ElapsedMilliseconds / 1000.0) : 0;
    }

    /// <summary>
    /// 高性能百分比 / 固定价格变化 K 线聚合与生成服务 (纯栈上结构体增量状态机, 零 GC 内存堆开销)
    /// </summary>
    public class PercentageBarGenerator
    {
        /// <summary>
        /// 批量全量生成百分比 / 固定价格 K 线
        /// </summary>
        public static (List<PercentageKline> bars, PercentBarGenerationStats stats) GenerateFromTicks(
            IReadOnlyList<RawTick> ticks,
            decimal thresholdValue,
            SliceUnitType sliceUnit = SliceUnitType.Percentage,
            PercentBarMode mode = PercentBarMode.ChangeFromOpen,
            CancellationToken ct = default,
            Action<int, int>? progressCallback = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new List<PercentageKline>(ticks.Count / 100 + 32);
            var stats = new PercentBarGenerationStats();

            if (ticks == null || ticks.Count == 0 || thresholdValue <= 0m)
            {
                sw.Stop();
                stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                return (result, stats);
            }

            decimal thresholdFrac = thresholdValue / 100.0m;
            int barIndex = 0;

            // 当前正在构建的 Bar 状态 (纯值类型寄存)
            bool isBarBuilding = false;
            decimal open = 0m, high = 0m, low = 0m, close = 0m;
            long openTime = 0, closeTime = 0;
            decimal volume = 0m, quoteVolume = 0m, takerBuyVol = 0m, takerBuyQuote = 0m;
            long tradeCount = 0;
            int tickCount = 0;

            decimal globalMaxPrice = decimal.MinValue;
            decimal globalMinPrice = decimal.MaxValue;
            decimal totalVol = 0m;
            decimal totalQuote = 0m;

            TimeSpan minDuration = TimeSpan.MaxValue;
            TimeSpan maxDuration = TimeSpan.MinValue;
            long totalDurationMs = 0;

            int count = ticks.Count;
            int reportInterval = Math.Max(10000, count / 50);

            for (int i = 0; i < count; i++)
            {
                if ((i & 0x3FFF) == 0 && ct.IsCancellationRequested)
                {
                    break;
                }

                var tick = ticks[i];
                decimal p = tick.Price;
                if (p <= 0m) continue;

                if (p > globalMaxPrice) globalMaxPrice = p;
                if (p < globalMinPrice) globalMinPrice = p;
                totalVol += tick.Qty;
                totalQuote += tick.QuoteQty;

                if (!isBarBuilding)
                {
                    // 开启首根/新一根 Bar
                    isBarBuilding = true;
                    open = p;
                    high = p;
                    low = p;
                    close = p;
                    openTime = tick.Time;
                    closeTime = tick.Time;
                    volume = tick.Qty;
                    quoteVolume = tick.QuoteQty;
                    tradeCount = 1;
                    takerBuyVol = (!tick.IsBuyerMaker) ? tick.Qty : 0m;
                    takerBuyQuote = (!tick.IsBuyerMaker) ? tick.QuoteQty : 0m;
                    tickCount = 1;
                    continue;
                }

                // 累加当前 Bar 状态
                if (p > high) high = p;
                if (p < low) low = p;
                close = p;
                closeTime = tick.Time;
                volume += tick.Qty;
                quoteVolume += tick.QuoteQty;
                tradeCount++;
                tickCount++;
                if (!tick.IsBuyerMaker)
                {
                    takerBuyVol += tick.Qty;
                    takerBuyQuote += tick.QuoteQty;
                }

                // 判定是否触发切分
                bool isTriggered = false;

                if (sliceUnit == SliceUnitType.Percentage)
                {
                    switch (mode)
                    {
                        case PercentBarMode.ChangeFromOpen:
                            decimal changePct = (p - open) / open;
                            if (changePct >= thresholdFrac || changePct <= -thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.HighLowRange:
                            if (low > 0m && ((high - low) / low) >= thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.Renko:
                            decimal renkoChange = (p - open) / open;
                            if (renkoChange >= thresholdFrac || renkoChange <= -thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;
                    }
                }
                else // SliceUnitType.FixedPrice
                {
                    switch (mode)
                    {
                        case PercentBarMode.ChangeFromOpen:
                            decimal diff = p - open;
                            if (diff >= thresholdValue || diff <= -thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.HighLowRange:
                            if ((high - low) >= thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.Renko:
                            decimal renkoDiff = p - open;
                            if (renkoDiff >= thresholdValue || renkoDiff <= -thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;
                    }
                }

                if (isTriggered)
                {
                    // 封盘当前 Bar
                    var completedBar = new PercentageKline
                    {
                        BarIndex = barIndex++,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        OpenTime = openTime,
                        CloseTime = closeTime,
                        Volume = volume,
                        QuoteVolume = quoteVolume,
                        TradeCount = tradeCount,
                        TakerBuyVolume = takerBuyVol,
                        TakerBuyQuoteVolume = takerBuyQuote,
                        TickCount = tickCount
                    };

                    result.Add(completedBar);

                    var dur = completedBar.Duration;
                    if (dur < minDuration) minDuration = dur;
                    if (dur > maxDuration) maxDuration = dur;
                    totalDurationMs += (long)dur.TotalMilliseconds;

                    // 开启下一根 Bar
                    open = p;
                    high = p;
                    low = p;
                    close = p;
                    openTime = tick.Time;
                    closeTime = tick.Time;
                    volume = 0m;
                    quoteVolume = 0m;
                    tradeCount = 0;
                    takerBuyVol = 0m;
                    takerBuyQuote = 0m;
                    tickCount = 0;
                }

                if (progressCallback != null && (i % reportInterval == 0 || i == count - 1))
                {
                    progressCallback(i + 1, barIndex);
                }
            }

            // 处理最后一根未达成突破阈值的剩余 Bar (封盘收尾)
            if (isBarBuilding && tickCount > 0)
            {
                var lastBar = new PercentageKline
                {
                    BarIndex = barIndex++,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    OpenTime = openTime,
                    CloseTime = closeTime,
                    Volume = volume,
                    QuoteVolume = quoteVolume,
                    TradeCount = tradeCount,
                    TakerBuyVolume = takerBuyVol,
                    TakerBuyQuoteVolume = takerBuyQuote,
                    TickCount = tickCount
                };

                result.Add(lastBar);

                var dur = lastBar.Duration;
                if (dur < minDuration) minDuration = dur;
                if (dur > maxDuration) maxDuration = dur;
                totalDurationMs += (long)dur.TotalMilliseconds;
            }

            sw.Stop();

            // 填充统计指标
            stats.TotalTicks = count;
            stats.TotalBars = barIndex;
            stats.MaxPrice = globalMaxPrice == decimal.MinValue ? 0m : globalMaxPrice;
            stats.MinPrice = globalMinPrice == decimal.MaxValue ? 0m : globalMinPrice;
            stats.TotalVolume = totalVol;
            stats.TotalQuoteVolume = totalQuote;
            stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;

            if (result.Count > 0)
            {
                long startMs = result[0].OpenTime;
                long endMs = result[result.Count - 1].CloseTime;
                stats.TotalTimeSpan = TimeSpan.FromMilliseconds(Math.Max(0, endMs - startMs));
                stats.AverageBarDuration = TimeSpan.FromMilliseconds(totalDurationMs / (double)result.Count);
                stats.MinBarDuration = minDuration == TimeSpan.MaxValue ? TimeSpan.Zero : minDuration;
                stats.MaxBarDuration = maxDuration == TimeSpan.MinValue ? TimeSpan.Zero : maxDuration;
                stats.AverageTicksPerBar = (double)count / result.Count;
            }

            return (result, stats);
        }

        /// <summary>
        /// 逐条动态流式生成 K 线
        /// </summary>
        public static async Task GenerateStreamingAsync(
            IReadOnlyList<RawTick> ticks,
            decimal thresholdValue,
            SliceUnitType sliceUnit,
            PercentBarMode mode,
            Func<PercentageKline, int, PercentBarGenerationStats, Task> onBarGeneratedAsync,
            Func<bool> checkPauseFunc,
            CancellationToken ct = default,
            int batchYieldBars = 1,
            int sleepIntervalMs = 0)
        {
            var sw = Stopwatch.StartNew();
            var stats = new PercentBarGenerationStats();

            if (ticks == null || ticks.Count == 0 || thresholdValue <= 0m)
            {
                sw.Stop();
                stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                return;
            }

            decimal thresholdFrac = thresholdValue / 100.0m;
            int barIndex = 0;

            bool isBarBuilding = false;
            decimal open = 0m, high = 0m, low = 0m, close = 0m;
            long openTime = 0, closeTime = 0;
            decimal volume = 0m, quoteVolume = 0m, takerBuyVol = 0m, takerBuyQuote = 0m;
            long tradeCount = 0;
            int tickCount = 0;

            decimal globalMaxPrice = decimal.MinValue;
            decimal globalMinPrice = decimal.MaxValue;
            decimal totalVol = 0m;
            decimal totalQuote = 0m;

            TimeSpan minDuration = TimeSpan.MaxValue;
            TimeSpan maxDuration = TimeSpan.MinValue;
            long totalDurationMs = 0;

            int count = ticks.Count;
            long firstTickTime = ticks[0].Time;
            int yieldCounter = 0;

            for (int i = 0; i < count; i++)
            {
                if ((i & 0x7FF) == 0)
                {
                    if (ct.IsCancellationRequested) break;

                    while (checkPauseFunc() && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                }

                var tick = ticks[i];
                decimal p = tick.Price;
                if (p <= 0m) continue;

                if (p > globalMaxPrice) globalMaxPrice = p;
                if (p < globalMinPrice) globalMinPrice = p;
                totalVol += tick.Qty;
                totalQuote += tick.QuoteQty;

                if (!isBarBuilding)
                {
                    isBarBuilding = true;
                    open = p;
                    high = p;
                    low = p;
                    close = p;
                    openTime = tick.Time;
                    closeTime = tick.Time;
                    volume = tick.Qty;
                    quoteVolume = tick.QuoteQty;
                    tradeCount = 1;
                    takerBuyVol = (!tick.IsBuyerMaker) ? tick.Qty : 0m;
                    takerBuyQuote = (!tick.IsBuyerMaker) ? tick.QuoteQty : 0m;
                    tickCount = 1;
                    continue;
                }

                if (p > high) high = p;
                if (p < low) low = p;
                close = p;
                closeTime = tick.Time;
                volume += tick.Qty;
                quoteVolume += tick.QuoteQty;
                tradeCount++;
                tickCount++;
                if (!tick.IsBuyerMaker)
                {
                    takerBuyVol += tick.Qty;
                    takerBuyQuote += tick.QuoteQty;
                }

                bool isTriggered = false;

                if (sliceUnit == SliceUnitType.Percentage)
                {
                    switch (mode)
                    {
                        case PercentBarMode.ChangeFromOpen:
                            decimal changePct = (p - open) / open;
                            if (changePct >= thresholdFrac || changePct <= -thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.HighLowRange:
                            if (low > 0m && ((high - low) / low) >= thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.Renko:
                            decimal renkoChange = (p - open) / open;
                            if (renkoChange >= thresholdFrac || renkoChange <= -thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;
                    }
                }
                else
                {
                    switch (mode)
                    {
                        case PercentBarMode.ChangeFromOpen:
                            decimal diff = p - open;
                            if (diff >= thresholdValue || diff <= -thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.HighLowRange:
                            if ((high - low) >= thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.Renko:
                            decimal renkoDiff = p - open;
                            if (renkoDiff >= thresholdValue || renkoDiff <= -thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;
                    }
                }

                if (isTriggered)
                {
                    var completedBar = new PercentageKline
                    {
                        BarIndex = barIndex++,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        OpenTime = openTime,
                        CloseTime = closeTime,
                        Volume = volume,
                        QuoteVolume = quoteVolume,
                        TradeCount = tradeCount,
                        TakerBuyVolume = takerBuyVol,
                        TakerBuyQuoteVolume = takerBuyQuote,
                        TickCount = tickCount
                    };

                    var dur = completedBar.Duration;
                    if (dur < minDuration) minDuration = dur;
                    if (dur > maxDuration) maxDuration = dur;
                    totalDurationMs += (long)dur.TotalMilliseconds;

                    stats.TotalTicks = i + 1;
                    stats.TotalBars = barIndex;
                    stats.MaxPrice = globalMaxPrice;
                    stats.MinPrice = globalMinPrice;
                    stats.TotalVolume = totalVol;
                    stats.TotalQuoteVolume = totalQuote;
                    stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                    stats.TotalTimeSpan = TimeSpan.FromMilliseconds(Math.Max(0, closeTime - firstTickTime));
                    stats.AverageBarDuration = TimeSpan.FromMilliseconds(totalDurationMs / (double)barIndex);
                    stats.MinBarDuration = minDuration == TimeSpan.MaxValue ? TimeSpan.Zero : minDuration;
                    stats.MaxBarDuration = maxDuration == TimeSpan.MinValue ? TimeSpan.Zero : maxDuration;
                    stats.AverageTicksPerBar = (double)(i + 1) / barIndex;

                    yieldCounter++;
                    if (yieldCounter >= batchYieldBars || sleepIntervalMs > 0)
                    {
                        yieldCounter = 0;
                        await onBarGeneratedAsync(completedBar, i + 1, stats).ConfigureAwait(false);
                        if (sleepIntervalMs > 0)
                        {
                            await Task.Delay(sleepIntervalMs, ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await onBarGeneratedAsync(completedBar, i + 1, stats).ConfigureAwait(false);
                    }

                    open = p;
                    high = p;
                    low = p;
                    close = p;
                    openTime = tick.Time;
                    closeTime = tick.Time;
                    volume = 0m;
                    quoteVolume = 0m;
                    tradeCount = 0;
                    takerBuyVol = 0m;
                    takerBuyQuote = 0m;
                    tickCount = 0;
                }
            }

            if (isBarBuilding && tickCount > 0)
            {
                var lastBar = new PercentageKline
                {
                    BarIndex = barIndex++,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    OpenTime = openTime,
                    CloseTime = closeTime,
                    Volume = volume,
                    QuoteVolume = quoteVolume,
                    TradeCount = tradeCount,
                    TakerBuyVolume = takerBuyVol,
                    TakerBuyQuoteVolume = takerBuyQuote,
                    TickCount = tickCount
                };

                var dur = lastBar.Duration;
                if (dur < minDuration) minDuration = dur;
                if (dur > maxDuration) maxDuration = dur;
                totalDurationMs += (long)dur.TotalMilliseconds;

                stats.TotalTicks = count;
                stats.TotalBars = barIndex;
                stats.MaxPrice = globalMaxPrice;
                stats.MinPrice = globalMinPrice;
                stats.TotalVolume = totalVol;
                stats.TotalQuoteVolume = totalQuote;
                stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                stats.TotalTimeSpan = TimeSpan.FromMilliseconds(Math.Max(0, closeTime - firstTickTime));
                stats.AverageBarDuration = TimeSpan.FromMilliseconds(totalDurationMs / (double)barIndex);
                stats.MinBarDuration = minDuration == TimeSpan.MaxValue ? TimeSpan.Zero : minDuration;
                stats.MaxBarDuration = maxDuration == TimeSpan.MinValue ? TimeSpan.Zero : maxDuration;
                stats.AverageTicksPerBar = (double)count / barIndex;

                await onBarGeneratedAsync(lastBar, count, stats).ConfigureAwait(false);
            }

            sw.Stop();
            stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// 分批流式聚合会话状态机 (跨批次跨天保持构建状态与全局统计指标, 零堆内存暴涨)
    /// </summary>
    public class IncrementalPercentageBarSession
    {
        private readonly decimal _thresholdValue;
        private readonly decimal _thresholdFrac;
        private readonly SliceUnitType _sliceUnit;
        private readonly PercentBarMode _mode;
        private int _barIndex = 0;

        // 当前正在构建的未完成 Bar 状态 (纯值类型寄存)
        private bool _isBarBuilding = false;
        private decimal _open = 0m, _high = 0m, _low = 0m, _close = 0m;
        private long _openTime = 0, _closeTime = 0;
        private decimal _volume = 0m, _quoteVolume = 0m, _takerBuyVol = 0m, _takerBuyQuote = 0m;
        private long _tradeCount = 0;
        private int _tickCount = 0;

        // 全局统计累加器
        public PercentBarGenerationStats Stats { get; } = new PercentBarGenerationStats();
        private decimal _globalMaxPrice = decimal.MinValue;
        private decimal _globalMinPrice = decimal.MaxValue;
        private decimal _totalVol = 0m;
        private decimal _totalQuote = 0m;
        private TimeSpan _minDuration = TimeSpan.MaxValue;
        private TimeSpan _maxDuration = TimeSpan.MinValue;
        private long _totalDurationMs = 0;
        private long _firstTickTime = -1;
        private int _totalTicks = 0;
        private readonly Stopwatch _sw = new Stopwatch();

        public IncrementalPercentageBarSession(
            decimal thresholdValue,
            SliceUnitType sliceUnit = SliceUnitType.Percentage,
            PercentBarMode mode = PercentBarMode.ChangeFromOpen)
        {
            _thresholdValue = thresholdValue;
            _thresholdFrac = thresholdValue / 100.0m;
            _sliceUnit = sliceUnit;
            _mode = mode;
            _sw.Start();
        }

        /// <summary>
        /// 消费一个分批数据块 (如单日或单批次 Tick 数据) 并增量发射生成的百分比 / 固定价格 K 线
        /// </summary>
        public async Task ProcessBatchAsync(
            RawTick[] ticks,
            Func<PercentageKline, int, PercentBarGenerationStats, Task> onBarGeneratedAsync,
            Func<bool> checkPauseFunc,
            CancellationToken ct,
            int batchYieldBars = 1,
            int sleepIntervalMs = 0)
        {
            if (ticks == null || ticks.Length == 0 || _thresholdValue <= 0m) return;

            int count = ticks.Length;
            int yieldCounter = 0;

            for (int i = 0; i < count; i++)
            {
                if ((i & 0x7FF) == 0)
                {
                    if (ct.IsCancellationRequested) break;

                    while (checkPauseFunc() && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                }

                var tick = ticks[i];
                decimal p = tick.Price;
                if (p <= 0m) continue;

                if (_firstTickTime < 0) _firstTickTime = tick.Time;
                _totalTicks++;

                if (p > _globalMaxPrice) _globalMaxPrice = p;
                if (p < _globalMinPrice) _globalMinPrice = p;
                _totalVol += tick.Qty;
                _totalQuote += tick.QuoteQty;

                if (!_isBarBuilding)
                {
                    _isBarBuilding = true;
                    _open = p;
                    _high = p;
                    _low = p;
                    _close = p;
                    _openTime = tick.Time;
                    _closeTime = tick.Time;
                    _volume = tick.Qty;
                    _quoteVolume = tick.QuoteQty;
                    _tradeCount = 1;
                    _takerBuyVol = (!tick.IsBuyerMaker) ? tick.Qty : 0m;
                    _takerBuyQuote = (!tick.IsBuyerMaker) ? tick.QuoteQty : 0m;
                    _tickCount = 1;
                    continue;
                }

                if (p > _high) _high = p;
                if (p < _low) _low = p;
                _close = p;
                _closeTime = tick.Time;
                _volume += tick.Qty;
                _quoteVolume += tick.QuoteQty;
                _tradeCount++;
                _tickCount++;
                if (!tick.IsBuyerMaker)
                {
                    _takerBuyVol += tick.Qty;
                    _takerBuyQuote += tick.QuoteQty;
                }

                bool isTriggered = false;

                if (_sliceUnit == SliceUnitType.Percentage)
                {
                    switch (_mode)
                    {
                        case PercentBarMode.ChangeFromOpen:
                            decimal changePct = (p - _open) / _open;
                            if (changePct >= _thresholdFrac || changePct <= -_thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.HighLowRange:
                            if (_low > 0m && ((_high - _low) / _low) >= _thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.Renko:
                            decimal renkoChange = (p - _open) / _open;
                            if (renkoChange >= _thresholdFrac || renkoChange <= -_thresholdFrac)
                            {
                                isTriggered = true;
                            }
                            break;
                    }
                }
                else // SliceUnitType.FixedPrice
                {
                    switch (_mode)
                    {
                        case PercentBarMode.ChangeFromOpen:
                            decimal diff = p - _open;
                            if (diff >= _thresholdValue || diff <= -_thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.HighLowRange:
                            if ((_high - _low) >= _thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;

                        case PercentBarMode.Renko:
                            decimal renkoDiff = p - _open;
                            if (renkoDiff >= _thresholdValue || renkoDiff <= -_thresholdValue)
                            {
                                isTriggered = true;
                            }
                            break;
                    }
                }

                if (isTriggered)
                {
                    var completedBar = new PercentageKline
                    {
                        BarIndex = _barIndex++,
                        Open = _open,
                        High = _high,
                        Low = _low,
                        Close = _close,
                        OpenTime = _openTime,
                        CloseTime = _closeTime,
                        Volume = _volume,
                        QuoteVolume = _quoteVolume,
                        TradeCount = _tradeCount,
                        TakerBuyVolume = _takerBuyVol,
                        TakerBuyQuoteVolume = _takerBuyQuote,
                        TickCount = _tickCount
                    };

                    var dur = completedBar.Duration;
                    if (dur < _minDuration) _minDuration = dur;
                    if (dur > _maxDuration) _maxDuration = dur;
                    _totalDurationMs += (long)dur.TotalMilliseconds;

                    UpdateStats();

                    yieldCounter++;
                    if (yieldCounter >= batchYieldBars || sleepIntervalMs > 0)
                    {
                        yieldCounter = 0;
                        await onBarGeneratedAsync(completedBar, _totalTicks, Stats).ConfigureAwait(false);
                        if (sleepIntervalMs > 0)
                        {
                            await Task.Delay(sleepIntervalMs, ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await onBarGeneratedAsync(completedBar, _totalTicks, Stats).ConfigureAwait(false);
                    }

                    // 开启下一根 Bar
                    _open = p;
                    _high = p;
                    _low = p;
                    _close = p;
                    _openTime = tick.Time;
                    _closeTime = tick.Time;
                    _volume = 0m;
                    _quoteVolume = 0m;
                    _tradeCount = 0;
                    _takerBuyVol = 0m;
                    _takerBuyQuote = 0m;
                    _tickCount = 0;
                }
            }
        }

        /// <summary>
        /// 全量读取结束时，将最后正在构建的未满 Bar 封盘输出
        /// </summary>
        public PercentageKline? FlushLastBar()
        {
            if (!_isBarBuilding || _tickCount <= 0) return null;

            var lastBar = new PercentageKline
            {
                BarIndex = _barIndex++,
                Open = _open,
                High = _high,
                Low = _low,
                Close = _close,
                OpenTime = _openTime,
                CloseTime = _closeTime,
                Volume = _volume,
                QuoteVolume = _quoteVolume,
                TradeCount = _tradeCount,
                TakerBuyVolume = _takerBuyVol,
                TakerBuyQuoteVolume = _takerBuyQuote,
                TickCount = _tickCount
            };

            var dur = lastBar.Duration;
            if (dur < _minDuration) _minDuration = dur;
            if (dur > _maxDuration) _maxDuration = dur;
            _totalDurationMs += (long)dur.TotalMilliseconds;

            UpdateStats();
            _isBarBuilding = false;
            return lastBar;
        }

        private void UpdateStats()
        {
            Stats.TotalTicks = _totalTicks;
            Stats.TotalBars = _barIndex;
            Stats.MaxPrice = _globalMaxPrice == decimal.MinValue ? 0m : _globalMaxPrice;
            Stats.MinPrice = _globalMinPrice == decimal.MaxValue ? 0m : _globalMinPrice;
            Stats.TotalVolume = _totalVol;
            Stats.TotalQuoteVolume = _totalQuote;
            Stats.ElapsedMilliseconds = _sw.ElapsedMilliseconds;
            if (_closeTime >= _firstTickTime && _firstTickTime > 0)
            {
                Stats.TotalTimeSpan = TimeSpan.FromMilliseconds(Math.Max(0, _closeTime - _firstTickTime));
            }
            if (_barIndex > 0)
            {
                Stats.AverageBarDuration = TimeSpan.FromMilliseconds(_totalDurationMs / (double)_barIndex);
                Stats.MinBarDuration = _minDuration == TimeSpan.MaxValue ? TimeSpan.Zero : _minDuration;
                Stats.MaxBarDuration = _maxDuration == TimeSpan.MinValue ? TimeSpan.Zero : _maxDuration;
                Stats.AverageTicksPerBar = (double)_totalTicks / _barIndex;
            }
        }
    }
}
