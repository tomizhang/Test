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
        /// 检查并激活指定波段的反转做单策略 (在第 5 根收盘时刻激活并输出日志)
        /// </summary>
        public bool TryActivateStrategy(ConsecutiveTrendItem trend, IReadOnlyList<RawKline> allKlines, int currentBarIndex, out string activationMsg)
        {
            activationMsg = "";
            if (!EnableReversalOrder || trend == null || allKlines == null) return false;

            int fifthBarIndex = trend.StartIndex + 4;
            if (currentBarIndex < fifthBarIndex || fifthBarIndex >= allKlines.Count) return false;

            if (_activatedTrendIds.Contains(trend.Id)) return false;

            _activatedTrendIds.Add(trend.Id);

            var fifthBar = allKlines[fifthBarIndex];
            DateTime t0Dt = TimeHelper.FromUnixTimeMilliseconds(fifthBar.CloseTime).ToLocalTime();
            string dirStr = trend.Type == ConsecutiveTrendType.Bullish ? "🔥 连续上涨 (连涨 5 根高位)" : "❄️ 连续下跌 (连跌 5 根低位)";
            decimal startP = trend.StartPrice;
            decimal fifthP = fifthBar.Close;
            decimal pct = startP > 0 ? (fifthP - startP) / startP * 100m : 0m;
            string sign = pct >= 0 ? "+" : "";

            activationMsg = $"[策略激活] ⚡ 检测到【{dirStr}】Bar #{trend.StartIndex} ~ #{fifthBarIndex} (启动价:{startP:F2} -> 第5根收盘:{fifthP:F2}, 累计:{sign}{pct:F2}%)！\n" +
                            $"[策略激活] 🎯 反转做单策略正式激活！以第 5 根收盘时刻 ({t0Dt:yyyy-MM-dd HH:mm:ss}) 开启第 1 个 {ObservationMinutes} 分钟观察期，开始接收 Tick 聚合小周期 K 线进行反转监控！";

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

            int fifthBarIndex = trend.StartIndex + 4; // 第 5 根 K 线的索引 (从 0 计数时 +4 即为第 5 根)
            if (currentBarIndex < fifthBarIndex || fifthBarIndex >= allKlines.Count)
            {
                return newSignals;
            }

            // 确保触发策略激活日志
            if (TryActivateStrategy(trend, allKlines, currentBarIndex, out var actMsg))
            {
                // 已在 TryActivateStrategy 内部触发 OnStrategyActivated
            }

            // 第 5 根 K 线收盘时刻作为第 1 个 3 分钟观察期的起点
            var fifthBar = allKlines[fifthBarIndex];
            long t0 = fifthBar.CloseTime;

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
                // 若该连续形态已在前面的观察期命中反转信号并做单，无需后续重复开单
                if (_allSignals.Any(s => s.TrendId == trend.Id && s.ObservationCycleIndex < cycleIndex))
                {
                    break;
                }

                long curCycleEnd = curCycleStart + cycleSpanMs - 1;
                string cycleKey = $"{trend.Id}_{cycleIndex}";

                RawKline? smallBar = null;

                // 1. 优先尝试从逐笔 Tick 聚合
                if (ticks != null && ticks.Count > 0)
                {
                    var cycleTicks = new List<RawTick>();
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

                // 2. 若当前无逐笔 Tick 数据，自动从 1m K 线数据源合并聚合为 3m 观察期 K 线
                if (smallBar == null)
                {
                    var sourceBars = fallback1mKlines ?? allKlines;
                    var subBars = new List<RawKline>();
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

                    if (isReversal && !_processedCycleKeys.Contains(cycleKey))
                    {
                        cycleObj.IsSignalTriggered = true;
                        _processedCycleKeys.Add(cycleKey);

                        int targetBigBarIndex = currentBarIndex;
                        for (int b = fifthBarIndex; b < allKlines.Count; b++)
                        {
                            if (sb.CloseTime >= allKlines[b].OpenTime && sb.CloseTime <= allKlines[b].CloseTime)
                            {
                                targetBigBarIndex = b;
                                break;
                            }
                        }

                        var signal = new ReversalOrderSignal
                        {
                            TrendId = trend.Id,
                            PriorTrendType = trend.Type,
                            ObservationCycleIndex = cycleIndex,
                            TriggerTime = sb.CloseTime,
                            Price = sb.Close,
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

                        string dirText = direction == OrderSignalDirection.Buy ? "🟢做多(Buy)" : "🔴做空(Sell)";
                        string trendDir = trend.Type == ConsecutiveTrendType.Bullish ? "连涨5根高位" : "连跌5根低位";
                        string notify = $"[反转做单信号] ⚡ 在第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 ({cStartDt:HH:mm:ss} ~ {cEndDt:HH:mm:ss}) 触发 {dirText}！前序:{trendDir}, 形态: [{pattern.PatternId}] {pattern.PatternName}, 入场价: {signal.Price:F2} USDT\n" +
                                        $"             💡 操盘逻辑: {pattern.SemanticLogic}";

                        OnReversalOrderSignal?.Invoke(signal, notify);
                        OnObservationCycleUpdated?.Invoke(cycleObj, $"第 {cycleIndex} 个 {ObservationMinutes}分钟观察期已满足反转做单条件 ({dirText})！已在 K线 #{targetBigBarIndex} 标记短黄线！");

                        // 满足反转做单条件，完成入场做单，退出观察循环
                        break;
                    }
                    else if (!isReversal && !_processedCycleKeys.Contains(cycleKey))
                    {
                        string trendDir = trend.Type == ConsecutiveTrendType.Bullish ? "连涨" : "连跌";
                        string reason = $"识别形态为 [{pattern.PatternId}] {pattern.PatternName} (开:{sb.Open:F2} 高:{sb.High:F2} 低:{sb.Low:F2} 收:{sb.Close:F2}, 实体幅:{pattern.BodyPct:F2}%)，未出现{trendDir}反转特征";
                        OnObservationCycleUpdated?.Invoke(cycleObj, $"[观察期推演] ℹ️ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 ({cStartDt:HH:mm:ss} ~ {cEndDt:HH:mm:ss}) {reason}，等待下个循环...");
                    }
                }
                else
                {
                    if (!_processedCycleKeys.Contains(cycleKey))
                    {
                        OnObservationCycleUpdated?.Invoke(cycleObj, $"[观察期推演] ⏳ 第 {cycleIndex} 个 {ObservationMinutes}分钟观察期 ({cStartDt:HH:mm:ss} ~ {cEndDt:HH:mm:ss}) 正在等待数据流推进...");
                    }
                    // 当前周期的 3 分钟尚未结束，退出等待后续帧推进
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

            // 确保激活策略
            if (TryActivateStrategy(trend, allKlines, currentBarIndex, out var actMsg))
            {
                // 已触发 OnStrategyActivated
            }

            long t0 = allKlines[fifthBarIndex].CloseTime;
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
