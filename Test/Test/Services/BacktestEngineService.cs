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
    /// 量化回测核心引擎服务实现类 (真·流式生产者-消费者流水线架构 + 触碰回弹开仓交易支持)
    /// </summary>
    public class BacktestEngineService : IBacktestEngineService
    {
        #region UI 实时事件委托

        public event Action<RawTick>? OnTickReceived;
        public event Action<RawKline, int, TrendLineStrategy>? OnKlineClosed;
        public event Action<TrendLine, RawTick, string>? OnTrendLinePenetrated;
        public event Action<TradeSignal>? OnTradeSignalGenerated;
        public event Action<Position>? OnPositionOpened;
        public event Action<TradeRecord>? OnTradeClosed;
        public event Action<string>? OnLogMessage;
        public event Action<BacktestProgress>? OnProgressChanged;

        #endregion

        #region 暂停/继续控制状态

        private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private volatile bool _isPaused = false;

        public bool IsPaused => _isPaused;

        public void Pause()
        {
            if (!_isPaused)
            {
                _isPaused = true;
                _pauseEvent.Reset();
                RaiseLog("[BacktestEngineService] ⏸ 回测已进入暂停状态。");
            }
        }

        public void Resume()
        {
            if (_isPaused)
            {
                _isPaused = false;
                _pauseEvent.Set();
                RaiseLog("[BacktestEngineService] ▶ 回测已恢复继续运行。");
            }
        }

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

            // 启动时确保处于非暂停运行状态
            _isPaused = false;
            _pauseEvent.Set();

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
                    AllowInternalPenetration = request.AllowInternalPenetration,
                    MinSignalLineX1X2 = request.MinSignalLineX1X2,
                    MinSignalLineAge = request.MinSignalLineAge,
                    MinSignalOverallSlopePct = request.MinSignalOverallSlopePct,
                    SignalCooldownSeconds = request.SignalCooldownSeconds,
                    TakeProfitPct = request.TakeProfitPct,
                    StopLossPct = request.StopLossPct,
                    EnableTrading = request.EnableTrading,
                    EnableTickStopLoss = request.EnableTickStopLoss,
                    MaxSlopePctPerBar = request.MaxSlopePctPerBar
                };

                // 转发策略内的穿透事件、开仓信号事件、持仓与平仓事件
                strategy.OnTrendLinePenetrated += (line, tick, reason) =>
                {
                    OnTrendLinePenetrated?.Invoke(line, tick, reason);
                };

                strategy.OnTradeSignalGenerated += signal =>
                {
                    OnTradeSignalGenerated?.Invoke(signal);
                };

                strategy.OnPositionOpened += pos =>
                {
                    OnPositionOpened?.Invoke(pos);
                };

                strategy.OnTradeClosed += trade =>
                {
                    OnTradeClosed?.Invoke(trade);
                };

                result.Strategy = strategy;

                // 2. 极速预加载全区间 K 线数据 (1个月4.3万根仅需约 50ms)
                RaiseLog($"[1/3] 正在快速加载 K 线序列...");
                ReportProgress(progress, 5.0, "正在加载 K 线序列...");

                int klineCount = await dataReader.LoadKlineRangeAsync(
                    request.Coin,
                    request.StartDate,
                    request.EndDate,
                    request.Interval,
                    ct).ConfigureAwait(false);

                result.TotalKlines = klineCount;
                RaiseLog($"-> K 线序列加载就绪: 共 {klineCount:N0} 根 (耗时: {sw.ElapsedMilliseconds} ms)");

                // 3. 启动 Tick 后台流式 3 线程滑动窗口生产者任务 (边读边测，内存恒定)
                RaiseLog($"[2/3] 启动 Tick 3 线程流式预取引擎 (生产者-消费者全速并发流水线)...");
                ReportProgress(progress, 10.0, "启动流式预取引擎...");

                var tickProducerTask = Task.Run(() => dataReader.StartTickStreamingAsync(
                    request.Coin,
                    request.StartDate,
                    request.EndDate,
                    parallelDays: request.ParallelDays,
                    maxBufferedDays: 3,
                    ct: ct), ct);

                // 极速等待首批缓冲到位 (0.2s 内即刻启动回测)
                while (!dataReader.IsTickStreamingCompleted && dataReader.TickQueueCount < 10000 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }

                // 4. 模拟真实交易时钟驱动事件循环 (消费者循环)
                int currentKlineIndex = 0;
                long currentTickIndex = 0;
                RawKline? currentKline = null;
                decimal lastPushedTickPrice = decimal.MinValue;

                if (dataReader.TryDequeueKline(out RawKline firstKline))
                {
                    currentKline = firstKline;
                    currentKlineIndex++;
                }

                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        RaiseLog("[BacktestEngineService] 用户取消了回测任务。");
                        result.Success = false;
                        result.ErrorMessage = "任务被用户取消";
                        return result;
                    }

                    // ⏸ 暂停检测与阻塞等待 (如果用户点击暂停，等待继续或取消)
                    if (!_pauseEvent.IsSet)
                    {
                        try
                        {
                            _pauseEvent.Wait(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            RaiseLog("[BacktestEngineService] 用户在暂停状态下取消了回测任务。");
                            result.Success = false;
                            result.ErrorMessage = "任务被用户取消";
                            return result;
                        }
                    }

                    // 从并发队列尝试取出 Tick
                    if (!dataReader.TryDequeueTick(out RawTick tick))
                    {
                        // 队列暂空，但生产者仍在后台读取后续天数数据，让步等待
                        if (!dataReader.IsTickStreamingCompleted)
                        {
                            await Task.Delay(5, ct).ConfigureAwait(false);
                            continue;
                        }
                        else
                        {
                            // 生产者已完成且队列全部消费完毕，跳出主循环
                            break;
                        }
                    }

                    // 若当前 Tick 的时间戳超出当前 K 线的收盘时间，触发 K 线周期收盘事件！
                    while (currentKline.HasValue && tick.Time > currentKline.Value.CloseTime)
                    {
                        // 1. 推送已完结周期的 K 线到策略
                        strategy.OnKline(currentKline.Value);

                        // 2. 触发外部事件回调
                        OnKlineClosed?.Invoke(currentKline.Value, currentKlineIndex, strategy);

                        // 3. 跨周期收盘后重置价格过滤缓存
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

                    // 推送价格变动的新 Tick 到策略 (触发触碰检测与 3-Tick 回弹开仓判定)
                    strategy.OnTick(tick);
                    currentTickIndex++;

                    // 触发逐笔 Tick 外部回调
                    OnTickReceived?.Invoke(tick);

                    // 进度周期汇报 (携带实时胜率、完成交易笔数与累计收益率)
                    if (currentTickIndex % 50000 == 0)
                    {
                        double percent = klineCount > 0
                            ? 10.0 + ((double)currentKlineIndex / klineCount) * 80.0
                            : 50.0;

                        string sign = strategy.TotalPnLPct >= 0 ? "+" : "";
                        ReportProgress(progress, percent, $"正在流式回测 ({currentKlineIndex:N0}/{klineCount:N0} 根K线, 战绩: {strategy.CompletedTrades.Count}笔 胜率:{strategy.WinRate:F1}% 盈亏:{sign}{strategy.TotalPnLPct:F2}%)...",
                            processedKlines: currentKlineIndex,
                            totalKlines: klineCount,
                            processedTicks: currentTickIndex,
                            totalTicks: dataReader.TotalTicksLoaded,
                            activeR: strategy.ActiveResistanceLines.Count,
                            activeS: strategy.ActiveSupportLines.Count,
                            activeChannels: strategy.ActiveTrendChannelsCount,
                            deletedCount: strategy.DeletedTrendLinesCount,
                            longSignals: strategy.LongSignalsCount,
                            shortSignals: strategy.ShortSignalsCount,
                            completedTrades: strategy.CompletedTrades.Count,
                            winningTrades: strategy.WinningTradesCount,
                            losingTrades: strategy.LosingTradesCount,
                            totalPnL: strategy.TotalPnLPct);
                    }
                }

                // 等待生产者任务彻底收尾
                await tickProducerTask.ConfigureAwait(false);

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

                // 平仓所有未完结持仓
                if (strategy.ActivePositions.Count > 0)
                {
                    decimal exitPrice = lastPushedTickPrice > decimal.MinValue ? lastPushedTickPrice : (strategy.LatestKline.Close > 0 ? strategy.LatestKline.Close : 0);
                    strategy.CloseAllRemainingPositions(exitPrice, Environment.TickCount64);
                }

                sw.Stop();
                result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                result.TotalTicks = currentTickIndex;

                // 5. 统计结果装填
                result.PeaksCount = strategy.Peaks.Count;
                result.ValleysCount = strategy.Valleys.Count;
                result.ActiveResistanceLinesCount = strategy.ActiveResistanceLines.Count;
                result.ActiveSupportLinesCount = strategy.ActiveSupportLines.Count;
                result.ActiveChannelsCount = strategy.ActiveTrendChannelsCount;
                result.DeletedTrendLinesCount = strategy.DeletedTrendLinesCount;
                result.HistoricalTrendLinesCount = strategy.HistoricalTrendLinesCount;
                result.LongSignalsCount = strategy.LongSignalsCount;
                result.ShortSignalsCount = strategy.ShortSignalsCount;
                result.CompletedTrades = new List<TradeRecord>(strategy.CompletedTrades);
                result.WinningTradesCount = strategy.WinningTradesCount;
                result.LosingTradesCount = strategy.LosingTradesCount;
                result.TotalPnLPct = strategy.TotalPnLPct;
                result.WinRate = strategy.WinRate;
                result.StrategySummary = strategy.GetStrategySummary();

                RaiseLog($"[完成] 回测执行完毕！总耗时: {sw.ElapsedMilliseconds} ms, 实际吞吐: {result.TicksPerSecond:N0} ticks/s");
                RaiseLog($"-> 交易统计: 完成={result.TotalTrades} 笔, 胜率={result.WinRate:F1}%, 累计收益率={result.TotalPnLPct:F2}% (止盈: +{request.TakeProfitPct:F1}%, 止损: -{request.StopLossPct:F1}%)");
                RaiseLog($"-> 信号统计: 多单(Long)={result.LongSignalsCount} 笔 | 空单(Short)={result.ShortSignalsCount} 笔 (总计 {result.TotalSignalsCount} 笔)");
                RaiseLog($"-> 策略最终状态: {result.StrategySummary}");

                // 6. 渲染生成 ScottPlot 分析图表
                if (request.GenerateChart && strategy.KlineCount > 0)
                {
                    RaiseLog($"[3/4] 正在生成 ScottPlot 专业分析图表...");
                    ReportProgress(progress, 90.0, "正在生成分析图表...");

                    string summaryText = request.Description ??
                        $"币种: {request.Coin}, 周期: {request.Interval.ToIntervalString()}, 时间窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}\n" +
                        $"交易胜率: {result.WinRate:F1}% ({result.WinningTradesCount}胜/{result.LosingTradesCount}负) | 收益: {result.TotalPnLPct:F2}%\n" +
                        $"策略参数: 触碰 3-Tick 回弹开仓 | 止盈: +{request.TakeProfitPct:F1}% | 止损: -{request.StopLossPct:F1}% | 信号: 多 {strategy.LongSignalsCount} | 空 {strategy.ShortSignalsCount}";

                    result.ChartPath = strategy.PlotChart(
                        summaryText,
                        request.ChartOutputPath,
                        request.ChartWidth,
                        request.ChartHeight,
                        lineWidth: (float)request.LineWidth);

                    RaiseLog($"-> 图表已成功保存至: {result.ChartPath}");
                }

                // 7. 生成独立交互式 HTML 回测报告
                if (request.GenerateHtmlReport)
                {
                    RaiseLog($"[4/4] 正在生成独立交互式 HTML 回测报告...");
                    ReportProgress(progress, 95.0, "正在生成 HTML 回测报告...");
                    string htmlPath = HtmlReportHelper.GenerateReport(result, request, request.ReportOutputPath);
                    result.ReportHtmlPath = htmlPath;
                    RaiseLog($"-> HTML 分析报告已生成落盘: {htmlPath}");
                }

                ReportProgress(progress, 100.0, "回测全部完成！",
                    processedKlines: currentKlineIndex,
                    totalKlines: klineCount,
                    processedTicks: currentTickIndex,
                    totalTicks: currentTickIndex,
                    activeR: strategy.ActiveResistanceLines.Count,
                    activeS: strategy.ActiveSupportLines.Count,
                    activeChannels: strategy.ActiveTrendChannelsCount,
                    deletedCount: strategy.DeletedTrendLinesCount,
                    longSignals: strategy.LongSignalsCount,
                    shortSignals: strategy.ShortSignalsCount,
                    completedTrades: strategy.CompletedTrades.Count,
                    winningTrades: strategy.WinningTradesCount,
                    losingTrades: strategy.LosingTradesCount,
                    totalPnL: strategy.TotalPnLPct);

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
            finally
            {
                _isPaused = false;
                _pauseEvent.Set();
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
            int activeChannels = 0,
            int deletedCount = 0,
            int longSignals = 0,
            int shortSignals = 0,
            int completedTrades = 0,
            int winningTrades = 0,
            int losingTrades = 0,
            decimal totalPnL = 0m)
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
                ActiveChannelsCount = activeChannels,
                DeletedLinesCount = deletedCount,
                LongSignalsCount = longSignals,
                ShortSignalsCount = shortSignals,
                CompletedTradesCount = completedTrades,
                WinningTradesCount = winningTrades,
                LosingTradesCount = losingTrades,
                CurrentTotalPnLPct = totalPnL
            };

            progress?.Report(p);
            OnProgressChanged?.Invoke(p);
        }

        #endregion
    }
}
