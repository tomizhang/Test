using Common;
using System;
using System.Collections.Generic;
using System.Linq;
using Test.PeriodTickPlayback.WinForms.Models;

namespace Test.PeriodTickPlayback.WinForms.Engine
{
    /// <summary>
    /// 角度趋势通道实时交易信号检测引擎 (零未来函数)
    /// 核心规则：
    /// 1. 当大周期连续涨跌通道形成 (满足设定根数且全部 Close 定型输出后) 立即激活进入观察期；
    /// 2. 进入观察期时，向前回溯提取最近 10,000 笔真实历史 Tick 并预聚合为 6 秒柱作为基准；
    /// 3. 观察期内实时接收 Tick 并按 6 秒进行动态聚合；
    /// 4. 当价格触及通道顶部且为局部高点时发出做空信号；触及通道底部且为局部低点时发出做多信号；
    /// 5. 信号标记点位严格锁定为触发该信号的原始实时 Tick，杜绝未来函数漂移。
    /// </summary>
    public class ChannelSignalDetector
    {
        private readonly PeriodTickPlaybackEngine _engine;
        private readonly List<ChannelSignalItem> _signals = new();
        private readonly object _lock = new();

        private ChannelObservationContext? _currentContext;
        private int _lastProcessedMacroBucket = -1;
        private int _signalIdCounter = 1;

        #region 参数配置

        public bool IsEnabled { get; set; } = true;
        public int ConsecutiveMinBars { get; set; } = 2; // 默认 2 根收涨/收跌后进入
        public decimal ConsecutiveMinPct { get; set; } = 0.0m; // 连续波段最低幅度门槛 (默认 0% 允许任意 2 根收盘连续)
        public int LookbackTicks { get; set; } = 10000; // 回溯历史 Tick 笔数 (预聚合 6 秒基准)
        public int AggregationSeconds { get; set; } = 6; // 微观聚合周期 (固定 6 秒)

        /// <summary>
        /// 连续两笔交易信号之间的最小安全时间间隔 (毫秒)，严格杜绝高频频繁报单 (默认 60 秒)
        /// </summary>
        public long MinSignalIntervalMs { get; set; } = 60_000L;

        /// <summary>
        /// 触碰通道轨线后局部冲顶/探底波段的极值吸附优化窗口 (默认 30 秒)
        /// 在此窗口内，若价格继续创出更高/更低点，静默微调吸附现有信号至最佳顶点 Tick，不重复产生新信号与刷屏日志
        /// 超过此窗口后，该触轨波段彻底定型锁定，不再挪动
        /// </summary>
        public long VertexRefineWindowMs { get; set; } = 30_000L;

        #endregion

        #region 事件通知

        /// <summary>
        /// 当产生新的通道顶底交易信号时触发 (写入交易日志并标记图表)
        /// </summary>
        public event Action<ChannelSignalItem>? OnSignalGenerated;

#pragma warning disable CS0067
        /// <summary>
        /// 当已有信号的顶点/低点打点坐标被动态优化吸附时触发 (保留向后兼容；当前策略已严格铆钉于首发 Tick，不再移位)
        /// </summary>
        public event Action<ChannelSignalItem>? OnSignalUpdated;
#pragma warning restore CS0067

        /// <summary>
        /// 观察期状态变化通知
        /// </summary>
        public event Action<string, System.Drawing.Color>? OnDetectorLog;

        #endregion

        public IReadOnlyList<ChannelSignalItem> Signals
        {
            get
            {
                lock (_lock)
                {
                    return _signals.ToArray();
                }
            }
        }

        public ChannelObservationContext? CurrentContext => _currentContext;

        public ChannelSignalDetector(PeriodTickPlaybackEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            // 订阅引擎事件
            _engine.OnMacroBarCompleted += HandleMacroBarCompleted;
            _engine.OnTickDispatched += HandleTickDispatched;
            _engine.OnPlaybackReset += Reset;
        }

