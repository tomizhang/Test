using Common;
using Common.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    /// <summary>
    /// 连续同向 5 根 K 线 3 分钟观察期反转做单驱动引擎
    /// 核心逻辑：
    /// 1. 当大周期出现连续同向 K 线（连涨/连跌）且达到 5 根收盘时刻立即激活；
    /// 2. 接收后续 Tick 序列并按 3 分钟为观察窗口流式聚合小周期 K 线；
    /// 3. 进入第 1 个 3 分钟观察期进行反转做单判定，若未满足则自动循环推进至下个 3 分钟观察期；
    /// 4. 循环持续推进，直到大周期连续形态反转结束；
    /// 5. 命中反转信号时，输出做单信号并记录短黄线标记位置。
    /// </summary>
    public class ReversalOrderEngine
    {
        private readonly List<ReversalOrderSignal> _allSignals = new();
        private readonly List<ReversalObservationCycle> _allCycles = new();
        private readonly HashSet<string> _processedCycleKeys = new();
        private readonly HashSet<int> _activatedTrendIds = new();
        private readonly Dictionary<int, IReadOnlyList<RawTick>> _trendTickCache = new();

        public bool EnableReversalOrder { get; set; } = true;
        public int ObservationMinutes { get; set; } = 3;
        public decimal PLong { get; set; } = CandlestickPatternClassifier.DefaultPLong;
        public decimal PMedium { get; set; } = CandlestickPatternClassifier.DefaultPMedium;
        public decimal PShort { get; set; } = CandlestickPatternClassifier.DefaultPShort;
        /// <summary>
        /// Tick 流冲高滞涨回落 / 探底企稳回升触发阈值百分比 (默认 0.06%，即从波峰回落 0.06% 触发做空，从波谷回升 0.06% 触发做多)
        /// </summary>
        public decimal TickPullbackThresholdPct { get; set; } = 0.06m;

        /// <summary>
        /// 绿色通道顶部/底部观察阶段进入区间百分比阈值 (默认 25.0%，即上涨需达到通道上部 25% 或突破上轨进入观察阶段，下跌需达到通道下部 25% 或突破下轨进入观察阶段)
        /// </summary>
        public decimal ChannelZonePct { get; set; } = 25.0m;

        public IReadOnlyList<ReversalOrderSignal> AllSignals => _allSignals;
        public IReadOnlyList<ReversalObservationCycle> AllCycles => _allCycles;

        /// <summary>
        /// 策略激活事件 (连续同向第 5 根收盘时刻触发)
        /// </summary>
        public event Action<ConsecutiveTrendItem, string>? OnStrategyActivated;

        /// <summary>
        /// 反转做单信号触发事件 (信号对象, 附带描述文本)
        /// </summary>
        public event Action<ReversalOrderSignal, string>? OnReversalOrderSignal;

        /// <summary>
        /// 观察期推进或循环事件 (周期对象, 状态说明)
        /// </summary>
        public event Action<ReversalObservationCycle, string>? OnObservationCycleUpdated;

        /// <summary>
        /// 重置所有信号与观察期状态
        /// </summary>
        public void Reset()
        {
            _allSignals.Clear();
            _allCycles.Clear();
            _processedCycleKeys.Clear();
            _activatedTrendIds.Clear();
            _trendTickCache.Clear();
        }

        /// <summary>
        /// 时间轴回滚同步
        /// </summary>
        public void SyncTo(int targetBarIndex, IReadOnlyList<RawKline> allKlines)
        {
            if (targetBarIndex < 0 || allKlines == null || targetBarIndex >= allKlines.Count)
            {
                Reset();
                return;
            }

            long targetTime = allKlines[targetBarIndex].CloseTime;
            _allSignals.RemoveAll(s => s.BigBarIndex > targetBarIndex || s.TriggerTime > targetTime);
            _allCycles.RemoveAll(c => c.StartTime > targetTime);
            _processedCycleKeys.RemoveWhere(k =>
            {
                var parts = k.Split('_');
                if (parts.Length >= 2 && int.TryParse(parts[0], out int trendId) && int.TryParse(parts[1], out int cycleIdx))
                {
                    return !_allCycles.Any(c => c.TrendId == trendId && c.CycleIndex == cycleIdx);
                }
                return false;
            });

            // 移除在回滚位置之后才成立的趋势策略激活记录
            _activatedTrendIds.RemoveWhere(id =>
            {
                // trendId 格式为 1000000 + startIndex (上涨) 或 2000000 + startIndex (下跌)
                int startIdx = id >= 2000000 ? id - 2000000 : (id >= 1000000 ? id - 1000000 : id);
                int fifthBarIdx = startIdx + 4;
                return fifthBarIdx > targetBarIndex;
            });
        }

        /// <summary>
        /// 检查连续走势中首次达到绿色通道顶部部分或顶部(上涨)/底部部分或底部(下跌)的进入观察阶段 K 线
        /// </summary>
        public bool TryFindObservationEntry(
            ConsecutiveTrendItem trend,
            IReadOnlyList<RawKline> allKlines,
            int currentBarIndex,
            out int entryBarIndex,
            out long entryTime,
            out decimal entryPrice,
            out decimal channelUpper,
            out decimal channelLower,
            out decimal channelPosPct)
        {
            entryBarIndex = -1;
            entryTime = 0L;
            entryPrice = 0m;
            channelUpper = 0m;
            channelLower = 0m;
            channelPosPct = 0m;

            if (trend == null || allKlines == null) return false;

            int fifthBarIndex = trend.StartIndex + 4;
            if (currentBarIndex < fifthBarIndex || fifthBarIndex >= allKlines.Count) return false;

            // 确保具有平行通道参数 (若未拟合则即时拟合)
            if (!trend.HasChannel || trend.UpperIntercept <= trend.LowerIntercept)
            {
                ConsecutiveTrendDetector.FitParallelChannel(
                    allKlines,
                    trend.StartIndex,
                    trend.ConfirmedBarIndex,
                    out decimal sk, out decimal ub, out decimal lb);
                trend.SlopeK = sk;
                trend.UpperIntercept = ub;
                trend.LowerIntercept = lb;
                trend.HasChannel = ub > lb;
            }

            decimal zoneRatio = Math.Clamp(ChannelZonePct, 0m, 50m) / 100m;
            int maxBar = Math.Min(trend.EndIndex, currentBarIndex);

            // 从第 5 根开始向后寻找首次达到通道顶部部分(上涨)或底部部分(下跌)的 K 线
            for (int b = fifthBarIndex; b <= maxBar; b++)
            {
                var bar = allKlines[b];
                decimal upper = trend.GetUpperPrice(b);
                decimal lower = trend.GetLowerPrice(b);
                decimal height = upper - lower;

                if (height <= 0m)
                {
                    entryBarIndex = fifthBarIndex;
                    entryTime = allKlines[fifthBarIndex].CloseTime;
                    entryPrice = allKlines[fifthBarIndex].Close;
                    channelUpper = upper;
                    channelLower = lower;
                    channelPosPct = 100m;
                    return true;
                }

                if (trend.Type == ConsecutiveTrendType.Bullish)
                {
                    // 连续上涨：顶部部分起始价位 (默认上部 25% 区域，即 >= Lower + Height * 0.75)
                    decimal topZonePrice = lower + height * (1m - zoneRatio);
                    // 触及顶部部分或突破/触及上轨
                    if (bar.High >= topZonePrice || bar.Close >= topZonePrice)
                    {
                        entryBarIndex = b;
                        entryTime = bar.CloseTime;
                        entryPrice = bar.High;
                        channelUpper = upper;
                        channelLower = lower;
                        channelPosPct = Math.Round((bar.High - lower) / height * 100m, 1);
                        return true;
                    }
                }
                else if (trend.Type == ConsecutiveTrendType.Bearish)
                {
                    // 连续下跌：底部部分起始价位 (默认下部 25% 区域，即 <= Lower + Height * 0.25)
                    decimal bottomZonePrice = lower + height * zoneRatio;
                    // 触及底部部分或跌破/触及下轨
                    if (bar.Low <= bottomZonePrice || bar.Close <= bottomZonePrice)
                    {
                        entryBarIndex = b;
                        entryTime = bar.CloseTime;
                        entryPrice = bar.Low;
                        channelUpper = upper;
                        channelLower = lower;
                        channelPosPct = Math.Round((bar.Low - lower) / height * 100m, 1);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 检查并激活指定波段的反转做单策略 (当连续走势达到绿色通道顶部/底部时激活并正式进入观察阶段)
        /// </summary>
        public bool TryActivateStrategy(ConsecutiveTrendItem trend, IReadOnlyList<RawKline> allKlines, int currentBarIndex, out string activationMsg)
        {
            activationMsg = "";
            if (!EnableReversalOrder || trend == null || allKlines == null) return false;

            if (_activatedTrendIds.Contains(trend.Id)) return false;

            // 核心要求：如果是连续上涨，必须在绿色通道顶部部分或顶部进入观察阶段；如果是连续下跌，反之同理
            if (!TryFindObservationEntry(
                trend,
                allKlines,
                currentBarIndex,
                out int entryBarIndex,
                out long entryTime,
                out decimal entryPrice,
                out decimal channelUpper,
                out decimal channelLower,
                out decimal channelPosPct))
            {
                return false;
            }

            _activatedTrendIds.Add(trend.Id);
            trend.ObservationEntryBarIndex = entryBarIndex;
            trend.ObservationEntryPrice = entryPrice;
            trend.ObservationEntryTime = entryTime;

            string dirStr = trend.Type == ConsecutiveTrendType.Bullish
                ? $"🔥 连续上涨到达绿色通道顶部部分 (Bar #{entryBarIndex}, 触顶价:{entryPrice:F2}, 上轨:{channelUpper:F2}, 通道位:{channelPosPct:F1}%)"
                : $"❄️ 连续下跌到达绿色通道底部部分 (Bar #{entryBarIndex}, 探底价:{entryPrice:F2}, 下轨:{channelLower:F2}, 通道位:{channelPosPct:F1}%)";

            activationMsg = $"[策略激活] ⚡ {dirStr}，正式进入观察阶段！开启第1个{ObservationMinutes}分钟观察期";

            OnStrategyActivated?.Invoke(trend, activationMsg);
            return true;
        }

        /// <summary>
        /// 针对当前推进到的最新连续走势波段，执行 3 分钟观察期循环与反转形态识别
        /// 具备 Tick 优先聚合与 1m K 线双模自适应回退机制，确保在任何数据环境下均能准确输出策略信号与日志
        /// </summary>
        public List<ReversalOrderSignal> EvaluateObservationCycles(
            ConsecutiveTrendItem trend,
            IReadOnlyList<RawKline> allKlines,
            IReadOnlyList<RawTick>? ticks,
            int currentBarIndex,
            IReadOnlyList<RawKline>? fallback1mKlines = null)
        {
            var newSignals = new List<ReversalOrderSignal>();
            if (!EnableReversalOrder || trend == null || allKlines == null)
            {
                return newSignals;
            }

            // 【防重复触发/防递归保护】：若该连续形态已触发过反转做单信号，直接返回，不再重复评估
            if (_allSignals.Any(s => s.TrendId == trend.Id))
            {
                return newSignals;
            }

            int fifthBarIndex = trend.StartIndex + 4; // 第 5 根 K 线的索引 (从 0 计数时 +4 即为第 5 根)
            if (currentBarIndex < fifthBarIndex || fifthBarIndex >= allKlines.Count)
            {
                return newSignals;
            }

            // 核心要求：必须在达到绿色通道顶部部分/顶部 (上涨) 或 底部部分/底部 (下跌) 才正式进入观察阶段
            if (!TryFindObservationEntry(
                trend,
                allKlines,
                currentBarIndex,
                out int entryBarIndex,
                out long entryTime,
                out decimal obsEntryPrice,
                out decimal channelUpper,
                out decimal channelLower,
                out decimal channelPosPct))
            {
                // 未达到绿色通道极值区域，静默保持监控，不进入观察阶段
                return newSignals;
            }

            // 确保触发策略激活日志
            if (TryActivateStrategy(trend, allKlines, currentBarIndex, out var actMsg))
            {
                // 已在 TryActivateStrategy 内部触发 OnStrategyActivated
            }

            // 以达到通道极值进入观察阶段的 K 线收盘时刻作为第 1 个 3 分钟观察期的起点
            long t0 = entryTime;

            // 观察期持续推进的终点时间
            int trendEndBarIndex = Math.Min(trend.EndIndex, currentBarIndex);
            long trendEndTime = allKlines[trendEndBarIndex].CloseTime;

            // 优先检查并重用已读取的 Tick 缓存
            if ((ticks == null || ticks.Count == 0) && _trendTickCache.TryGetValue(trend.Id, out var cachedTicks))
            {
                ticks = cachedTicks;
            }

            long tickMaxTime = (ticks != null && ticks.Count > 0) ? ticks[ticks.Count - 1].Time : 0L;
            long currentPlayTime = allKlines[currentBarIndex].CloseTime;

            // 观察期推进的最大时间：当前播放时间与 Tick 最新时间的最大值
            long maxObservationTime = Math.Max(currentPlayTime, tickMaxTime);
            if (!trend.IsActive && trend.EndIndex < currentBarIndex)
            {
                // 若大周期连续走势已结束，观察期推进至大周期波段反转结束时刻
                long trendActualEnd = allKlines[trend.EndIndex].CloseTime;
                maxObservationTime = Math.Max(trendEndTime, Math.Min(maxObservationTime, trendActualEnd));
            }

            long cycleSpanMs = (long)ObservationMinutes * 60_000L;
            if (cycleSpanMs <= 0) cycleSpanMs = 180_000L;

            int cycleIndex = 1;
            long curCycleStart = t0;

            while (curCycleStart <= maxObservationTime)
            {
                // 若该连续形态已命中反转信号并做单，无需后续重复开单
                if (_allSignals.Any(s => s.TrendId == trend.Id))
                {
                    break;
                }

                long curCycleEnd = curCycleStart + cycleSpanMs - 1;
                string cycleKey = $"{trend.Id}_{cycleIndex}";

                // 若该观察期已处理完毕，直接跳过当前期，推进到下一观察期
                if (_processedCycleKeys.Contains(cycleKey))
                {
                    curCycleStart += cycleSpanMs;
                    cycleIndex++;
                    continue;
                }

                RawKline? smallBar = null;
                List<RawTick>? cycleTicks = null;
                List<RawKline>? subBars = null;

                // 1. 优先尝试提取当前观察期的逐笔 Tick 数据
                if (ticks != null && ticks.Count > 0)
                {
                    cycleTicks = new List<RawTick>();
                    for (int i = 0; i < ticks.Count; i++)
                    {
                        var t = ticks[i];
                        if (t.Time >= curCycleStart && t.Time <= curCycleEnd)
                        {
                            cycleTicks.Add(t);
                        }
                    }

                    if (cycleTicks.Count > 0)
                    {
                        var synthKlines = KlinePlaybackEngine.AggregateTicksToKlines(cycleTicks, ObservationMinutes, fillEmptyBars: false);
                        if (synthKlines.Count > 0)
                        {
                            smallBar = synthKlines[0];
                        }
                    }
                }

                // 2. 若当前无逐笔 Tick 数据，自动从 1m K 线数据源合并聚合为观察期 K 线
                if (cycleTicks == null || cycleTicks.Count == 0)
                {
                    var sourceBars = fallback1mKlines ?? allKlines;
                    subBars = new List<RawKline>();
                    for (int i = 0; i < sourceBars.Count; i++)
                    {
                        var b = sourceBars[i];
                        if (b.OpenTime >= curCycleStart && b.CloseTime <= curCycleEnd + 1000L)
                        {
                            subBars.Add(b);
                        }
                    }

                    if (subBars.Count > 0)
                    {
                        decimal o = subBars[0].Open;
                        decimal h = decimal.MinValue;
                        decimal l = decimal.MaxValue;
                        decimal c = subBars[subBars.Count - 1].Close;
                        decimal v = 0m;
                        decimal qv = 0m;
                        for (int sb = 0; sb < subBars.Count; sb++)
                        {
                            if (subBars[sb].High > h) h = subBars[sb].High;
                            if (subBars[sb].Low < l) l = subBars[sb].Low;
                            v += subBars[sb].Volume;
                            qv += subBars[sb].QuoteVolume;
                        }

                        smallBar = new RawKline
                        {
                            OpenTime = curCycleStart,
                            CloseTime = curCycleEnd,
                            Open = o,
                            High = h,
                            Low = l,
                            Close = c,
                            Volume = v,
                            QuoteVolume = qv
                        };
                    }
                }

                var cycleObj = _allCycles.FirstOrDefault(c => c.TrendId == trend.Id && c.CycleIndex == cycleIndex);
                if (cycleObj == null)
                {
                    cycleObj = new ReversalObservationCycle
                    {
                        TrendId = trend.Id,
                        PriorTrendType = trend.Type,
                        CycleIndex = cycleIndex,
                        StartTime = curCycleStart,
                        EndTime = curCycleEnd
                    };
                    _allCycles.Add(cycleObj);
                }

                DateTime cStartDt = TimeHelper.FromUnixTimeMilliseconds(curCycleStart).ToLocalTime();
                DateTime cEndDt = TimeHelper.FromUnixTimeMilliseconds(curCycleEnd).ToLocalTime();

                // =========================================================================
                // 核心流式判定分支 A：逐笔 Tick 流实时动态推演与触发 (零未来函数)
                // =========================================================================
                if (cycleTicks != null && cycleTicks.Count > 0)
                {
                    decimal runningOpen = cycleTicks[0].Price;
                    decimal runningHigh = cycleTicks[0].Price;
                    decimal runningLow = cycleTicks[0].Price;
                    decimal runningVol = 0m;
                    decimal runningQuoteVol = 0m;

                    decimal localPeak = cycleTicks[0].Price;
                    int localPeakIdx = 0;
                    long localPeakTime = cycleTicks[0].Time;

                    decimal localTrough = cycleTicks[0].Price;
                    int localTroughIdx = 0;
                    long localTroughTime = cycleTicks[0].Time;

                    decimal zoneRatio = Math.Clamp(ChannelZonePct, 0m, 50m) / 100m;
                    bool tickTriggered = false;

                    for (int ti = 0; ti < cycleTicks.Count; ti++)
                    {
                        var t = cycleTicks[ti];
                        if (t.Price > runningHigh) runningHigh = t.Price;
                        if (t.Price < runningLow) runningLow = t.Price;
                        runningVol += t.Qty;
                        runningQuoteVol += t.QuoteQty;

                        if (trend.Type == ConsecutiveTrendType.Bullish)
                        {
                            // 连涨后观察期做空监控
                            if (t.Price > localPeak)
                            {
                                localPeak = t.Price;
                                localPeakIdx = ti;
                                localPeakTime = t.Time;
                            }

                            int targetBigBarIndex = currentBarIndex;
                            for (int b = entryBarIndex; b < allKlines.Count; b++)
                            {
                                if (t.Time >= allKlines[b].OpenTime && t.Time <= allKlines[b].CloseTime)
                                {
                                    targetBigBarIndex = b;
                                    break;
                                }
                            }

                            // 验证价格是否位于绿色通道顶部部分或顶部 (或者突破上轨)
                            decimal curBarUpper = trend.GetUpperPrice(targetBigBarIndex);
                            decimal curBarLower = trend.GetLowerPrice(targetBigBarIndex);
                            decimal curBarHeight = curBarUpper - curBarLower;
                            decimal topZonePrice = curBarLower + curBarHeight * (1m - zoneRatio);

                            bool reachedHigh = localPeak >= topZonePrice || localPeak >= curBarUpper * 0.999m;

                            if (reachedHigh && ti > localPeakIdx)
                            {
                                decimal pullbackPct = (localPeak - t.Price) / localPeak * 100m;
                                decimal upperShadow = localPeak - Math.Max(runningOpen, t.Price);
                                decimal totalRange = localPeak - runningLow;
                                decimal shadowRatio = totalRange > 0 ? upperShadow / totalRange : 0m;

                                // 实时触发做空：微观从波峰回落 >= 阈值 (默认 0.06%)，且形成了上影线或翻阴
                                if (pullbackPct >= TickPullbackThresholdPct && (shadowRatio >= 0.25m || t.Price <= runningOpen))
                                {
                                    tickTriggered = true;

                                    var runningBar = new RawKline
                                    {
                                        OpenTime = curCycleStart,
                                        CloseTime = t.Time,
                                        Open = runningOpen,
                                        High = runningHigh,
                                        Low = runningLow,
                                        Close = t.Price,
                                        Volume = runningVol,
                                        QuoteVolume = runningQuoteVol
                                    };

                                    var pattern = CandlestickPatternClassifier.Classify(runningBar, PLong, PMedium, PShort);
                                    if (pattern.PatternType == CandlestickPatternType.None ||
                                        pattern.PatternType == CandlestickPatternType.FullBodyLongBullish ||
                                        pattern.PatternType == CandlestickPatternType.FullBodyMediumBullish)
                                    {
                                        pattern.PatternType = CandlestickPatternType.UpperShadowMediumBearish;
                                        pattern.PatternName = "Tick流冲高滞涨回落阴";
                                        pattern.SuggestedDirection = OrderSignalDirection.Sell;
                                    }

                                    decimal entryPrice = t.Price; // 实时触发瞬间的当前 Tick 价格
                                    long triggerTime = t.Time;
                                    decimal stopLoss = Math.Round(localPeak * 1.0015m, 2); // 防守止损精准锁定在波峰上方 0.15%
                                    decimal takeProfit = trend.StartPrice;

                                    var signal = new ReversalOrderSignal
                                    {
                                        TrendId = trend.Id,
                                        PriorTrendType = trend.Type,
                                        ObservationCycleIndex = cycleIndex,
                                        TriggerTime = triggerTime,
                                        Price = entryPrice,
                                        StopLossPrice = stopLoss,
                                        TakeProfitPrice = takeProfit,
                                        HighPointPrice = localPeak,
                                        LowPointPrice = runningLow,
                                        PeakTroughPrice = localPeak,
                                        PullbackPct = pullbackPct,
                                        IsTickStreamTriggered = true,
                                        Direction = OrderSignalDirection.Sell,
                                        Pattern = pattern,
                                        BigBarIndex = targetBigBarIndex,
                                        SmallKline = runningBar,
                                        ObservationStartTime = curCycleStart,
                                        ObservationEndTime = curCycleEnd
                                    };

                                    cycleObj.ResultKline = runningBar;
                                    cycleObj.PatternResult = pattern;
                                    cycleObj.IsCompleted = true;
                                    cycleObj.IsSignalTriggered = true;
                                    cycleObj.Signal = signal;
                                    _processedCycleKeys.Add(cycleKey);
                                    _allSignals.Add(signal);
                                    newSignals.Add(signal);

                                    string notify = $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 Tick流实时动态触发 🔴高点做空(Sell)！实时入场价: {signal.Price:F2} USDT (波峰: {localPeak:F2}, 滞涨回落: -{pullbackPct:F2}%, 防守止损: {stopLoss:F2}, 目标止盈: {takeProfit:F2}, 已标记短黄线)";
                                    OnReversalOrderSignal?.Invoke(signal, notify);
                                    break;
                                }
                            }
                        }
                        else if (trend.Type == ConsecutiveTrendType.Bearish)
                        {
                            // 连跌后观察期做多监控
                            if (t.Price < localTrough)
                            {
                                localTrough = t.Price;
                                localTroughIdx = ti;
                                localTroughTime = t.Time;
                            }

                            int targetBigBarIndex = currentBarIndex;
                            for (int b = entryBarIndex; b < allKlines.Count; b++)
                            {
                                if (t.Time >= allKlines[b].OpenTime && t.Time <= allKlines[b].CloseTime)
                                {
                                    targetBigBarIndex = b;
                                    break;
                                }
                            }

                            // 验证价格是否位于绿色通道底部部分或底部 (或者跌破下轨)
                            decimal curBarUpper = trend.GetUpperPrice(targetBigBarIndex);
                            decimal curBarLower = trend.GetLowerPrice(targetBigBarIndex);
                            decimal curBarHeight = curBarUpper - curBarLower;
                            decimal bottomZonePrice = curBarLower + curBarHeight * zoneRatio;

                            bool reachedLow = localTrough <= bottomZonePrice || localTrough <= curBarLower * 1.001m;

                            if (reachedLow && ti > localTroughIdx)
                            {
                                decimal bouncePct = (t.Price - localTrough) / localTrough * 100m;
                                decimal lowerShadow = Math.Min(runningOpen, t.Price) - localTrough;
                                decimal totalRange = runningHigh - localTrough;
                                decimal shadowRatio = totalRange > 0 ? lowerShadow / totalRange : 0m;

                                // 实时触发做多：微观从波谷反弹 >= 阈值 (默认 0.06%)，且形成了下影线或翻阳
                                if (bouncePct >= TickPullbackThresholdPct && (shadowRatio >= 0.25m || t.Price >= runningOpen))
                                {
                                    tickTriggered = true;

                                    var runningBar = new RawKline
                                    {
                                        OpenTime = curCycleStart,
                                        CloseTime = t.Time,
                                        Open = runningOpen,
                                        High = runningHigh,
                                        Low = runningLow,
                                        Close = t.Price,
                                        Volume = runningVol,
                                        QuoteVolume = runningQuoteVol
                                    };

                                    var pattern = CandlestickPatternClassifier.Classify(runningBar, PLong, PMedium, PShort);
                                    if (pattern.PatternType == CandlestickPatternType.None ||
                                        pattern.PatternType == CandlestickPatternType.FullBodyLongBearish ||
                                        pattern.PatternType == CandlestickPatternType.FullBodyMediumBearish)
                                    {
                                        pattern.PatternType = CandlestickPatternType.LowerShadowMediumBullish;
                                        pattern.PatternName = "Tick流探底企稳回升阳";
                                        pattern.SuggestedDirection = OrderSignalDirection.Buy;
                                    }

                                    decimal entryPrice = t.Price; // 实时触发瞬间的当前 Tick 价格
                                    long triggerTime = t.Time;
                                    decimal stopLoss = Math.Round(localTrough * 0.9985m, 2); // 防守止损精准锁定在波谷下方 0.15%
                                    decimal takeProfit = trend.StartPrice;

                                    var signal = new ReversalOrderSignal
                                    {
                                        TrendId = trend.Id,
                                        PriorTrendType = trend.Type,
                                        ObservationCycleIndex = cycleIndex,
                                        TriggerTime = triggerTime,
                                        Price = entryPrice,
                                        StopLossPrice = stopLoss,
                                        TakeProfitPrice = takeProfit,
                                        HighPointPrice = runningHigh,
                                        LowPointPrice = localTrough,
                                        PeakTroughPrice = localTrough,
                                        PullbackPct = bouncePct,
                                        IsTickStreamTriggered = true,
                                        Direction = OrderSignalDirection.Buy,
                                        Pattern = pattern,
                                        BigBarIndex = targetBigBarIndex,
                                        SmallKline = runningBar,
                                        ObservationStartTime = curCycleStart,
                                        ObservationEndTime = curCycleEnd
                                    };

                                    cycleObj.ResultKline = runningBar;
                                    cycleObj.PatternResult = pattern;
                                    cycleObj.IsCompleted = true;
                                    cycleObj.IsSignalTriggered = true;
                                    cycleObj.Signal = signal;
                                    _processedCycleKeys.Add(cycleKey);
                                    _allSignals.Add(signal);
                                    newSignals.Add(signal);

                                    string notify = $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 Tick流实时动态触发 🟢低点做多(Buy)！实时入场价: {signal.Price:F2} USDT (波谷: {localTrough:F2}, 企稳反弹: +{bouncePct:F2}%, 防守止损: {stopLoss:F2}, 目标止盈: {takeProfit:F2}, 已标记短黄线)";
                                    OnReversalOrderSignal?.Invoke(signal, notify);
                                    break;
                                }
                            }
                        }
                    }

                    if (tickTriggered)
                    {
                        // 满足实时反转做单条件，完成入场做单，退出观察循环
                        break;
                    }

                    // 若当前周期的 Tick 流已经走完但未触发反转做单
                    bool cycleFinished = curCycleEnd <= maxObservationTime || (cycleTicks.Count > 0 && cycleTicks[cycleTicks.Count - 1].Time >= curCycleEnd - 1000L);
                    if (cycleFinished)
                    {
                        cycleObj.IsCompleted = true;
                        if (smallBar.HasValue)
                        {
                            cycleObj.ResultKline = smallBar.Value;
                            cycleObj.PatternResult = CandlestickPatternClassifier.Classify(smallBar.Value, PLong, PMedium, PShort);
                        }
                        if (!_processedCycleKeys.Contains(cycleKey))
                        {
                            _processedCycleKeys.Add(cycleKey);
                            OnObservationCycleUpdated?.Invoke(cycleObj, $"[观察期推演] 第 {cycleIndex} 个 {ObservationMinutes}分钟 Tick流未满足滞涨/企稳反转，进入第 {cycleIndex + 1} 期...");
                        }
                        curCycleStart += cycleSpanMs;
                        cycleIndex++;
                        continue;
                    }
                    else
                    {
                        // 3 分钟尚未走完，静默等待后续 Tick 流推进
                        break;
                    }
                }

                // =========================================================================
                // 核心流式判定分支 B：1m K 线流自适应模拟推演 (无 Tick 数据时的回退方案)
                // =========================================================================
                if (smallBar.HasValue)
                {
                    var sb = smallBar.Value;
                    cycleObj.ResultKline = sb;
                    cycleObj.IsCompleted = true;

                    // 进行 18 种 K 线形态分类
                    var pattern = CandlestickPatternClassifier.Classify(sb, PLong, PMedium, PShort);
                    cycleObj.PatternResult = pattern;

                    // 判定是否符合前序趋势的反转做单信号
                    bool isReversal = CandlestickPatternClassifier.IsReversalOrderSignal(trend.Type, pattern, out var direction);

                    if (isReversal)
                    {
                        int targetBigBarIndex = currentBarIndex;
                        for (int b = entryBarIndex; b < allKlines.Count; b++)
                        {
                            if (sb.CloseTime >= allKlines[b].OpenTime && sb.CloseTime <= allKlines[b].CloseTime)
                            {
                                targetBigBarIndex = b;
                                break;
                            }
                        }

                        decimal curBarUpper = trend.GetUpperPrice(targetBigBarIndex);
                        decimal curBarLower = trend.GetLowerPrice(targetBigBarIndex);
                        decimal curBarHeight = curBarUpper - curBarLower;
                        decimal zoneRatio = Math.Clamp(ChannelZonePct, 0m, 50m) / 100m;

                        if (trend.Type == ConsecutiveTrendType.Bullish)
                        {
                            decimal topZonePrice = curBarLower + curBarHeight * (1m - zoneRatio);
                            if (sb.High < topZonePrice && sb.High < curBarUpper * 0.999m) isReversal = false;
                        }
                        else if (trend.Type == ConsecutiveTrendType.Bearish)
                        {
                            decimal bottomZonePrice = curBarLower + curBarHeight * zoneRatio;
                            if (sb.Low > bottomZonePrice && sb.Low > curBarLower * 1.001m) isReversal = false;
                        }
                    }

                    if (isReversal && !_processedCycleKeys.Contains(cycleKey))
                    {
                        cycleObj.IsSignalTriggered = true;
                        _processedCycleKeys.Add(cycleKey);

                        int targetBigBarIndex = currentBarIndex;
                        for (int b = entryBarIndex; b < allKlines.Count; b++)
                        {
                            if (sb.CloseTime >= allKlines[b].OpenTime && sb.CloseTime <= allKlines[b].CloseTime)
                            {
                                targetBigBarIndex = b;
                                break;
                            }
                        }

                        decimal entryPrice = direction == OrderSignalDirection.Sell ? sb.High : sb.Low;
                        long triggerTime = sb.CloseTime;
                        decimal stopLoss = direction == OrderSignalDirection.Sell
                            ? Math.Round(entryPrice * 1.0015m, 2)
                            : Math.Round(entryPrice * 0.9985m, 2);
                        decimal takeProfit = trend.StartPrice;

                        var signal = new ReversalOrderSignal
                        {
                            TrendId = trend.Id,
                            PriorTrendType = trend.Type,
                            ObservationCycleIndex = cycleIndex,
                            TriggerTime = triggerTime,
                            Price = entryPrice,
                            StopLossPrice = stopLoss,
                            TakeProfitPrice = takeProfit,
                            HighPointPrice = sb.High,
                            LowPointPrice = sb.Low,
                            PeakTroughPrice = direction == OrderSignalDirection.Sell ? sb.High : sb.Low,
                            PullbackPct = 0m,
                            IsTickStreamTriggered = false,
                            Direction = direction,
                            Pattern = pattern,
                            BigBarIndex = targetBigBarIndex,
                            SmallKline = sb,
                            ObservationStartTime = curCycleStart,
                            ObservationEndTime = curCycleEnd
                        };

                        cycleObj.Signal = signal;
                        _allSignals.Add(signal);
                        newSignals.Add(signal);

                        string dirText = direction == OrderSignalDirection.Sell ? "🔴高点做空(Sell)" : "🟢低点做多(Buy)";
                        string notify = direction == OrderSignalDirection.Sell
                            ? $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 1m回退触发 {dirText}！形态: [{pattern.PatternId}]{pattern.PatternName}, 入场价: {signal.Price:F2} USDT (防守止损: {stopLoss:F2}, 目标止盈: {takeProfit:F2}, 已标记短黄线)"
                            : $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 1m回退触发 {dirText}！形态: [{pattern.PatternId}]{pattern.PatternName}, 入场价: {signal.Price:F2} USDT (防守止损: {stopLoss:F2}, 目标止盈: {takeProfit:F2}, 已标记短黄线)";

                        OnReversalOrderSignal?.Invoke(signal, notify);
                        break;
                    }
                    else if (!isReversal && !_processedCycleKeys.Contains(cycleKey))
                    {
                        _processedCycleKeys.Add(cycleKey);
                        string reason = CandlestickPatternClassifier.IsReversalOrderSignal(trend.Type, pattern, out _)
                            ? "未达到高/低点验证区间"
                            : $"形态 [{pattern.PatternName}] 未满足反转";
                        OnObservationCycleUpdated?.Invoke(cycleObj, $"[观察期推演] 第 {cycleIndex} 期 {reason}，进入第 {cycleIndex + 1} 期...");
                    }
                }
                else
                {
                    // 当前周期尚未结束且无数据，静默等待后续数据流推进
                    break;
                }

                // 推进至下一个 3 分钟观察期循环
                curCycleStart += cycleSpanMs;
                cycleIndex++;
            }

            return newSignals;
        }

        /// <summary>
        /// 异步根据币种与时间区间全量读取真实 Tick 并推演观察期
        /// </summary>
        public async Task<List<ReversalOrderSignal>> ProcessTrendTicksAsync(
            string coin,
            ConsecutiveTrendItem trend,
            IReadOnlyList<RawKline> allKlines,
            int currentBarIndex,
            IReadOnlyList<RawKline>? fallback1mKlines = null,
            CancellationToken ct = default)
        {
            if (!EnableReversalOrder || trend == null || allKlines == null) return new List<ReversalOrderSignal>();

            int fifthBarIndex = trend.StartIndex + 4;
            if (currentBarIndex < fifthBarIndex || fifthBarIndex >= allKlines.Count) return new List<ReversalOrderSignal>();

            // 核心要求：检查是否满足进入观察阶段条件 (达到绿色通道顶部/底部)
            if (!TryFindObservationEntry(
                trend,
                allKlines,
                currentBarIndex,
                out int entryBarIndex,
                out long entryTime,
                out decimal obsEntryPrice,
                out decimal channelUpper,
                out decimal channelLower,
                out decimal channelPosPct))
            {
                return new List<ReversalOrderSignal>();
            }

            // 确保激活策略
            if (TryActivateStrategy(trend, allKlines, currentBarIndex, out var actMsg))
            {
                // 已触发 OnStrategyActivated
            }

            long t0 = entryTime;
            int trendEndBarIndex = Math.Min(trend.EndIndex, currentBarIndex);
            long tEnd = allKlines[trendEndBarIndex].CloseTime;

            // 至少读取覆盖第 1 个 3 分钟观察期
            long fetchEnd = Math.Max(tEnd, t0 + (long)ObservationMinutes * 60_000L);
            if (trend.EndIndex > currentBarIndex && trend.EndIndex < allKlines.Count)
            {
                fetchEnd = Math.Max(fetchEnd, allKlines[trend.EndIndex].CloseTime);
            }

            try
            {
                var ticks = await ParquetDataReader.ReadTicksForTimeRangeAsync(coin, t0, fetchEnd, ct).ConfigureAwait(true);
                if (ticks != null && ticks.Length > 0)
                {
                    _trendTickCache[trend.Id] = ticks;
                }
                return EvaluateObservationCycles(trend, allKlines, ticks, currentBarIndex, fallback1mKlines);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReversalOrderEngine] 读取 Tick 异常: {ex.Message}");
                return EvaluateObservationCycles(trend, allKlines, Array.Empty<RawTick>(), currentBarIndex, fallback1mKlines);
            }
        }
    }
}
