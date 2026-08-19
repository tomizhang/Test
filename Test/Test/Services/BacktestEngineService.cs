using Common.Helper;
using Common.Models;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Test.Strategy;

namespace Common.Services
{
    /// <summary>
    /// 量化回测核心引擎服务实现类
    /// 核心职责：
    /// 1. 协调 ParquetDataReader 数据流式预取与 TrendLineStrategy 增量事件流
    /// 2. 实现模拟真实交易状态的时钟驱动事件派发 (Tick周期内，跨周期K线收盘)
    /// 3. 支持 WinForms / WPF 依赖注入、实时进度通知与高性能事件回调
    /// 4. 自动触发 ScottPlot 图表渲染与落地
    /// </summary>
    public class BacktestEngineService : IBacktestEngineService
    {
        #region UI 实时事件委托

        public event Action<RawTick>? OnTickReceived;
        public event Action<RawKline, int, TrendLineStrategy>? OnKlineClosed;
        public event Action<TrendLine, RawTick, string>? OnTrendLinePenetrated;
        public event Action<string>? OnLogMessage;
        public event Action<BacktestProgress>? OnProgressChanged;

        #endregion

        /// <summary>
        /// 异步启动全流程量化回测执行
        /// </summary>
        public async Task<BacktestResult> RunBacktestAsync(
            BacktestRequest request,
            IProgress<BacktestProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var result = new BacktestResult
            {
                Coin = request.Coin,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Interval = request.Interval
            };

            var sw = Stopwatch.StartNew();

            try
            {
                RaiseLog($"[BacktestEngineService] 开始初始化回测: {request.Coin} ({request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}, 周期: {request.Interval.ToIntervalString()})");

                // 1. 初始化数据读取器与策略实例
                using var dataReader = new ParquetDataReader();
                var strategy = new TrendLineStrategy(
                    request.Coin,
                    request.Interval,
                    maxKlines: request.MaxKlinesCapacity,
                    minTrendLines: request.MinTrendLinesCapacity,
                    maxDeletedLines: request.MaxDeletedTrendLinesCapacity)
                {
                    LeftLen = request.LeftLen,
                    RightLen = request.RightLen,
                    MaxSpan = request.MaxSpan,
                    AllowInternalPenetration = request.AllowInternalPenetration
                };

                // 转发策略内的穿透事件
                strategy.OnTrendLinePenetrated += (line, tick, reason) =>
                {
                    OnTrendLinePenetrated?.Invoke(line, tick, reason);
                };

                result.Strategy = strategy;

                // 2. 异步并行加载指定日期区间内的全部 K 线与 Tick 数据 (通过 DuckDB 原生文件下推 + 3 线程滑动窗口)
                RaiseLog($"[1/3] 正在通过 DuckDB 文件下推与 3 线程滑动窗口预取数据...");
                ReportProgress(progress, 10.0, "正在加载数据文件...");

                var (klineCount, tickCount) = await dataReader.LoadDateRangeAsync(
                    request.Coin,
                    request.StartDate,
                    request.EndDate,
                    request.Interval,
                    parallelDays: request.ParallelDays,
                    ct: ct).ConfigureAwait(false);

                result.TotalKlines = klineCount;
                result.TotalTicks = tickCount;

                RaiseLog($"-> 数据加载完成: K线 {klineCount:N0} 根, Tick {tickCount:N0} 条 (读取耗时: {sw.ElapsedMilliseconds} ms)");
                ReportProgress(progress, 30.0, $"数据加载完成 (K线: {klineCount:N0}, Tick: {tickCount:N0})", totalKlines: klineCount, totalTicks: tickCount);

                // 3. 模拟真实交易时钟驱动事件循环
                RaiseLog($"[2/3] 开始执行真实交易时钟驱动事件流推送...");

                int currentKlineIndex = 0;
                long currentTickIndex = 0;
                RawKline? currentKline = null;

                if (dataReader.TryDequeueKline(out RawKline firstKline))
                {
                    currentKline = firstKline;
                    currentKlineIndex++;
                }

                // 进度节流计数器与价格去重缓存
                long progressInterval = Math.Max(10000, tickCount / 100);
                decimal lastPushedTickPrice = decimal.MinValue;

                while (dataReader.TryDequeueTick(out RawTick tick))
                {
                    if (ct.IsCancellationRequested)
                    {
                        RaiseLog("[BacktestEngineService] 用户取消了回测任务。");
                        result.Success = false;
                        result.ErrorMessage = "任务被用户取消";
                        return result;
                    }

                    // 若当前 Tick 的时间戳超出当前 K 线的收盘时间，触发 K 线周期收盘事件！
                    while (currentKline.HasValue && tick.Time > currentKline.Value.CloseTime)
                    {
                        // 1. 推送已完结周期的 K 线到策略
                        strategy.OnKline(currentKline.Value);

                        // 2. 触发外部事件回调
                        OnKlineClosed?.Invoke(currentKline.Value, currentKlineIndex, strategy);

                        // 3. 跨周期收盘后重置价格过滤缓存，确保新周期的首个 Tick 会重新代入更新后的趋势线方程
                        lastPushedTickPrice = decimal.MinValue;

                        // 4. 推进到下一个 K 线周期
                        if (dataReader.TryDequeueKline(out RawKline nextKline))
                        {
                            currentKline = nextKline;
                            currentKlineIndex++;
                        }
                        else
                        {
                            currentKline = null;
                        }
                    }

                    // ⚡ 价格去重优化：如果价格没有发生变动，则无需推送到策略与外部事件
                    if (tick.Price == lastPushedTickPrice)
                    {
                        continue;
                    }

                    lastPushedTickPrice = tick.Price;

                    // 推送价格变动的新 Tick 到策略
                    strategy.OnTick(tick);
                    currentTickIndex++;

                    // 触发逐笔 Tick 外部回调
                    OnTickReceived?.Invoke(tick);

                    // 进度周期汇报
                    if (currentTickIndex % progressInterval == 0 && tickCount > 0)
                    {
                        double percent = 30.0 + ((double)currentTickIndex / tickCount) * 60.0;
                        ReportProgress(progress, percent, $"正在回测事件流 ({percent:F1}%)...",
                            processedKlines: currentKlineIndex,
                            totalKlines: klineCount,
                            processedTicks: currentTickIndex,
                            totalTicks: tickCount,
                            activeR: strategy.ActiveResistanceLines.Count,
                            activeS: strategy.ActiveSupportLines.Count,
                            deletedCount: strategy.DeletedTrendLinesCount);
                    }
                }

                // 所有 Tick 消费完毕后，收盘处理剩余 K 线
                while (currentKline.HasValue)
                {
                    strategy.OnKline(currentKline.Value);
                    OnKlineClosed?.Invoke(currentKline.Value, currentKlineIndex, strategy);

                    if (dataReader.TryDequeueKline(out RawKline nextKline))
                    {
                        currentKline = nextKline;
                        currentKlineIndex++;
                    }
                    else
                    {
                        currentKline = null;
                    }
                }

                sw.Stop();
                result.ElapsedMilliseconds = sw.ElapsedMilliseconds;

                // 4. 统计结果装填
                result.PeaksCount = strategy.Peaks.Count;
                result.ValleysCount = strategy.Valleys.Count;
                result.ActiveResistanceLinesCount = strategy.ActiveResistanceLines.Count;
                result.ActiveSupportLinesCount = strategy.ActiveSupportLines.Count;
                result.DeletedTrendLinesCount = strategy.DeletedTrendLinesCount;
                result.HistoricalTrendLinesCount = strategy.HistoricalTrendLinesCount;
                result.StrategySummary = strategy.GetStrategySummary();

                RaiseLog($"[完成] 回测执行完毕！总耗时: {sw.ElapsedMilliseconds} ms, 平均吞吐: {result.TicksPerSecond:N0} ticks/s");
                RaiseLog($"-> 策略最终状态: {result.StrategySummary}");

                // 5. 渲染生成 ScottPlot 分析图表
                if (request.GenerateChart && strategy.KlineCount > 0)
                {
                    RaiseLog($"[3/3] 正在生成 ScottPlot 专业分析图表...");
                    ReportProgress(progress, 95.0, "正在生成分析图表...");

                    string summaryText = request.Description ??
                        $"币种: {request.Coin}, 周期: {request.Interval.ToIntervalString()}, 时间窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}\n" +
                        $"策略模式: 三层增量计算流水线 + OnTick 实时穿透删除 (已删除: {strategy.DeletedTrendLinesCount}条)";

                    result.ChartPath = strategy.PlotChart(
                        summaryText,
                        request.ChartOutputPath,
                        request.ChartWidth,
                        request.ChartHeight);

                    RaiseLog($"-> 图表已成功保存至: {result.ChartPath}");
                }

                ReportProgress(progress, 100.0, "回测全部完成！",
                    processedKlines: currentKlineIndex,
                    totalKlines: klineCount,
                    processedTicks: currentTickIndex,
                    totalTicks: tickCount,
                    activeR: strategy.ActiveResistanceLines.Count,
                    activeS: strategy.ActiveSupportLines.Count,
                    deletedCount: strategy.DeletedTrendLinesCount);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                RaiseLog($"[BacktestEngineService] 发生未处理异常: {ex}");
                return result;
            }
        }

        #region 内部辅助方法

        private void RaiseLog(string message)
        {
            Logger.Log(message);
            OnLogMessage?.Invoke(message);
        }

        private void ReportProgress(
            IProgress<BacktestProgress>? progress,
            double percentage,
            string message,
            int processedKlines = 0,
            int totalKlines = 0,
            long processedTicks = 0,
            long totalTicks = 0,
            int activeR = 0,
            int activeS = 0,
            int deletedCount = 0)
        {
            var p = new BacktestProgress
            {
                Percentage = Math.Clamp(percentage, 0.0, 100.0),
                Message = message,
                ProcessedKlines = processedKlines,
                TotalKlines = totalKlines,
                ProcessedTicks = processedTicks,
                TotalTicks = totalTicks,
                ActiveResistanceCount = activeR,
                ActiveSupportCount = activeS,
                DeletedLinesCount = deletedCount
            };

            progress?.Report(p);
            OnProgressChanged?.Invoke(p);
        }

        #endregion
    }
}