        /// <summary>
        /// 当大周期 K 线定型输出时触发：检测是否形成连续涨跌通道
        /// 严格法则：只有当 K 线完全 Close 收盘输出后，才判定通道确立！
        /// </summary>
        private void HandleMacroBarCompleted(MacroKline finalKline, int bucketIdx, int totalBuckets)
        {
            if (!IsEnabled) return;

            var completedBars = _engine.CompletedMacroBars;
            if (completedBars == null || completedBars.Count < ConsecutiveMinBars) return;

            // 零未来函数扫描：仅基于已定型收盘的大周期 K 线列表扫描连续趋势
            var trends = MacroConsecutiveTrendDetector.ScanTrends(
                completedBars,
                formingBar: null, // 绝不包含未收盘的形成柱！
                minBars: ConsecutiveMinBars,
                minChangePct: ConsecutiveMinPct);

            if (trends.Count == 0) return;

            // 获取最新确立的波段
            var latestTrend = trends.Last();

            // 若最新波段的结束点正好是当前这根刚刚收盘的 K 线，且具备有效通道
            if (latestTrend.EndIndex == bucketIdx && latestTrend.HasChannel)
            {
                // 若这是一个新通道 (或与当前正在观察的通道起点不同、多空方向不同)
                if (_currentContext == null || _currentContext.Channel.StartIndex != latestTrend.StartIndex || _currentContext.Channel.Type != latestTrend.Type)
                {
                    EnterObservationPeriod(latestTrend, bucketIdx);
                }
                else
                {
                    // 若是同一个正在连续推进延长的通道，更新通道几何参数但保持观察期上下文连续推进
                    _currentContext.Channel = latestTrend;
                }
            }

            _lastProcessedMacroBucket = bucketIdx;
        }

        /// <summary>
        /// 正式进入观察期：提取历史 10,000 笔 Tick 并进行 6 秒预聚合
        /// </summary>
        private void EnterObservationPeriod(MacroConsecutiveTrendItem trend, int confirmedBarIndex)
        {
            lock (_lock)
            {
                // 🌟 注意：历史信号必须持续保留在列表中，以便在图表全貌与回放时完整展现历史标记！
                // 信号检测器当前仅激活最新通道为活跃上下文，实时 Tick 仅对最新通道进行顶底判定。

                // 1. 向前追溯提取当前时刻前最多 10,000 笔真实已发生的历史 Tick (严格零未来函数)
                var historyTicks = _engine.GetRecentPlayedTicks(LookbackTicks);

                // 2. 将这 10,000 笔历史 Tick 预聚合为 6 秒微观 K 线基准序列
                var preWarmedBars = AggregateTicksTo6sBars(historyTicks, AggregationSeconds);

                // 3. 计算预聚合基准的极值与当前时刻价格位置
                decimal maxH = decimal.MinValue;
                decimal minL = decimal.MaxValue;
                foreach (var bar in preWarmedBars)
                {
                    if (bar.High > maxH) maxH = bar.High;
                    if (bar.Low < minL) minL = bar.Low;
                }

                double initX = confirmedBarIndex + 1.0;
                decimal initUpper = trend.SlopeK * (decimal)initX + trend.UpperIntercept;
                decimal initLower = trend.SlopeK * (decimal)initX + trend.LowerIntercept;
                decimal lastPrice = historyTicks.Count > 0 ? historyTicks[^1].Price : trend.EndPrice;
                bool initBelowLower = lastPrice < initLower;
                bool initAboveUpper = lastPrice > initUpper;

                // 4. 构建新的观察期上下文
                _currentContext = new ChannelObservationContext
                {
                    Channel = trend,
                    ConfirmedMacroBarIndex = confirmedBarIndex,
                    IsActive = true,
                    PreWarmed6sBars = preWarmedBars,
                    Observation6sBars = new List<SubBar6s>(500),
                    Current6sBar = null,
                    ShortCooldown = false,
                    LongCooldown = false,
                    PriceWasBelowLower = initBelowLower,
                    PriceWasAboveUpper = initAboveUpper,
                    LastTickPrice = lastPrice,
                    RecentMaxHigh = maxH,
                    RecentMinLow = minL
                };

                string dirStr = trend.IsBullish ? "连续上涨" : "连续下跌";
                OnDetectorLog?.Invoke(
                    $"[进入观察期] {dirStr}通道形成(第#{trend.StartIndex}~#{trend.EndIndex}根Close定型) -> 成功回溯获取 {historyTicks.Count:N0} 笔Tick预聚合 {preWarmedBars.Count} 根6秒柱！",
                    trend.IsBullish ? System.Drawing.Color.FromArgb(52, 211, 153) : System.Drawing.Color.FromArgb(248, 113, 113));
            }
        }

