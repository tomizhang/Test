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
        private readonly HashSet<int> _loggedTerminatedTrendIds = new();
        private readonly HashSet<int> _loggedUnenteredTrendIds = new();
        private readonly Dictionary<int, int> _trendEndIndexMap = new();
        private readonly Dictionary<int, IReadOnlyList<RawTick>> _trendTickCache = new();

        public bool EnableReversalOrder { get; set; } = true;
        public int ObservationMinutes { get; set; } = 3;
        public const int MaxObservationCycles = 15;
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

        /// <summary>
        /// 到达百分比观察线附近的容差比例 (占通道高度百分比，默认 8.0%，即距离百分比线 8% 通道高度以内均视为到达线附近)
        /// </summary>
        public decimal NearLineTolerancePct { get; set; } = 8.0m;

        /// <summary>
        /// 到达线附近后确认相对高点/低点所需的最小后续 Tick 笔数 (默认 2 笔)
        /// </summary>
        public int MinTicksAfterNearLine { get; set; } = 2;

        /// <summary>
        /// 做空单时在 Tick 级别形成高点后确保高点已经形成所需的最小后续 Tick 笔数 (默认 5 笔)
        /// </summary>
        public int MinTicksAfterPeakForSell { get; set; } = 5;

        /// <summary>
        /// 观察期开始时向前回溯计算高低点的 Tick 数量 (默认 1000 笔)
        /// </summary>
        public int LookbackTickCount { get; set; } = 1000;

        public IReadOnlyList<ReversalOrderSignal> AllSignals => _allSignals;
        public IReadOnlyList<ReversalObservationCycle> AllCycles => _allCycles;

        /// <summary>
        /// 查询指定趋势波段在截至 upToBarIndex 之前是否已触发产生做单交易信号
        /// </summary>
        public bool HasTradingSignal(int trendId, int upToBarIndex)
        {
            for (int i = 0; i < _allSignals.Count; i++)
            {
                var s = _allSignals[i];
                if (s.TrendId == trendId && s.BigBarIndex <= upToBarIndex)
                {
                    return true;
                }
            }
            return false;
        }

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
        public event Action<ReversalObservationCycle?, string>? OnObservationCycleUpdated;

        /// <summary>
        /// 重置所有信号与观察期状态
        /// </summary>
        public void Reset()
        {
            _allSignals.Clear();
            _allCycles.Clear();
            _processedCycleKeys.Clear();
            _activatedTrendIds.Clear();
            _loggedTerminatedTrendIds.Clear();
            _loggedUnenteredTrendIds.Clear();
            _trendEndIndexMap.Clear();
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

            // 移除在回滚位置之后才终结的波段失效/未激活日志标记
            _loggedTerminatedTrendIds.RemoveWhere(id =>
            {
                if (_trendEndIndexMap.TryGetValue(id, out int endIdx))
                {
                    return targetBarIndex <= endIdx;
                }
                return true;
            });
            _loggedUnenteredTrendIds.RemoveWhere(id =>
            {
                if (_trendEndIndexMap.TryGetValue(id, out int endIdx))
                {
                    return targetBarIndex <= endIdx;
                }
                return true;
            });
        }

        /// <summary>
        /// 确保平行通道参数已计算拟合
        /// </summary>
        public static void EnsureChannelFitted(ConsecutiveTrendItem trend, IReadOnlyList<RawKline> allKlines)
        {
            if (trend == null || allKlines == null) return;
            if (!trend.HasChannel || trend.UpperIntercept <= trend.LowerIntercept)
            {
                ConsecutiveTrendDetector.FitParallelChannel(
                    allKlines,
                    trend.StartIndex,
                    trend.ConfirmedBarIndex,
                    out decimal sk, out decimal ub, out decimal lb,
                    trend.PriceMode);
                trend.SlopeK = sk;
                trend.UpperIntercept = ub;
                trend.LowerIntercept = lb;
                trend.HasChannel = ub > lb;
            }
        }

        /// <summary>
        /// 检查连续走势中历史 K 线实体部分是否曾超过通道 (上涨实体突破上轨 / 下跌实体跌破下轨)
        /// 实体上界: Math.Max(bar.Open, bar.Close)
        /// 实体下界: Math.Min(bar.Open, bar.Close)
        /// </summary>
        public bool CheckBodyExceededChannel(
            ConsecutiveTrendItem trend,
            IReadOnlyList<RawKline> allKlines,
            int currentBarIndex,
            out int breakoutBarIndex)
        {
            breakoutBarIndex = -1;
            if (trend == null || allKlines == null) return false;

            // 保持回放时序完整性：若已记录的突破 Bar 索引位于当前播放帧之后或早于新通道起始点，重置状态
            int startCheckBar = trend.ChannelStartIndex > 0 ? trend.ChannelStartIndex : trend.StartIndex;
            if (trend.BreakoutBarIndex > currentBarIndex || trend.BreakoutBarIndex < startCheckBar)
            {
                trend.HasBodyExceededChannel = false;
                trend.BreakoutBarIndex = -1;
            }

            if (trend.HasBodyExceededChannel && trend.BreakoutBarIndex >= startCheckBar && trend.BreakoutBarIndex <= currentBarIndex)
            {
                breakoutBarIndex = trend.BreakoutBarIndex;
                return true;
            }

            EnsureChannelFitted(trend, allKlines);
            if (!trend.HasChannel || trend.UpperIntercept <= trend.LowerIntercept)
            {
                return false;
            }

            int maxBar = Math.Min(trend.EndIndex, currentBarIndex);
            for (int b = startCheckBar; b <= maxBar && b < allKlines.Count; b++)
            {
                var bar = allKlines[b];
                decimal upper = trend.GetUpperPrice(b);
                decimal lower = trend.GetLowerPrice(b);

                if (trend.Type == ConsecutiveTrendType.Bullish)
                {
                    decimal bodyTop = Math.Max(bar.Open, bar.Close);
                    if (bodyTop > upper)
                    {
                        breakoutBarIndex = b;
                        trend.HasBodyExceededChannel = true;
                        trend.BreakoutBarIndex = b;
                        return true;
                    }
                }
                else if (trend.Type == ConsecutiveTrendType.Bearish)
                {
                    decimal bodyBottom = Math.Min(bar.Open, bar.Close);
                    if (bodyBottom < lower)
                    {
                        breakoutBarIndex = b;
                        trend.HasBodyExceededChannel = true;
                        trend.BreakoutBarIndex = b;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 检查连续走势在历史中 (从 StartIndex/ChannelStartIndex 到 currentBarIndex) 是否曾有高点达到通道顶部(上涨)或低点达到通道底部(下跌)
        /// </summary>
        public bool CheckHistoricalExtremeReached(
            ConsecutiveTrendItem trend,
            IReadOnlyList<RawKline> allKlines,
            int currentBarIndex,
            out int extremeBarIndex)
        {
            extremeBarIndex = -1;
            if (trend == null || allKlines == null) return false;

            int startCheckBar = trend.StartIndex;
            // 保持回放时序完整性：若已记录的极值 Bar 索引位于当前播放帧之后，重置状态
            if (trend.HistoricalExtremeBarIndex > currentBarIndex || trend.HistoricalExtremeBarIndex < startCheckBar)
            {
                trend.HasHistoricalReachedExtreme = false;
                trend.HistoricalExtremeBarIndex = -1;
            }

            if (trend.HasHistoricalReachedExtreme && trend.HistoricalExtremeBarIndex >= startCheckBar && trend.HistoricalExtremeBarIndex <= currentBarIndex)
            {
                extremeBarIndex = trend.HistoricalExtremeBarIndex;
                return true;
            }

            EnsureChannelFitted(trend, allKlines);
            if (!trend.HasChannel || trend.UpperIntercept <= trend.LowerIntercept)
            {
                return false;
            }

            decimal zoneRatio = Math.Clamp(ChannelZonePct, 0m, 50m) / 100m;
            int maxBar = Math.Min(trend.EndIndex, currentBarIndex);

            for (int b = startCheckBar; b <= maxBar && b < allKlines.Count; b++)
            {
                var bar = allKlines[b];
                decimal upper = trend.GetUpperPrice(b);
                decimal lower = trend.GetLowerPrice(b);
                decimal height = upper - lower;
                if (height <= 0m) continue;

                if (trend.Type == ConsecutiveTrendType.Bullish)
                {
                    decimal topZonePrice = lower + height * (1m - zoneRatio);
                    if (bar.High >= topZonePrice - height * 0.02m || bar.High >= upper * 0.999m)
                    {
                        extremeBarIndex = b;
                        trend.HasHistoricalReachedExtreme = true;
                        trend.HistoricalExtremeBarIndex = b;
                        return true;
                    }
                }
                else if (trend.Type == ConsecutiveTrendType.Bearish)
                {
                    decimal bottomZonePrice = lower + height * zoneRatio;
                    if (bar.Low <= bottomZonePrice + height * 0.02m || bar.Low <= lower * 1.001m)
                    {
                        extremeBarIndex = b;
                        trend.HasHistoricalReachedExtreme = true;
                        trend.HistoricalExtremeBarIndex = b;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 检查连续走势进入观察阶段的 K 线：
        /// 1. 策略激活于连续同向第 5 根收盘时刻；
        /// 2. 上涨达到通道中线上方 (>= 50%) 进入 50%/75%/100% 观察阶段；
        /// 3. 下跌达到通道中线下方 (<= 50%) 进入 50%/25%/0% 观察阶段。
        /// 保持观察阶段起点 entryTime 锚定稳定，避免通道重绘把时间戳往后推而丢弃中间的 Tick 流。
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

            // 确保具有平行通道参数
            EnsureChannelFitted(trend, allKlines);

            // 若之前已经激活并记录过观察阶段入口，复用以保持观察期时间轴稳定！
            if (trend.ObservationEntryBarIndex >= fifthBarIndex && trend.ObservationEntryTime > 0)
            {
                entryBarIndex = trend.ObservationEntryBarIndex;
                entryTime = trend.ObservationEntryTime;
                entryPrice = trend.ObservationEntryPrice;
                channelUpper = trend.GetUpperPrice(entryBarIndex);
                channelLower = trend.GetLowerPrice(entryBarIndex);
                decimal h = channelUpper - channelLower;
                channelPosPct = h > 0m ? Math.Round((entryPrice - channelLower) / h * 100m, 1) : 100m;
                return true;
            }

            CheckHistoricalExtremeReached(trend, allKlines, currentBarIndex, out int _);

            int maxBar = Math.Min(trend.EndIndex, currentBarIndex);

            // 从第 5 根开始向后寻找满足进入观察阶段条件的 K 线 (触及 >= 50% 中线即开启观察)
            for (int b = fifthBarIndex; b <= maxBar; b++)
            {
                var bar = allKlines[b];
                decimal upper = trend.GetUpperPrice(b);
                decimal lower = trend.GetLowerPrice(b);
                decimal height = upper - lower;
                decimal mid = (upper + lower) / 2m;

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
                    // 上涨：达到 50% 中线以上即可开启观察 50%/75%/100% 线条反应
                    bool reached = bar.High >= mid - height * 0.01m || bar.Close >= mid - height * 0.01m;

                    if (reached)
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
                    // 下跌：达到 50% 中线以下即可开启观察 50%/25%/0% 线条反应
                    bool reached = bar.Low <= mid + height * 0.01m || bar.Close <= mid + height * 0.01m;

                    if (reached)
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
        /// 检查并激活指定波段的反转做单策略 (当满足观察阶段进入条件时激活并正式开启观察期)
        /// </summary>
        public bool TryActivateStrategy(ConsecutiveTrendItem trend, IReadOnlyList<RawKline> allKlines, int currentBarIndex, out string activationMsg)
        {
            activationMsg = "";
            if (!EnableReversalOrder || trend == null || allKlines == null) return false;

            if (_activatedTrendIds.Contains(trend.Id)) return false;

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

            string dirStr;
            if (trend.Type == ConsecutiveTrendType.Bullish)
            {
                dirStr = trend.HasHistoricalReachedExtreme
                    ? $"🔥 连续上涨历史曾达通道顶部 (Bar #{entryBarIndex}, 触及中线上方:{entryPrice:F2}, 通道位:{channelPosPct:F1}%)"
                    : $"🔥 连续上涨到达绿色通道顶部部分 (Bar #{entryBarIndex}, 触顶价:{entryPrice:F2}, 上轨:{channelUpper:F2}, 通道位:{channelPosPct:F1}%)";
            }
            else
            {
                dirStr = trend.HasHistoricalReachedExtreme
                    ? $"❄️ 连续下跌历史曾达通道底部 (Bar #{entryBarIndex}, 探及中线下方:{entryPrice:F2}, 通道位:{channelPosPct:F1}%)"
                    : $"❄️ 连续下跌到达绿色通道底部部分 (Bar #{entryBarIndex}, 探底价:{entryPrice:F2}, 下轨:{channelLower:F2}, 通道位:{channelPosPct:F1}%)";
            }

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

            _trendEndIndexMap[trend.Id] = trend.EndIndex;

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
                // 未达到绿色通道极值区域：若大周期连续形态已终结，输出未进入观察阶段的通道失效原因
                if (!trend.IsActive && trend.EndIndex <= currentBarIndex)
                {
                    if (!_loggedUnenteredTrendIds.Contains(trend.Id))
                    {
                        _loggedUnenteredTrendIds.Add(trend.Id);
                        DateTime trendStartDt = TimeHelper.FromUnixTimeMilliseconds(allKlines[trend.StartIndex].OpenTime).ToLocalTime();
                        DateTime trendEndDt = TimeHelper.FromUnixTimeMilliseconds(allKlines[trend.EndIndex].CloseTime).ToLocalTime();
                        string trendDir = trend.Type == ConsecutiveTrendType.Bullish ? "连续上涨" : "连续下跌";
                        string targetZone = trend.Type == ConsecutiveTrendType.Bullish ? "50% 通道中线 (未达 50%/75%/100% 观察线)" : "50% 通道中线 (未达 50%/25%/0% 观察线)";
                        string desc = $"[观察未激活-通道失效] ⚪ {trendDir}形态 (Bar #{trend.StartIndex}~#{trend.EndIndex}, {trendStartDt:HH:mm}~{trendEndDt:HH:mm}) 已终结，全程价格未触及{targetZone}，未进入观察阶段，策略安全退出。";
                        OnObservationCycleUpdated?.Invoke(null, desc);
                    }
                }
                return newSignals;
            }

            // 确保触发策略激活日志
            if (TryActivateStrategy(trend, allKlines, currentBarIndex, out var actMsg))
            {
                // 已在 TryActivateStrategy 内部触发 OnStrategyActivated
            }

            // 以达到通道极值进入观察阶段的 K 线收盘时刻作为第 1 个 3 分钟观察期的起点
            long t0 = entryTime;
            decimal zoneRatio = Math.Clamp(ChannelZonePct, 0m, 50m) / 100m;

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

                    // 确定当前观察期对应的通道基准 (用于判断价格处于通道高点还是中间低点)
                    int obsBigBarIndex = currentBarIndex;
                    for (int b = entryBarIndex; b < allKlines.Count; b++)
                    {
                        if (curCycleStart >= allKlines[b].OpenTime && curCycleStart <= allKlines[b].CloseTime)
                        {
                            obsBigBarIndex = b;
                            break;
                        }
                    }
                    decimal obsUpper = trend.GetUpperPrice(obsBigBarIndex);
                    decimal obs75 = trend.GetQuarter75Price(obsBigBarIndex);
                    decimal obsMid = trend.GetMidPrice(obsBigBarIndex);
                    decimal obs25 = trend.GetQuarter25Price(obsBigBarIndex);
                    decimal obsLower = trend.GetLowerPrice(obsBigBarIndex);
                    decimal obsHeight = obsUpper - obsLower;
                    decimal obsNearTol = obsHeight * (NearLineTolerancePct / 100m);
                    if (obsNearTol <= 0m) obsNearTol = obsHeight * 0.08m;

                    decimal obsStartPrice = cycleTicks[0].Price;
                    bool isBullishTrend = trend.Type == ConsecutiveTrendType.Bullish;

                    // 判断是否处于【通道中间低点】还是【通道高点】
                    // 通道中间低点：当前价格位于 50% 通道中线及其下方 (包括 50% 中线、25% 高度线、0% 通道下轨及附近)
                    // 通道高点：当前价格位于 50% 通道中线上方，逼近 75% 极值线或 100% 通道上轨
                    bool isMidLowZone = isBullishTrend && (obsStartPrice <= obsMid + obsNearTol);

                    // 1. 如果是上涨通道且处于通道中间低点：采用分钟级别高低点计算（取前 60 根 K 线计算，绝无未来函数）
                    var prev60Bars = new List<RawKline>();
                    decimal minute60High = decimal.MinValue;
                    decimal minute60Low = decimal.MaxValue;
                    long minute60HighTime = 0L;
                    long minute60LowTime = 0L;
                    decimal minuteMidHigh = decimal.MinValue;
                    long minuteMidHighTime = 0L;

                    if (isBullishTrend && isMidLowZone)
                    {
                        var sourceBars = (fallback1mKlines != null && fallback1mKlines.Count > 0) ? fallback1mKlines : allKlines;
                        for (int i = sourceBars.Count - 1; i >= 0; i--)
                        {
                            var b = sourceBars[i];
                            // 严格无未来函数：仅提取在当前观察期开始时刻 curCycleStart 之前已经闭合收盘的历史 K 线
                            if (b.CloseTime <= curCycleStart)
                            {
                                prev60Bars.Add(b);
                                if (prev60Bars.Count >= 60)
                                {
                                    break;
                                }
                            }
                        }
                        prev60Bars.Reverse(); // 恢复时间升序排列

                        for (int k = 0; k < prev60Bars.Count; k++)
                        {
                            var pb = prev60Bars[k];
                            if (pb.High > minute60High)
                            {
                                minute60High = pb.High;
                                minute60HighTime = pb.CloseTime;
                            }
                            if (pb.Low < minute60Low)
                            {
                                minute60Low = pb.Low;
                                minute60LowTime = pb.CloseTime;
                            }

                            // 统计通道中线及以下区间的分钟前高点 (避免被远端 75% 顶部极值遮蔽)
                            if (pb.High < obs75 - obsNearTol)
                            {
                                if (pb.High > minuteMidHigh)
                                {
                                    minuteMidHigh = pb.High;
                                    minuteMidHighTime = pb.CloseTime;
                                }
                            }
                        }

                        // 检验前 60 根 K 线中是否曾触及通道顶部极值区域 (作为历史极值背景参考)
                        if (minute60High > decimal.MinValue && obsHeight > 0m)
                        {
                            if (minute60High >= obsUpper - obsNearTol || minute60High >= obs75 - obsNearTol)
                            {
                                trend.HasHistoricalReachedExtreme = true;
                            }
                        }
                    }

                    // 分钟前高点：若存在中线及以下区间的分钟前高点则优先采用，否则使用整体前60根K线高点
                    decimal minutePrevHigh = (minuteMidHigh > decimal.MinValue) ? minuteMidHigh : minute60High;

                    // 2. 如果当前价格处于通道高点 (或下跌通道)：按 Tick 进行计算高低点
                    var lookbackTicks = new List<RawTick>();
                    decimal lookbackHigh = decimal.MinValue;
                    int lookbackHighIdx = -1;
                    long lookbackHighTime = 0L;

                    decimal lookbackLow = decimal.MaxValue;
                    int lookbackLowIdx = -1;
                    long lookbackLowTime = 0L;

                    if (!isMidLowZone)
                    {
                        if (ticks != null && ticks.Count > 0 && LookbackTickCount > 0)
                        {
                            for (int i = ticks.Count - 1; i >= 0; i--)
                            {
                                var t = ticks[i];
                                if (t.Time < curCycleStart)
                                {
                                    lookbackTicks.Add(t);
                                    if (lookbackTicks.Count >= LookbackTickCount)
                                    {
                                        break;
                                    }
                                }
                            }
                            lookbackTicks.Reverse(); // 恢复时间升序排列
                        }

                        for (int li = 0; li < lookbackTicks.Count; li++)
                        {
                            var lt = lookbackTicks[li];
                            if (lt.Price > lookbackHigh)
                            {
                                lookbackHigh = lt.Price;
                                lookbackHighTime = lt.Time;
                                lookbackHighIdx = li;
                            }
                            if (lt.Price < lookbackLow)
                            {
                                lookbackLow = lt.Price;
                                lookbackLowTime = lt.Time;
                                lookbackLowIdx = li;
                            }
                        }

                        // 方案 3：开仓高低点必须在观察期启动后实时走出 (严格在 cycleTicks 范围内跟踪波峰与波谷)
                        // 前序回溯 Tick 仅用于检测历史是否曾触及通道顶部或底部极值区 (作为历史极值背景参考)
                        if (lookbackHigh > decimal.MinValue && trend.Type == ConsecutiveTrendType.Bullish)
                        {
                            decimal upperRef = trend.GetUpperPrice(currentBarIndex);
                            decimal heightRef = upperRef - trend.GetLowerPrice(currentBarIndex);
                            decimal tolRef = heightRef * (NearLineTolerancePct / 100m);
                            if (heightRef > 0m && lookbackHigh >= upperRef - tolRef)
                            {
                                trend.HasHistoricalReachedExtreme = true;
                            }
                        }
                        else if (lookbackLow < decimal.MaxValue && trend.Type == ConsecutiveTrendType.Bearish)
                        {
                            decimal lowerRef = trend.GetLowerPrice(currentBarIndex);
                            decimal heightRef = trend.GetUpperPrice(currentBarIndex) - lowerRef;
                            decimal tolRef = heightRef * (NearLineTolerancePct / 100m);
                            if (heightRef > 0m && lookbackLow <= lowerRef + tolRef)
                            {
                                trend.HasHistoricalReachedExtreme = true;
                            }
                        }
                    }

                    decimal localPeak = cycleTicks[0].Price;
                    int localPeakIdx = 0;
                    long localPeakTime = cycleTicks[0].Time;
                    RawTick localPeakTick = cycleTicks[0];

                    decimal localTrough = cycleTicks[0].Price;
                    int localTroughIdx = 0;
                    long localTroughTime = cycleTicks[0].Time;
                    RawTick localTroughTick = cycleTicks[0];

                    bool tickTriggered = false;
                    decimal maxPullbackPct = 0m;
                    decimal maxBouncePct = 0m;
                    bool everReachedExtreme = false;
                    decimal lastRequiredExtremePrice = 0m;
                    string testedLineDesc = "无";
                    decimal testedLinePrice = 0m;
                    bool hasEnteredNearZone = false;
                    int firstNearTickIdx = -1;
                    int minTicksForSell = MinTicksAfterPeakForSell > 0 ? MinTicksAfterPeakForSell : 5;

                    for (int ti = 0; ti < cycleTicks.Count; ti++)
                    {
                        var t = cycleTicks[ti];
                        if (t.Price > runningHigh) runningHigh = t.Price;
                        if (t.Price < runningLow) runningLow = t.Price;
                        runningVol += t.Qty;
                        runningQuoteVol += t.QuoteQty;

                        int targetBigBarIndex = currentBarIndex;
                        for (int b = entryBarIndex; b < allKlines.Count; b++)
                        {
                            if (t.Time >= allKlines[b].OpenTime && t.Time <= allKlines[b].CloseTime)
                            {
                                targetBigBarIndex = b;
                                break;
                            }
                        }

                        decimal curBarUpper = trend.GetUpperPrice(targetBigBarIndex);
                        decimal curBar75 = trend.GetQuarter75Price(targetBigBarIndex);
                        decimal curBarMid = trend.GetMidPrice(targetBigBarIndex);
                        decimal curBar25 = trend.GetQuarter25Price(targetBigBarIndex);
                        decimal curBarLower = trend.GetLowerPrice(targetBigBarIndex);
                        decimal curBarHeight = curBarUpper - curBarLower;
                        decimal tol = curBarHeight * 0.02m;
                        decimal nearTol = curBarHeight * (NearLineTolerancePct / 100m);
                        if (nearTol <= 0m) nearTol = curBarHeight * 0.08m;
                        bool hasHistExtreme = trend.HasHistoricalReachedExtreme;
                        bool isBodyExceeded = trend.HasBodyExceededChannel;

                        // 实时微观突破/跌破通道检测 (上涨通道跌破下轨 / 下跌通道突破上轨)
                        if (!trend.HasTickBreakthrough)
                        {
                            if (trend.Type == ConsecutiveTrendType.Bullish && t.Price < curBarLower)
                            {
                                trend.HasTickBreakthrough = true;
                                trend.TickBreakthroughBarIndex = targetBigBarIndex;
                                trend.TickBreakthroughPrice = t.Price;
                                trend.TickBreakthroughTime = t.Time;
                                trend.TickBreakthroughTickIndex = ti;
                                trend.TickBreakthroughType = "⚡上涨跌破下轨";
                            }
                            else if (trend.Type == ConsecutiveTrendType.Bearish && t.Price > curBarUpper)
                            {
                                trend.HasTickBreakthrough = true;
                                trend.TickBreakthroughBarIndex = targetBigBarIndex;
                                trend.TickBreakthroughPrice = t.Price;
                                trend.TickBreakthroughTime = t.Time;
                                trend.TickBreakthroughTickIndex = ti;
                                trend.TickBreakthroughType = "⚡下跌突破上轨";
                            }
                        }

                        if (trend.Type == ConsecutiveTrendType.Bullish)
                        {
                            // 【连续上涨反转做空：到达线附近后过几个Tick，出现相对高点做空 (Sell)】
                            if (t.Price > localPeak)
                            {
                                localPeak = t.Price;
                                localPeakIdx = ti;
                                localPeakTime = t.Time;
                                localPeakTick = t;
                            }

                            decimal topZoneThreshold = curBarLower + curBarHeight * (1m - zoneRatio);
                            decimal topTarget = Math.Min(curBar75, topZoneThreshold);

                            // 检查当前价格或观察期波峰是否已到达线附近 (100%上轨附近、75%高度线/顶部极值区附近、50%中线附近)
                            bool isCurNearLine = (t.Price >= curBarUpper - nearTol) ||
                                                 (t.Price >= topTarget - nearTol) ||
                                                 (hasHistExtreme && t.Price >= curBarMid - nearTol);
                            bool isPeakNearLine = (localPeak >= curBarUpper - nearTol) ||
                                                  (localPeak >= topTarget - nearTol) ||
                                                  (hasHistExtreme && localPeak >= curBarMid - nearTol);
                            if ((isCurNearLine || isPeakNearLine) && !hasEnteredNearZone)
                            {
                                hasEnteredNearZone = true;
                                firstNearTickIdx = localPeakIdx;
                            }

                            // 四等分观察线识别：不要求严格压线，到线附近即纳入合格防守线
                            bool lineEligible = false;
                            if (localPeak >= curBarUpper - nearTol)
                            {
                                testedLineDesc = localPeak > curBarUpper + tol
                                    ? "100%通道上轨(突破超买极值)"
                                    : (localPeak >= curBarUpper - tol ? "100%通道上轨" : "100%通道上轨附近");
                                testedLinePrice = curBarUpper;
                                lineEligible = true;
                            }
                            else if (localPeak >= topTarget - nearTol)
                            {
                                testedLineDesc = localPeak >= topTarget - tol
                                    ? "75%通道高度线(顶部极值区)"
                                    : "75%通道高度线附近";
                                testedLinePrice = curBar75;
                                lineEligible = true;
                            }
                            else if (localPeak >= curBarMid - nearTol)
                            {
                                testedLineDesc = localPeak >= curBarMid - tol
                                    ? "50%通道中线"
                                    : "50%通道中线附近";
                                testedLinePrice = curBarMid;

                                // 优化：到达上涨时候到达通道中间，需要价格达到分钟前高点附近或者之上出现交易信号
                                bool reachedMinuteHigh = (minutePrevHigh > decimal.MinValue && localPeak >= minutePrevHigh - nearTol) ||
                                                         (minute60High > decimal.MinValue && localPeak >= minute60High - nearTol);

                                if (isBullishTrend)
                                {
                                    // 上涨通道到达通道中线：必须价格达到分钟前高点附近或者之上，方可满足入场条件
                                    lineEligible = reachedMinuteHigh && (hasHistExtreme || minute60High >= curBarMid - nearTol);
                                    if (!reachedMinuteHigh && minutePrevHigh > decimal.MinValue)
                                    {
                                        testedLineDesc += $" (未达分钟前高{minutePrevHigh:F2})";
                                    }
                                }
                                else
                                {
                                    lineEligible = hasHistExtreme;
                                }
                            }
                            else if (isMidLowZone && localPeak >= curBar25 - nearTol)
                            {
                                testedLineDesc = localPeak >= curBar25 - tol
                                    ? "25%通道高度线"
                                    : "25%通道高度线附近";
                                testedLinePrice = curBar25;
                                bool reachedMinuteHigh = (minutePrevHigh > decimal.MinValue && localPeak >= minutePrevHigh - nearTol) ||
                                                         (minute60High > decimal.MinValue && localPeak >= minute60High - nearTol);
                                lineEligible = reachedMinuteHigh && (hasHistExtreme || minute60High >= curBarMid - nearTol);
                                if (!reachedMinuteHigh && minutePrevHigh > decimal.MinValue)
                                {
                                    testedLineDesc += $" (未达分钟前高{minutePrevHigh:F2})";
                                }
                            }
                            else
                            {
                                testedLineDesc = isMidLowZone ? "未达通道线附近" : "未达50%中线附近";
                                testedLinePrice = curBarMid;
                                lineEligible = false;
                            }

                            lastRequiredExtremePrice = hasHistExtreme ? curBarMid : (topTarget - nearTol);
                            if (lineEligible) everReachedExtreme = true;

                            if (ti > localPeakIdx)
                            {
                                decimal pullbackPct = (localPeak - t.Price) / localPeak * 100m;
                                if (pullbackPct > maxPullbackPct) maxPullbackPct = pullbackPct;

                                // 判定是否满足【在tick级别形成高点后5个tick后进行下单，确保高点已经形成】:
                                int ticksSinceNear = hasEnteredNearZone ? (ti - firstNearTickIdx) : (ti - localPeakIdx);
                                int ticksSincePeak = ti - localPeakIdx;
                                bool ticksEnough = ticksSinceNear >= MinTicksAfterNearLine;

                                // 出现相对高点并转折回落:
                                // 做空单必须严格满足：在 tick 级别形成高点后至少经过 minTicksForSell 笔 Tick (默认 5 笔)，且当前价格处于高点下方 (t.Price < localPeak)，确保高点已经形成
                                bool isRelativeHighConfirmed = ticksEnough && 
                                                               (ticksSincePeak >= minTicksForSell) && 
                                                               (t.Price < localPeak);

                                if (lineEligible && isRelativeHighConfirmed)
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

                                    decimal entryPrice = t.Price; // 优化：在实际触发信号的 Tick 进行开仓下单与标记 (零未来函数)
                                    long triggerTime = t.Time;
                                    RawTick triggerTick = t;
                                    int triggerTickIdx = ti;
                                    decimal stopLoss = Math.Round(localPeak * 1.0015m, 2); // 防守止损精准锁定在观察期波峰上方 0.15%
                                    decimal takeProfit = trend.StartPrice;

                                    decimal highPt = isMidLowZone 
                                        ? (minute60High > decimal.MinValue ? minute60High : localPeak)
                                        : (lookbackHigh > decimal.MinValue ? Math.Max(lookbackHigh, localPeak) : localPeak);
                                    decimal lowPt = isMidLowZone 
                                        ? (minute60Low < decimal.MaxValue ? minute60Low : runningLow)
                                        : (lookbackLow < decimal.MaxValue ? Math.Min(lookbackLow, runningLow) : runningLow);
                                    string calcModeStr = isMidLowZone 
                                        ? $"分钟级前{prev60Bars.Count}根K线高低点" 
                                        : "Tick高低点";

                                    var signal = new ReversalOrderSignal
                                    {
                                        TrendId = trend.Id,
                                        PriorTrendType = trend.Type,
                                        ObservationCycleIndex = cycleIndex,
                                        TriggerTime = triggerTime,
                                        Price = entryPrice,
                                        StopLossPrice = stopLoss,
                                        TakeProfitPrice = takeProfit,
                                        HighPointPrice = highPt,
                                        LowPointPrice = lowPt,
                                        PeakTroughPrice = localPeak,
                                        PullbackPct = pullbackPct,
                                        IsTickStreamTriggered = true,
                                        IsBreakoutTrendFollowing = false,
                                        Direction = OrderSignalDirection.Sell,
                                        Pattern = pattern,
                                        BigBarIndex = targetBigBarIndex,
                                        SmallKline = runningBar,
                                        ObservationStartTime = curCycleStart,
                                        ObservationEndTime = curCycleEnd,
                                        TriggerTick = triggerTick,
                                        TriggerTickIndex = triggerTickIdx,
                                        CycleTotalTicks = cycleTicks.Count,
                                        PeakTroughTime = localPeakTime,
                                        ConfirmTime = t.Time,
                                        ConfirmTick = t,
                                        ChannelLineReaction = testedLineDesc,
                                        ChannelLinePrice = testedLinePrice,
                                        LookbackTicksCount = isMidLowZone ? 0 : lookbackTicks.Count,
                                        LookbackBarsCount = isMidLowZone ? prev60Bars.Count : 0,
                                        IsMinuteLevelCalculation = isMidLowZone,
                                        HighLowCalculationMode = calcModeStr,
                                        Minute60High = (isMidLowZone && minute60High > decimal.MinValue) ? minute60High : 0m,
                                        Minute60Low = (isMidLowZone && minute60Low < decimal.MaxValue) ? minute60Low : 0m,
                                        MinutePrevHigh = (minutePrevHigh > decimal.MinValue) ? minutePrevHigh : 0m,
                                        IsPeakFromLookback = false // 方案 3：波峰 100% 形成于观察期内部
                                    };

                                    cycleObj.ResultKline = runningBar;
                                    cycleObj.PatternResult = pattern;
                                    cycleObj.IsCompleted = true;
                                    cycleObj.IsSignalTriggered = true;
                                    cycleObj.Signal = signal;
                                    _processedCycleKeys.Add(cycleKey);
                                    _allSignals.Add(signal);
                                    newSignals.Add(signal);

                                    DateTime confirmDt = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).LocalDateTime;
                                    DateTime peakDt = DateTimeOffset.FromUnixTimeMilliseconds(localPeakTime).LocalDateTime;
                                    string tickSide = triggerTick.IsBuyerMaker ? "主动卖出(Taker Sell)" : "主动买入(Taker Buy)";
                                    string histNote = hasHistExtreme ? " (历史曾达极值)" : "";
                                    string triggerReason = $"高点后经{ticksSincePeak}笔Tick确立(回落:-{pullbackPct:F2}%, 到线后经{ticksSinceNear + 1}笔Tick, 门槛:{minTicksForSell}笔)";
                                    string calcNote = isMidLowZone
                                        ? $" [计算模式: 分钟级前{prev60Bars.Count}根K线高低点(高:{minute60High:F2}, 低:{minute60Low:F2})]"
                                        : $" [计算模式: Tick高低点(回溯{lookbackTicks.Count}笔Tick)]";
                                    string prevHighTag = (minutePrevHigh > decimal.MinValue)
                                        ? (localPeak >= minutePrevHigh ? $" | 🎯 已超越分钟前高({minutePrevHigh:F2})" : $" | 🎯 已达分钟前高({minutePrevHigh:F2})附近")
                                        : "";

                                    string notify = $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 Tick流在【{testedLineDesc}】(价格:{testedLinePrice:F2}) 触发 🔴高点做空(Sell)！{histNote}{calcNote}{prevHighTag}\n" +
                                                    $"  └ 🎯 触发Tick开仓: 开仓价:{entryPrice:F2} USDT (触发时刻:{confirmDt:HH:mm:ss.fff}, Tick #{ti + 1}/{cycleTicks.Count}, {tickSide}) | 观察期波峰:{localPeak:F2} (波峰时刻:{peakDt:HH:mm:ss.fff}, {triggerReason})\n" +
                                                    $"  └ 📊 极值推演风控: 触发Tick开仓:{entryPrice:F2} | 波峰防守止损:{stopLoss:F2} (+0.15%), 目标止盈:{takeProfit:F2} | 高低点基准: 分钟前高{signal.MinutePrevHigh:F2}, 高{signal.HighPointPrice:F2} 低{signal.LowPointPrice:F2} ({signal.HighLowCalculationMode})";
                                    OnReversalOrderSignal?.Invoke(signal, notify);
                                    break;
                                }
                            }
                        }
                        else if (trend.Type == ConsecutiveTrendType.Bearish)
                        {
                            // 【连续下跌反转做多：到达线附近后过几个Tick，出现相对低点做多 (Buy)】
                            if (t.Price < localTrough)
                            {
                                localTrough = t.Price;
                                localTroughIdx = ti;
                                localTroughTime = t.Time;
                                localTroughTick = t;
                            }

                            decimal bottomZoneThreshold = curBarLower + curBarHeight * zoneRatio;
                            decimal bottomTarget = Math.Max(curBar25, bottomZoneThreshold);

                            // 检查当前价格或观察期波谷是否已到达线附近 (0%下轨附近、25%高度线/底部极值区附近、50%中线附近)
                            bool isCurNearLine = (t.Price <= curBarLower + nearTol) ||
                                                 (t.Price <= bottomTarget + nearTol) ||
                                                 (hasHistExtreme && t.Price <= curBarMid + nearTol);
                            bool isTroughNearLine = (localTrough <= curBarLower + nearTol) ||
                                                    (localTrough <= bottomTarget + nearTol) ||
                                                    (hasHistExtreme && localTrough <= curBarMid + nearTol);
                            if ((isCurNearLine || isTroughNearLine) && !hasEnteredNearZone)
                            {
                                hasEnteredNearZone = true;
                                firstNearTickIdx = localTroughIdx;
                            }

                            // 四等分观察线识别：不要求严格压线，到线附近即纳入合格防守线
                            bool lineEligible = false;
                            if (localTrough <= curBarLower + nearTol)
                            {
                                testedLineDesc = localTrough < curBarLower - tol
                                    ? "0%通道下轨(跌破超卖极值)"
                                    : (localTrough <= curBarLower + tol ? "0%通道下轨" : "0%通道下轨附近");
                                testedLinePrice = curBarLower;
                                lineEligible = true;
                            }
                            else if (localTrough <= bottomTarget + nearTol)
                            {
                                testedLineDesc = localTrough <= bottomTarget + tol
                                    ? "25%通道高度线(底部极值区)"
                                    : "25%通道高度线附近";
                                testedLinePrice = curBar25;
                                lineEligible = true;
                            }
                            else if (localTrough <= curBarMid + nearTol)
                            {
                                testedLineDesc = localTrough <= curBarMid + tol
                                    ? "50%通道中线"
                                    : "50%通道中线附近";
                                testedLinePrice = curBarMid;
                                // 若历史曾达通道底，放宽至中线即可触发做多
                                lineEligible = hasHistExtreme;
                            }
                            else
                            {
                                testedLineDesc = "未达50%中线附近";
                                testedLinePrice = curBarMid;
                                lineEligible = false;
                            }

                            lastRequiredExtremePrice = hasHistExtreme ? curBarMid : (bottomTarget + nearTol);
                            if (lineEligible) everReachedExtreme = true;

                            if (ti > localTroughIdx)
                            {
                                decimal bouncePct = (t.Price - localTrough) / localTrough * 100m;
                                if (bouncePct > maxBouncePct) maxBouncePct = bouncePct;

                                // 判定是否满足【到线附近后过几个Tick，出现相对Tick的低点】:
                                int ticksSinceNear = hasEnteredNearZone ? (ti - firstNearTickIdx) : (ti - localTroughIdx);
                                int ticksSinceTrough = ti - localTroughIdx;
                                bool ticksEnough = ticksSinceNear >= MinTicksAfterNearLine;

                                // 出现相对低点并转折回升:
                                // 1) 达到标准反弹阈值 (如 >= 0.06%);
                                // 2) 或在到线经过数笔Tick后，波谷确立并连续回升 (ticksSinceTrough >= 2 且反弹 >= 0.02% 或价格高于波谷)
                                bool isRelativeLowConfirmed = ticksEnough && (t.Price > localTrough) && (
                                    (bouncePct >= TickPullbackThresholdPct) ||
                                    (ticksSinceTrough >= 2 && bouncePct >= 0.02m)
                                );

                                if (lineEligible && isRelativeLowConfirmed)
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

                                    decimal entryPrice = t.Price; // 优化：在实际触发信号的 Tick 进行开仓下单与标记 (零未来函数)
                                    long triggerTime = t.Time;
                                    RawTick triggerTick = t;
                                    int triggerTickIdx = ti;
                                    decimal stopLoss = Math.Round(localTrough * 0.9985m, 2); // 防守止损精准锁定在观察期波谷下方 0.15%
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
                                        IsBreakoutTrendFollowing = false,
                                        Direction = OrderSignalDirection.Buy,
                                        Pattern = pattern,
                                        BigBarIndex = targetBigBarIndex,
                                        SmallKline = runningBar,
                                        ObservationStartTime = curCycleStart,
                                        ObservationEndTime = curCycleEnd,
                                        TriggerTick = triggerTick,
                                        TriggerTickIndex = triggerTickIdx,
                                        CycleTotalTicks = cycleTicks.Count,
                                        PeakTroughTime = localTroughTime,
                                        ConfirmTime = t.Time,
                                        ConfirmTick = t,
                                        ChannelLineReaction = testedLineDesc,
                                        ChannelLinePrice = testedLinePrice,
                                        LookbackTicksCount = lookbackTicks.Count,
                                        IsPeakFromLookback = false // 方案 3：波谷 100% 形成于观察期内部
                                    };

                                    cycleObj.ResultKline = runningBar;
                                    cycleObj.PatternResult = pattern;
                                    cycleObj.IsCompleted = true;
                                    cycleObj.IsSignalTriggered = true;
                                    cycleObj.Signal = signal;
                                    _processedCycleKeys.Add(cycleKey);
                                    _allSignals.Add(signal);
                                    newSignals.Add(signal);

                                    DateTime confirmDt = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).LocalDateTime;
                                    DateTime troughDt = DateTimeOffset.FromUnixTimeMilliseconds(localTroughTime).LocalDateTime;
                                    string tickSide = triggerTick.IsBuyerMaker ? "主动卖出(Taker Sell)" : "主动买入(Taker Buy)";
                                    string histNote = hasHistExtreme ? " (历史曾达极值)" : "";
                                    string triggerReason = bouncePct >= TickPullbackThresholdPct
                                        ? $"企稳反弹:+{bouncePct:F2}% (门槛:{TickPullbackThresholdPct:F2}%)"
                                        : $"相对低点确立(反弹:+{bouncePct:F2}%, 到线后经{ticksSinceNear + 1}笔Tick)";

                                    string notify = $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 Tick流在【{testedLineDesc}】(价格:{testedLinePrice:F2}) 触发 🟢低点做多(Buy)！{histNote}\n" +
                                                    $"  └ 🎯 触发Tick开仓: 开仓价:{entryPrice:F2} USDT (触发时刻:{confirmDt:HH:mm:ss.fff}, Tick #{ti + 1}/{cycleTicks.Count}, {tickSide}) | 观察期波谷:{localTrough:F2} (波谷时刻:{troughDt:HH:mm:ss.fff}, {triggerReason})\n" +
                                                    $"  └ 📊 极值推演风控: 触发Tick开仓:{entryPrice:F2} | 波谷防守止损:{stopLoss:F2} (-0.15%), 目标止盈:{takeProfit:F2} | 已在触发Tick标记短黄线";
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

                            string failureReason;
                            if (cycleTicks == null || cycleTicks.Count == 0)
                            {
                                failureReason = "本期无有效 Tick 流成交数据";
                            }
                            else if (trend.Type == ConsecutiveTrendType.Bullish)
                            {
                                int finalTicksSinceNear = hasEnteredNearZone ? (cycleTicks.Count - 1 - firstNearTickIdx) : 0;
                                if (!everReachedExtreme)
                                {
                                    if (testedLineDesc.Contains("未达分钟前高"))
                                    {
                                        failureReason = $"最高价 {localPeak:F2} 未达到分钟前高点附近或之上 (要求达到 {minutePrevHigh - obsNearTol:F2} ~ {minutePrevHigh:F2} 或之上，当前测试:{testedLineDesc})";
                                    }
                                    else
                                    {
                                        failureReason = $"最高价 {localPeak:F2} 未触及有效观察线附近 (要求触及 50%/75%/100% 观察线附近:{lastRequiredExtremePrice:F2})";
                                    }
                                }
                                else if (!hasEnteredNearZone || finalTicksSinceNear < MinTicksAfterNearLine)
                                {
                                    failureReason = $"在【{testedLineDesc}】附近仅短暂触及，未达到至少经历 {MinTicksAfterNearLine} 笔Tick的观察门槛";
                                }
                                else if (localPeakIdx >= cycleTicks.Count - minTicksForSell)
                                {
                                    failureReason = $"在【{testedLineDesc}】最高冲至 {localPeak:F2}，但出现于周期末尾（后仅经 {cycleTicks.Count - 1 - localPeakIdx} 笔Tick，未达高点形成后至少 {minTicksForSell} 笔Tick确认下单要求）";
                                }
                                else if (maxPullbackPct <= 0m)
                                {
                                    failureReason = $"测试【{testedLineDesc}】后价格持续持平未见滞涨回落，未确立相对高点转折";
                                }
                                else
                                {
                                    failureReason = $"测试【{testedLineDesc}】后滞涨回落未达触发要求";
                                }
                            }
                            else // Bearish
                            {
                                int finalTicksSinceNear = hasEnteredNearZone ? (cycleTicks.Count - 1 - firstNearTickIdx) : 0;
                                if (!everReachedExtreme)
                                {
                                    failureReason = $"最低价 {localTrough:F2} 未触及有效观察线附近 (要求触及 50%/25%/0% 观察线附近:{lastRequiredExtremePrice:F2})";
                                }
                                else if (!hasEnteredNearZone || finalTicksSinceNear < MinTicksAfterNearLine)
                                {
                                    failureReason = $"在【{testedLineDesc}】附近仅短暂触及，未达到至少经历 {MinTicksAfterNearLine} 笔Tick的观察门槛";
                                }
                                else if (localTroughIdx >= cycleTicks.Count - 2)
                                {
                                    failureReason = $"在【{testedLineDesc}】最低探至 {localTrough:F2}，但出现于周期最末尾，尚未形成转折相对低点";
                                }
                                else if (maxBouncePct < TickPullbackThresholdPct)
                                {
                                    failureReason = $"测试【{testedLineDesc}】后波谷 {localTrough:F2} 最大反弹仅 +{maxBouncePct:F3}%，未确立相对低点转折 (门槛 +{TickPullbackThresholdPct:F2}%)";
                                }
                                else
                                {
                                    failureReason = $"测试【{testedLineDesc}】后探底企稳未达触发要求";
                                }
                            }

                            string cycleTickDetail = cycleTicks != null && cycleTicks.Count > 0
                                ? $" (共推演 {cycleTicks.Count} 笔Tick, 最高:{runningHigh:F2}, 最低:{runningLow:F2})"
                                : "";
                            string logMsg = $"[观察期推演] 第 {cycleIndex} 个 {ObservationMinutes}分钟 Tick流推演完毕{cycleTickDetail}\n" +
                                            $"  └ ⚠️ 本期未触发理由: {failureReason}，进入第 {cycleIndex + 1} 期...";
                            OnObservationCycleUpdated?.Invoke(cycleObj, logMsg);
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

                    // 判定是否符合前序趋势的反转做单形态
                    bool hasHistExtreme = trend.HasHistoricalReachedExtreme;
                    OrderSignalDirection direction;
                    bool isReversal = CandlestickPatternClassifier.IsReversalOrderSignal(trend.Type, pattern, out direction);

                    string fallbackLineDesc = "";
                    decimal fallbackLinePrice = 0m;

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
                    decimal curBar75 = trend.GetQuarter75Price(targetBigBarIndex);
                    decimal curBarMid = trend.GetMidPrice(targetBigBarIndex);
                    decimal curBar25 = trend.GetQuarter25Price(targetBigBarIndex);
                    decimal curBarLower = trend.GetLowerPrice(targetBigBarIndex);
                    decimal curBarHeight = curBarUpper - curBarLower;
                    decimal tol = curBarHeight * 0.02m;
                    decimal nearTol = curBarHeight * (NearLineTolerancePct / 100m);
                    if (nearTol <= 0m) nearTol = curBarHeight * 0.08m;
                    decimal topZoneThreshold = curBarLower + curBarHeight * (1m - zoneRatio);
                    decimal bottomZoneThreshold = curBarLower + curBarHeight * zoneRatio;

                    bool isBullishTrendFallback = trend.Type == ConsecutiveTrendType.Bullish;
                    bool isMidLowZoneFallback = isBullishTrendFallback && (sb.Open <= curBarMid + nearTol);

                    var prev60BarsFallback = new List<RawKline>();
                    decimal minute60HighFallback = decimal.MinValue;
                    decimal minute60LowFallback = decimal.MaxValue;
                    decimal minuteMidHighFallback = decimal.MinValue;

                    if (isBullishTrendFallback && isMidLowZoneFallback)
                    {
                        var sourceBars = fallback1mKlines ?? allKlines;
                        for (int i = sourceBars.Count - 1; i >= 0; i--)
                        {
                            var b = sourceBars[i];
                            // 严格无未来函数：仅提取在当前观察期开始时刻 curCycleStart 之前已经闭合收盘的历史 K 线
                            if (b.CloseTime <= curCycleStart)
                            {
                                prev60BarsFallback.Add(b);
                                if (prev60BarsFallback.Count >= 60)
                                {
                                    break;
                                }
                            }
                        }
                        prev60BarsFallback.Reverse();

                        for (int k = 0; k < prev60BarsFallback.Count; k++)
                        {
                            var pb = prev60BarsFallback[k];
                            if (pb.High > minute60HighFallback) minute60HighFallback = pb.High;
                            if (pb.Low < minute60LowFallback) minute60LowFallback = pb.Low;

                            // 统计通道中线及以下区间的分钟前高点 (避免被远端 75% 顶部极值遮蔽)
                            if (pb.High < curBar75 - nearTol)
                            {
                                if (pb.High > minuteMidHighFallback) minuteMidHighFallback = pb.High;
                            }
                        }

                        if (minute60HighFallback > decimal.MinValue && curBarHeight > 0m)
                        {
                            if (minute60HighFallback >= curBarUpper - nearTol || minute60HighFallback >= curBar75 - nearTol)
                            {
                                trend.HasHistoricalReachedExtreme = true;
                                hasHistExtreme = true;
                            }
                        }
                    }

                    decimal minutePrevHighFallback = (minuteMidHighFallback > decimal.MinValue) ? minuteMidHighFallback : minute60HighFallback;
                    // 优化：到达上涨时候到达通道中间，需要价格达到分钟前高点附近或者之上出现交易信号
                    bool reachedMinuteHighFallback = (minutePrevHighFallback > decimal.MinValue && sb.High >= minutePrevHighFallback - nearTol) ||
                                                     (minute60HighFallback > decimal.MinValue && sb.High >= minute60HighFallback - nearTol);

                    if (isReversal)
                    {
                        if (direction == OrderSignalDirection.Sell)
                        {
                            // 连涨常规反转做空：观察 100%上轨、75%高度线/顶部极值区、50%中线及其附近
                            decimal topTarget = Math.Min(curBar75, topZoneThreshold);

                            if (sb.High >= curBarUpper - nearTol)
                            {
                                fallbackLineDesc = sb.High > curBarUpper + tol
                                    ? "100%通道上轨(突破超买极值)"
                                    : (sb.High >= curBarUpper - tol ? "100%通道上轨" : "100%通道上轨附近");
                                fallbackLinePrice = curBarUpper;
                            }
                            else if (sb.High >= topTarget - nearTol)
                            {
                                fallbackLineDesc = sb.High >= topTarget - tol
                                    ? "75%通道高度线(顶部极值区)"
                                    : "75%通道高度线附近";
                                fallbackLinePrice = curBar75;
                            }
                            else if (sb.High >= curBarMid - nearTol)
                            {
                                bool eligible = reachedMinuteHighFallback && (hasHistExtreme || minute60HighFallback >= curBarMid - nearTol);
                                if (eligible)
                                {
                                    fallbackLineDesc = sb.High >= curBarMid - tol
                                        ? "50%通道中线"
                                        : "50%通道中线附近";
                                    fallbackLinePrice = curBarMid;
                                }
                                else
                                {
                                    isReversal = false;
                                }
                            }
                            else if (isMidLowZoneFallback && sb.High >= curBar25 - nearTol)
                            {
                                bool eligible = reachedMinuteHighFallback && (hasHistExtreme || minute60HighFallback >= curBarMid - nearTol);
                                if (eligible)
                                {
                                    fallbackLineDesc = sb.High >= curBar25 - tol
                                        ? "25%通道高度线"
                                        : "25%通道高度线附近";
                                    fallbackLinePrice = curBar25;
                                }
                                else
                                {
                                    isReversal = false;
                                }
                            }
                            else
                            {
                                isReversal = false;
                            }
                        }
                        else if (direction == OrderSignalDirection.Buy)
                        {
                            // 连跌常规反转做多：观察 0%下轨、25%高度线/底部极值区、50%中线及其附近
                            decimal bottomTarget = Math.Max(curBar25, bottomZoneThreshold);
                            if (sb.Low <= curBarLower + nearTol)
                            {
                                fallbackLineDesc = sb.Low < curBarLower - tol
                                    ? "0%通道下轨(跌破超卖极值)"
                                    : (sb.Low <= curBarLower + tol ? "0%通道下轨" : "0%通道下轨附近");
                                fallbackLinePrice = curBarLower;
                            }
                            else if (sb.Low <= bottomTarget + nearTol)
                            {
                                fallbackLineDesc = sb.Low <= bottomTarget + tol
                                    ? "25%通道高度线(底部极值区)"
                                    : "25%通道高度线附近";
                                fallbackLinePrice = curBar25;
                            }
                            else if (hasHistExtreme && sb.Low <= curBarMid + nearTol)
                            {
                                fallbackLineDesc = sb.Low <= curBarMid + tol
                                    ? "50%通道中线"
                                    : "50%通道中线附近";
                                fallbackLinePrice = curBarMid;
                            }
                            else
                            {
                                isReversal = false;
                            }
                        }
                    }

                    if (isReversal && !_processedCycleKeys.Contains(cycleKey))
                    {
                        cycleObj.IsSignalTriggered = true;
                        _processedCycleKeys.Add(cycleKey);

                        decimal entryPrice = sb.Close; // 优化：在实际触发信号的收盘时刻进行开仓下单与标记 (零未来函数)
                        decimal peakTrough = direction == OrderSignalDirection.Sell ? sb.High : sb.Low; // 观察期波峰/波谷极值
                        long triggerTime = sb.CloseTime;
                        decimal stopLoss = direction == OrderSignalDirection.Sell
                            ? Math.Round(sb.High * 1.0015m, 2)
                            : Math.Round(sb.Low * 0.9985m, 2);
                        decimal takeProfit = trend.StartPrice;
                        decimal pullback = direction == OrderSignalDirection.Sell
                            ? (sb.High > 0m ? (sb.High - sb.Close) / sb.High * 100m : 0m)
                            : (sb.Low > 0m ? (sb.Close - sb.Low) / sb.Low * 100m : 0m);

                        decimal fallbackHighPt = isMidLowZoneFallback && minute60HighFallback > decimal.MinValue ? minute60HighFallback : sb.High;
                        decimal fallbackLowPt = isMidLowZoneFallback && minute60LowFallback < decimal.MaxValue ? minute60LowFallback : sb.Low;
                        string fallbackCalcMode = isMidLowZoneFallback ? $"分钟级前{prev60BarsFallback.Count}根K线高低点" : "1m回退K线高低点";

                        var signal = new ReversalOrderSignal
                        {
                            TrendId = trend.Id,
                            PriorTrendType = trend.Type,
                            ObservationCycleIndex = cycleIndex,
                            TriggerTime = triggerTime,
                            Price = entryPrice,
                            StopLossPrice = stopLoss,
                            TakeProfitPrice = takeProfit,
                            HighPointPrice = fallbackHighPt,
                            LowPointPrice = fallbackLowPt,
                            PeakTroughPrice = peakTrough,
                            PullbackPct = pullback,
                            IsTickStreamTriggered = false,
                            IsBreakoutTrendFollowing = false,
                            Direction = direction,
                            Pattern = pattern,
                            BigBarIndex = targetBigBarIndex,
                            SmallKline = sb,
                            ObservationStartTime = curCycleStart,
                            ObservationEndTime = curCycleEnd,
                            ChannelLineReaction = fallbackLineDesc,
                            ChannelLinePrice = fallbackLinePrice,
                            LookbackBarsCount = isMidLowZoneFallback ? prev60BarsFallback.Count : 0,
                            IsMinuteLevelCalculation = isMidLowZoneFallback,
                            HighLowCalculationMode = fallbackCalcMode,
                            Minute60High = (isMidLowZoneFallback && minute60HighFallback > decimal.MinValue) ? minute60HighFallback : 0m,
                            Minute60Low = (isMidLowZoneFallback && minute60LowFallback < decimal.MaxValue) ? minute60LowFallback : 0m,
                            MinutePrevHigh = (isMidLowZoneFallback && minutePrevHighFallback > decimal.MinValue) ? minutePrevHighFallback : 0m,
                            IsPeakFromLookback = false
                        };

                        cycleObj.Signal = signal;
                        _allSignals.Add(signal);
                        newSignals.Add(signal);

                        DateTime barStart = TimeHelper.FromUnixTimeMilliseconds(sb.OpenTime).ToLocalTime();
                        DateTime barEnd = TimeHelper.FromUnixTimeMilliseconds(sb.CloseTime).ToLocalTime();
                        string dirText = direction == OrderSignalDirection.Sell ? "🔴高点做空(Sell)" : "🟢低点做多(Buy)";
                        string histNote = hasHistExtreme ? " (历史曾达极值，放宽至中线)" : "";
                        string calcNote = isMidLowZoneFallback
                            ? $" [计算模式: 分钟级前{prev60BarsFallback.Count}根K线高低点(高:{minute60HighFallback:F2}, 低:{minute60LowFallback:F2})]"
                            : " [计算模式: 1m回退K线高低点]";
                        string prevHighTagFallback = (isMidLowZoneFallback && minutePrevHighFallback > decimal.MinValue)
                            ? (sb.High >= minutePrevHighFallback ? $" | 🎯 已超越分钟前高({minutePrevHighFallback:F2})" : $" | 🎯 已达分钟前高({minutePrevHighFallback:F2})附近")
                            : "";
                        string notify = $"[做单信号] ⚡ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 1m回退在【{fallbackLineDesc}】(价格:{fallbackLinePrice:F2}) 触发 {dirText}！{histNote}{calcNote}{prevHighTagFallback}\n" +
                                        $"  └ 📊 观察期K线明细: 时间:{barStart:HH:mm:ss}~{barEnd:HH:mm:ss} | 开:{sb.Open:F2} 高:{sb.High:F2} 低:{sb.Low:F2} 收:{sb.Close:F2} | 量:{sb.Volume:F2} | 形态:[#{pattern.PatternId}]{pattern.PatternName}\n" +
                                        $"  └ 🎯 触发时刻开仓风控: 开仓价:{entryPrice:F2} USDT ({(direction == OrderSignalDirection.Sell ? "高点做空" : "低点做多")}) | 极值参考:{peakTrough:F2} | 防守止损:{stopLoss:F2} (0.15%), 目标止盈:{takeProfit:F2} | 高低点基准: 分钟前高{signal.MinutePrevHigh:F2}, 高{signal.HighPointPrice:F2} 低{signal.LowPointPrice:F2} ({signal.HighLowCalculationMode})";

                        OnReversalOrderSignal?.Invoke(signal, notify);
                        break;
                    }
                    else if (!isReversal && !_processedCycleKeys.Contains(cycleKey))
                    {
                        _processedCycleKeys.Add(cycleKey);
                        decimal lastReqPrice = hasHistExtreme
                            ? curBarMid
                            : (trend.Type == ConsecutiveTrendType.Bullish ? Math.Min(curBar75, topZoneThreshold) : Math.Max(curBar25, bottomZoneThreshold));
                        string specificReason;
                        if (CandlestickPatternClassifier.IsReversalOrderSignal(trend.Type, pattern, out var reqDir))
                        {
                            string reqExtreme;
                            if (reqDir == OrderSignalDirection.Sell)
                            {
                                if (isBullishTrendFallback && sb.High >= curBarMid - nearTol && !reachedMinuteHighFallback && minutePrevHighFallback > decimal.MinValue)
                                {
                                    reqExtreme = $"最高价 {sb.High:F2} 未达到分钟前高点附近或之上 (要求达到 {minutePrevHighFallback - nearTol:F2} ~ {minutePrevHighFallback:F2} 或之上)";
                                }
                                else
                                {
                                    reqExtreme = $"最高价 {sb.High:F2} 未达到 50%/75%/100% 观察线 (当前要求:{lastReqPrice:F2})";
                                }
                            }
                            else
                            {
                                reqExtreme = $"最低价 {sb.Low:F2} 未达到 50%/25%/0% 观察线 (当前要求:{lastReqPrice:F2})";
                            }
                            specificReason = $"出现反转形态 [#{pattern.PatternId}]{pattern.PatternName}，但{reqExtreme}";
                        }
                        else
                        {
                            specificReason = $"形态 [#{pattern.PatternId}]{pattern.PatternName} 未满足反转做单要求";
                        }

                        string logMsg = $"[观察期推演] 第 {cycleIndex} 个 {ObservationMinutes}分钟 1m聚合推演完毕 (开:{sb.Open:F2} 高:{sb.High:F2} 低:{sb.Low:F2} 收:{sb.Close:F2})\n" +
                                        $"  └ ⚠️ 本期未触发理由: {specificReason}，进入第 {cycleIndex + 1} 期...";
                        OnObservationCycleUpdated?.Invoke(cycleObj, logMsg);
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

            // 若大周期连续走势已结束，且该波段全程推演完毕均未产生做单信号，输出波段失效终结总结
            if (!trend.IsActive && trend.EndIndex <= currentBarIndex && !_allSignals.Any(s => s.TrendId == trend.Id))
            {
                if (!_loggedTerminatedTrendIds.Contains(trend.Id))
                {
                    _loggedTerminatedTrendIds.Add(trend.Id);
                    DateTime trendEndDt = TimeHelper.FromUnixTimeMilliseconds(allKlines[trend.EndIndex].CloseTime).ToLocalTime();
                    string trendDir = trend.Type == ConsecutiveTrendType.Bullish ? "连续上涨" : "连续下跌";
                    int totalCyclesEvaluated = Math.Max(0, cycleIndex - 1);
                    var lastCycle = _allCycles.LastOrDefault(c => c.TrendId == trend.Id);
                    string termLog = $"[观察结束-波段失效] 🛑 {trendDir}形态已于 Bar #{trend.EndIndex} ({trendEndDt:HH:mm:ss}) 终结反转！\n" +
                                     $"  └ 📋 波段推演总结: 累计完成 {totalCyclesEvaluated} 个 {ObservationMinutes}分钟观察期推演，全程未捕捉到合格反转做单点，观察期正式失效结束，策略安全退出。";
                    OnObservationCycleUpdated?.Invoke(lastCycle, termLog);
                }
            }

            return newSignals;
        }

        /// <summary>
        /// 将指定的 Tick 数据存入该连续趋势形态的内存缓存
        /// </summary>
        public void CacheTrendTicks(int trendId, RawTick[] ticks)
        {
            if (ticks != null && ticks.Length > 0)
            {
                _trendTickCache[trendId] = ticks;
            }
        }

        /// <summary>
        /// 预加载指定趋势形态的 Tick 数据到内存缓存中
        /// </summary>
        public async Task PreloadTrendTicksAsync(
            string coin,
            ConsecutiveTrendItem trend,
            IReadOnlyList<RawKline> allKlines,
            CancellationToken ct = default)
        {
            if (trend == null || allKlines == null || allKlines.Count == 0) return;
            if (_trendTickCache.TryGetValue(trend.Id, out var cached) && cached != null && cached.Count > 0) return;

            int fifthBarIndex = trend.StartIndex + 4;
            if (fifthBarIndex >= allKlines.Count) return;

            long t0 = allKlines[fifthBarIndex].OpenTime;
            long lookbackT0 = Math.Max(0, t0 - 3600_000L);
            long trendEndT = allKlines[Math.Min(trend.EndIndex, allKlines.Count - 1)].CloseTime;
            long maxObsSpan = (long)ObservationMinutes * 60_000L * MaxObservationCycles;
            long fetchEnd = Math.Max(trendEndT, t0 + maxObsSpan);

            try
            {
                var ticks = await ParquetDataReader.ReadTicksForTimeRangeAsync(coin, lookbackT0, fetchEnd, ct).ConfigureAwait(false);
                if (ticks != null && ticks.Length > 0)
                {
                    _trendTickCache[trend.Id] = ticks;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PreloadTrendTicksAsync] 异常: {ex.Message}");
            }
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
            long trendEndT = allKlines[Math.Min(trend.EndIndex, allKlines.Count - 1)].CloseTime;
            long maxObsSpan = (long)ObservationMinutes * 60_000L * MaxObservationCycles;
            long fetchEnd = Math.Max(trendEndT, t0 + maxObsSpan);

            // 优先检查并复用已缓存 Tick
            if (_trendTickCache.TryGetValue(trend.Id, out var cachedTicks) && cachedTicks != null && cachedTicks.Count > 0)
            {
                long currentBarClose = allKlines[Math.Min(allKlines.Count - 1, currentBarIndex)].CloseTime;
                if (cachedTicks[cachedTicks.Count - 1].Time >= Math.Min(fetchEnd, currentBarClose))
                {
                    return EvaluateObservationCycles(trend, allKlines, cachedTicks, currentBarIndex, fallback1mKlines);
                }
            }

            try
            {
                // 回溯 1 小时读取 Tick 数据，确保包含观察期前序的至多 1000 笔 Tick
                long lookbackT0 = Math.Max(0, t0 - 3600_000L);
                var ticks = await ParquetDataReader.ReadTicksForTimeRangeAsync(coin, lookbackT0, fetchEnd, ct).ConfigureAwait(false);
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