        /// <summary>
        /// 当实时 Tick 派发时触发：驱动 6 秒聚合与动态通道顶底信号判定
        /// </summary>
        private void HandleTickDispatched(RawTick tick, FormingMacroKline formingBar, int curTickIdx, int totalTicks)
        {
            if (!IsEnabled || _currentContext == null || !_currentContext.IsActive) return;

            lock (_lock)
            {
                var ctx = _currentContext;
                var ch = ctx.Channel;

                // 1. 驱动 6 秒微观聚合器 (基于 6000ms 时间戳窗口对齐)
                long tickMs = tick.Time;
                long windowMs = AggregationSeconds * 1000L;
                long bucketMs = (tickMs / windowMs) * windowMs;

                if (ctx.Current6sBar == null)
                {
                    ctx.Current6sBar = new SubBar6s(tick, bucketMs);
                }
                else if (bucketMs == ctx.Current6sBar.BucketMs)
                {
                    ctx.Current6sBar.UpdateWithTick(tick);
                }
                else if (bucketMs > ctx.Current6sBar.BucketMs)
                {
                    // 前一根 6s 柱收盘定型，压入观察期历史列表
                    var closedBar = ctx.Current6sBar;
                    ctx.Observation6sBars.Add(closedBar);

                    // 动态更新近端极值
                    if (closedBar.High > ctx.RecentMaxHigh) ctx.RecentMaxHigh = closedBar.High;
                    if (closedBar.Low < ctx.RecentMinLow) ctx.RecentMinLow = closedBar.Low;

                    // 开启新的 6s 柱
                    ctx.Current6sBar = new SubBar6s(tick, bucketMs);
                }

                // 2. 检查通道向前投影是否超期 (超过 15 根 K 线则自动结束该观察期)
                int curMacroIndex = formingBar.BucketIndex;
                if (curMacroIndex > ch.EndIndex + 15)
                {
                    ctx.IsActive = false;
                    return;
                }

                double progress = totalTicks > 0 ? (double)curTickIdx / totalTicks : 0.0;
                double xMacro = curMacroIndex + Math.Clamp(progress, 0.0, 0.999);

                decimal slopeK = ch.SlopeK;
                decimal upperB = ch.UpperIntercept;
                decimal lowerB = ch.LowerIntercept;

                decimal currentUpper = slopeK * (decimal)xMacro + upperB;
                decimal currentLower = slopeK * (decimal)xMacro + lowerB;
                decimal currentMid = (currentUpper + currentLower) / 2.0m;

                // 3. 信号检测 (严格零未来函数，标记当前触发的实时 Tick)
                // 用户明确法则：当下跌通道时候做多，上涨通道做空
                // 🌟 核心优化法则（用户要求）：
                //   1. 下降通道：价格必须是由通道内部从上下跌触碰下通道线才做多！
                //      - 若价格是从下上涨接触下通道线（跌破下轨后反抽），坚决不做单！
                //      - 若价格是从上下跌接触上通道线（冲破上轨后回踩），坚决不做单！
                //   2. 上升通道：价格必须是由通道内部从下上涨触碰上通道线才做空！
                //      - 若价格是从上下跌接触上通道线（冲破上轨后回踩），坚决不做单！
                //      - 若价格是从下上涨接触下通道线（跌破下轨后反抽），坚决不做单！
                decimal price = tick.Price;
                decimal prevPrice = ctx.LastTickPrice > 0 ? ctx.LastTickPrice : price;

                // 记录进入本 Tick 前的通道越界状态
                bool wasBelowLower = ctx.PriceWasBelowLower;
                bool wasAboveUpper = ctx.PriceWasAboveUpper;

                // 动态更新通道穿越状态机：
                if (price > currentUpper)
                {
                    ctx.PriceWasAboveUpper = true; // 冲破上轨，处于通道外部上方
                }
                else if (price <= currentMid)
                {
                    // 价格充分回落至通道中轨及以下，彻底回归通道内，重置上轨越界标记与做空冷却
                    ctx.PriceWasAboveUpper = false;
                    ctx.ShortCooldown = false;
                }

                if (price < currentLower)
                {
                    ctx.PriceWasBelowLower = true; // 跌破下轨，处于通道外部下方
                }
                else if (price >= currentMid)
                {
                    // 价格充分反弹至通道中轨及以上，彻底回归通道内，重置下轨越界标记与做多冷却
                    ctx.PriceWasBelowLower = false;
                    ctx.LongCooldown = false;
                }

                // --- 3.1 上涨通道 (Bullish)：只在通道内部由下向上冲上顶部且为局部高点时 -> 做空 ---
                if (ch.IsBullish)
                {
                    // 1. 通道顶部时 (现价触及通道动态上轨，容差 0.02%)
                    bool isAtTop = price >= (currentUpper * 0.9998m);
                    // 2. 为局部高点时 (现价创出当前 6s 聚合柱近端最高点)
                    bool is6sHigh = ctx.Current6sBar == null || price >= ctx.Current6sBar.High;

                    // 3. 🌟 核心方向法则（用户要求）：
                    //    必须是在通道内部由下往上涨到达上轨！
                    //    如果价格此前冲破上轨现在从上下跌接触上轨，坚决不做单！
                    bool isRisingFromBelowToTop = !wasAboveUpper && (price >= prevPrice || (ctx.Current6sBar != null && price >= ctx.Current6sBar.Open));

                    // 🌟 核心防重：检查当前大周期 K 线 (curMacroIndex) 是否已有信号
                    ChannelSignalItem? existingSigInBar = (_signals.Count > 0 && _signals[^1].MacroBarIndex == curMacroIndex)
                        ? _signals[^1]
                        : _signals.LastOrDefault(s => s.MacroBarIndex == curMacroIndex);

                    if (existingSigInBar == null)
                    {
                        // 尚未产生信号：严格检查触顶与由下往上涨的入轨方向
                        if (isAtTop && is6sHigh && isRisingFromBelowToTop)
                        {
                            bool timeIntervalOk = ctx.LastSignalTimeMs <= 0 || (tick.Time - ctx.LastSignalTimeMs) >= MinSignalIntervalMs;
                            if (!ctx.ShortCooldown && timeIntervalOk)
                            {
                                var shortSignal = new ChannelSignalItem
                                {
                                    Id = _signalIdCounter++,
                                    ChannelId = ch.Id,
                                    Type = ChannelSignalType.Short,
                                    TriggerTick = tick, // 🌟 严格打点于触发信号的实时真实 Tick
                                    TriggerPrice = price,
                                    TriggerTimeMs = tick.Time,
                                    InitialTriggerTimeMs = tick.Time,
                                    TriggerTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).UtcDateTime,
                                    MacroBarIndex = curMacroIndex,
                                    MacroProgress = progress,
                                    TickIndexInBucket = Math.Max(0, curTickIdx - 1),
                                    ChannelUpper = currentUpper,
                                    ChannelLower = currentLower,
                                    ChannelSlope = slopeK,
                                    Note = $"上涨通道顶部做空 (现价:{price:F2} >= 上轨:{currentUpper:F2}, 通道内由下向上冲顶触轨)"
                                };

                                _signals.Add(shortSignal);
                                ctx.ShortCooldown = true; // 进入做空冷却
                                ctx.LastSignalBucketMs = bucketMs;
                                ctx.LastSignalTimeMs = tick.Time;

                                OnSignalGenerated?.Invoke(shortSignal);
                                OnDetectorLog?.Invoke(
                                    $"[做空信号 ▼] {DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).LocalDateTime:HH:mm:ss} 现价:{price:F2} 由下向上触碰上涨通道上轨(上轨:{currentUpper:F2}) 创出局部最高点 -> 做空！",
                                    System.Drawing.Color.FromArgb(250, 204, 21)); // 醒目金黄
                            }
                        }
                    }
                    else
                    {
                        // 🌟 核心防重与零未来函数：当前 K 线内已经存在信号！
                        // 信号一旦初次触发，必须永久“铆钉”在最初触发的实时 Tick 上，严禁后续移位或篡改坐标，杜绝未来函数漂移！
                    }
                }
                // --- 3.2 下跌通道 (Bearish)：只在通道内部由上向下探至底部且为局部低点时 -> 做多 ---
                else
                {
                    // 1. 通道底部时 (现价触及通道动态下轨，容差 0.02%)
                    bool isAtBottom = price <= (currentLower * 1.0002m);
                    // 2. 为局部低点时 (现价创出当前 6s 聚合柱近端最低点)
                    bool is6sLow = ctx.Current6sBar == null || price <= ctx.Current6sBar.Low;

                    // 3. 🌟 核心方向法则（用户要求）：
                    //    必须是在通道内部由上往下跌到达下轨！
                    //    如果价格此前跌破下轨现在从下上涨接触下轨，坚决不做单！
                    bool isFallingFromAboveToBottom = !wasBelowLower && (price <= prevPrice || (ctx.Current6sBar != null && price <= ctx.Current6sBar.Open));

                    // 🌟 核心防重：检查当前大周期 K 线 (curMacroIndex) 是否已有信号
                    ChannelSignalItem? existingSigInBar = (_signals.Count > 0 && _signals[^1].MacroBarIndex == curMacroIndex)
                        ? _signals[^1]
                        : _signals.LastOrDefault(s => s.MacroBarIndex == curMacroIndex);

                    if (existingSigInBar == null)
                    {
                        // 尚未产生信号：严格检查触底与由上往下跌的入轨方向
                        if (isAtBottom && is6sLow && isFallingFromAboveToBottom)
                        {
                            bool timeIntervalOk = ctx.LastSignalTimeMs <= 0 || (tick.Time - ctx.LastSignalTimeMs) >= MinSignalIntervalMs;
                            if (!ctx.LongCooldown && timeIntervalOk)
                            {
                                var longSignal = new ChannelSignalItem
                                {
                                    Id = _signalIdCounter++,
                                    ChannelId = ch.Id,
                                    Type = ChannelSignalType.Long,
                                    TriggerTick = tick, // 🌟 严格打点于触发信号的实时真实 Tick
                                    TriggerPrice = price,
                                    TriggerTimeMs = tick.Time,
                                    InitialTriggerTimeMs = tick.Time,
                                    TriggerTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).UtcDateTime,
                                    MacroBarIndex = curMacroIndex,
                                    MacroProgress = progress,
                                    TickIndexInBucket = Math.Max(0, curTickIdx - 1),
                                    ChannelUpper = currentUpper,
                                    ChannelLower = currentLower,
                                    ChannelSlope = slopeK,
                                    Note = $"下跌通道底部做多 (现价:{price:F2} <= 下轨:{currentLower:F2}, 通道内由上向下探底触轨)"
                                };

                                _signals.Add(longSignal);
                                ctx.LongCooldown = true; // 进入做多冷却
                                ctx.LastSignalBucketMs = bucketMs;
                                ctx.LastSignalTimeMs = tick.Time;

                                OnSignalGenerated?.Invoke(longSignal);
                                OnDetectorLog?.Invoke(
                                    $"[做多信号 ▲] {DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).LocalDateTime:HH:mm:ss} 现价:{price:F2} 由上向下触碰下跌通道下轨(下轨:{currentLower:F2}) 创出局部最低点 -> 做多！",
                                    System.Drawing.Color.FromArgb(250, 204, 21)); // 醒目金黄
                            }
                        }
                    }
                    else
                    {
                        // 🌟 核心防重与零未来函数：当前 K 线内已经存在信号！
                        // 信号一旦初次触发，必须永久“铆钉”在最初触发的实时 Tick 上，严禁后续移位或篡改坐标，杜绝未来函数漂移！
                    }
                }

                // 记录本笔 Tick 价格作为下笔参考
                ctx.LastTickPrice = price;
            }
        }

        /// <summary>
        /// 将指定 Tick 列表按 6 秒固定时间切片聚合为微观 6 秒柱列表
        /// </summary>
        public static List<SubBar6s> AggregateTicksTo6sBars(IReadOnlyList<RawTick> ticks, int aggregationSeconds = 6)
        {
            var bars = new List<SubBar6s>();
            if (ticks == null || ticks.Count == 0) return bars;

            long windowMs = aggregationSeconds * 1000L;
            SubBar6s? currentBar = null;

            for (int i = 0; i < ticks.Count; i++)
            {
                var t = ticks[i];
                long bucketMs = (t.Time / windowMs) * windowMs;

                if (currentBar == null)
                {
                    currentBar = new SubBar6s(t, bucketMs);
                }
                else if (bucketMs == currentBar.BucketMs)
                {
                    currentBar.UpdateWithTick(t);
                }
                else if (bucketMs > currentBar.BucketMs)
                {
                    bars.Add(currentBar);
                    currentBar = new SubBar6s(t, bucketMs);
                }
            }

            if (currentBar != null)
            {
                bars.Add(currentBar);
            }

            return bars;
        }

        /// <summary>
        /// 重置清空检测器状态 (切换币种、重新加载时调用)
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _signals.Clear();
                _currentContext = null;
                _lastProcessedMacroBucket = -1;
                _signalIdCounter = 1;
            }
        }
    }
}
