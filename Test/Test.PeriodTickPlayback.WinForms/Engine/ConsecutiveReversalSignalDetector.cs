using Common;
using System;
using System.Collections.Generic;
using System.Drawing;
using Test.PeriodTickPlayback.WinForms.Models;

namespace Test.PeriodTickPlayback.WinForms.Engine
{
    /// <summary>
    /// 连续形态反转二次探顶/探底交易信号检测引擎 (零未来函数)
    /// 
    /// 核心规则：
    /// 1. 【做空逻辑】：
    ///    - 当大周期出现连续上涨 N 条 K 线 (连阳，默认 4 条，参数可调)；
    ///    - 在大周期结束后的第 1 个小周期 (默认 3 分钟，参数可调) 结束时，若收阴线确认反转，则正式进入观察期；
    ///    - 在第 2 个小周期 (第 3~6 分钟) 进行中，若价格达到连续上涨高点附近 (容差可调，默认 ±0.2%)，立即发出做空信号 (▼)！
    /// 2. 【做多逻辑】(对称镜像法则)：
    ///    - 当大周期出现连续下跌 N 条 K 线 (连阴，默认 4 条，参数可调)；
    ///    - 在大周期结束后的第 1 个小周期 (默认 3 分钟，参数可调) 结束时，若收阳线确认反转，则正式进入观察期；
    ///    - 在第 2 个小周期 (第 3~6 分钟) 进行中，若价格达到连续下跌低点附近 (容差可调，默认 ±0.2%)，立即发出做多信号 (▲)！
    /// 3. 信号触发后严格永久“铆钉”在首发实时 Tick 上，在 K 线图表和 Tick 图表中均使用黄色短十字架 (#facc15) 标记；
    /// 4. 连续 K 线根数与反转小周期时间均支持参数调节并持久化。
    /// </summary>
    public class ConsecutiveReversalSignalDetector
    {
        private readonly PeriodTickPlaybackEngine _engine;
        private readonly List<ChannelSignalItem> _signals = new();
        private readonly object _lock = new();

        private ConsecutiveReversalContext? _currentContext;
        private int _lastDetectedMacroBarIndex = -1;
        private int _signalIdCounter = 20001;

        #region 参数配置

        /// <summary>
        /// 策略总开关 (默认开启)
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 连续大周期 K 线根数门槛 (默认 4 根)
        /// </summary>
        public int ConsecutiveBars { get; set; } = 4;

        /// <summary>
        /// 向后兼容属性：连续上涨大周期 K 线根数门槛
        /// </summary>
        public int ConsecutiveBullishBars { get => ConsecutiveBars; set => ConsecutiveBars = value; }

        /// <summary>
        /// 反转确认与二次探顶/探底小周期跨度 (默认 3 分钟)
        /// </summary>
        public int ReversalSubPeriodMinutes { get; set; } = 3;

        /// <summary>
        /// 探顶前高 / 探底前低容差百分比 (默认 ±0.2%)
        /// </summary>
        public decimal TolerancePct { get; set; } = 0.2m;

        /// <summary>
        /// 向后兼容属性：探顶前高容差百分比 (默认 ±0.2%)
        /// </summary>
        public decimal HighTolerancePct { get => TolerancePct; set => TolerancePct = value; }

        /// <summary>
        /// 冲高回落 / 探底回升确认幅度百分比 (默认 0.05%)
        /// </summary>
        public decimal PullbackPct { get; set; } = 0.05m;

        /// <summary>
        /// 无明显信号时最大允许顺延观察期轮数 (默认 5 轮小周期)
        /// </summary>
        public int MaxObservationPeriods { get; set; } = 5;

        /// <summary>
        /// 小十字星/小十字架形态实体占振幅比例上限 (默认 0.25，即 25%)
        /// </summary>
        public decimal DojiBodyRatioMax { get; set; } = 0.25m;

        /// <summary>
        /// 小十字星/小十字架形态实体最大涨跌幅百分比 (默认 0.15%)
        /// </summary>
        public decimal DojiMaxChangePct { get; set; } = 0.15m;

        /// <summary>
        /// 连续大周期 K 线序列中十字星允许占的最大比例上限 (默认 0.5，即不超过 50%，十字星不能占大部分)
        /// </summary>
        public decimal ConsecutiveMaxDojiRatio { get; set; } = 0.5m;

        /// <summary>
        /// 反转 K 线单根最大涨跌幅百分比上限 (默认 1.0%，超过则判定反转K线太大跳过)
        /// </summary>
        public decimal MaxReversalBarChangePct { get; set; } = 1.0m;

        /// <summary>
        /// 反转 K 线相对连续波段回撤比例上限百分比 (默认 50.0%，超过则判定反转K线太大跳过)
        /// </summary>
        public decimal MaxReversalRetracePct { get; set; } = 50.0m;

        /// <summary>
        /// 连续大周期 K 线波段最低幅度百分比门槛 (默认 1.0%，连涨取 (最高价 - 起点开盘)/起点开盘 * 100%，连跌取 (起点开盘 - 最低价)/起点开盘 * 100%，低于此门槛不确立形态)
        /// </summary>
        public decimal MinConsecutiveTrendPct { get; set; } = 1.0m;

        /// <summary>
        /// 触发信号后的顶点波段吸附优化窗口 (保留向后兼容)
        /// </summary>
        public long VertexRefineWindowMs { get; set; } = 30_000L;

        /// <summary>
        /// 判断给定 K 线的开高低收是否构成“小十字星 / 小十字架”形态 (零未来函数)
        /// 判定依据：
        /// 1. 价格必须为有效正数且全振幅大于 0 (具备上下影线区间，杜绝未初始化的全0假K线)；
        /// 2. 实体高度 / 全振幅高度 &lt;= DojiBodyRatioMax (默认 25%，影线占据 75% 以上，呈现经典十字架形态)；
        /// 3. 实体的绝对涨跌幅 &lt;= DojiMaxChangePct (默认 0.15%，价格波动极其微弱)。
        /// </summary>
        public bool IsDojiCrossBar(decimal open, decimal high, decimal low, decimal close)
        {
            if (open <= 0m || high <= 0m || low <= 0m || close <= 0m || high <= low)
            {
                return false;
            }

            decimal body = Math.Abs(close - open);
            decimal range = high - low;

            decimal bodyRatio = body / range;
            decimal changePct = (body / open) * 100m;

            return bodyRatio <= DojiBodyRatioMax && changePct <= DojiMaxChangePct;
        }

        #endregion

        #region 事件通知

        /// <summary>
        /// 当产生新的连阳反转做空交易信号时触发 (写入交易日志并标记图表)
        /// </summary>
        public event Action<ChannelSignalItem>? OnSignalGenerated;

#pragma warning disable CS0067
        /// <summary>
        /// 当已有信号的顶点打点坐标被动态优化吸附时触发 (保留向后兼容；当前策略已严格铆钉于首发 Tick，不再移位)
        /// </summary>
        public event Action<ChannelSignalItem>? OnSignalUpdated;
#pragma warning restore CS0067

        /// <summary>
        /// 观察期状态变化通知
        /// </summary>
        public event Action<string, Color>? OnDetectorLog;

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

        public ConsecutiveReversalContext? CurrentContext => _currentContext;

        public ConsecutiveReversalSignalDetector(PeriodTickPlaybackEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            _engine.OnMacroBarCompleted += HandleMacroBarCompleted;
            _engine.OnTickDispatched += HandleTickDispatched;
            _engine.OnPlaybackReset += Reset;
        }

        /// <summary>
        /// 重置检测器运行状态
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _signals.Clear();
            }
            _currentContext = null;
            _lastDetectedMacroBarIndex = -1;
            _signalIdCounter = 20001;
        }

        /// <summary>
        /// 当大周期 K 线定型输出时触发：检测是否出现连续上涨或连续下跌 N 条 K 线
        /// 严格零未来函数：仅在 K 线完全 Close 收盘输出后评估
        /// </summary>
        private void HandleMacroBarCompleted(MacroKline finalKline, int bucketIdx, int totalBuckets)
        {
            if (!IsEnabled) return;

            var completedBars = _engine.CompletedMacroBars;
            int n = ConsecutiveBars;
            if (completedBars == null || completedBars.Count < n)
            {
                return;
            }

            int count = completedBars.Count;
            int startIdx = count - n;

            // 1.1 严格检查过去 N 根大周期 K 线是否全为连续上涨 (允许小十字星阴线作为休整，不打断连涨趋势)
            bool isConsecutiveBullish = true;
            decimal maxHigh = decimal.MinValue;
            int solidBullishBarCount = 0;
            int bullishDojiBarCount = 0;

            for (int i = startIdx; i < count; i++)
            {
                var bar = completedBars[i];
                bool isDoji = IsDojiCrossBar(bar.Open, bar.High, bar.Low, bar.Close);
                bool isSolidBullish = bar.Close > bar.Open && !isDoji;

                if (isDoji)
                {
                    bullishDojiBarCount++;
                }
                else if (isSolidBullish)
                {
                    solidBullishBarCount++;
                }

                // 规则：若既不是上涨阳线，也不是小十字星(如下跌实体阴线)，则视为趋势中断
                if (!isSolidBullish && !isDoji)
                {
                    isConsecutiveBullish = false;
                    break;
                }

                // 若收盘价明显低于前一根收盘价且非小十字星，则视为回调中断
                if (i > startIdx && bar.Close < completedBars[i - 1].Close && !isDoji)
                {
                    isConsecutiveBullish = false;
                    break;
                }

                if (bar.High > maxHigh)
                {
                    maxHigh = bar.High;
                }
            }

            // 规则约束：
            // 1. 不能都是十字架类型的 K 线 (bullishDojiBarCount < n, 至少有实体阳线支撑)
            // 2. 十字星不能占连续 K 线的大部分 (bullishDojiBarCount / n <= ConsecutiveMaxDojiRatio)
            // 3. 必须有至少1根实体阳线支撑，且末根收盘必须高于首根开盘(整体为净上涨波段)
            bool isBullishAllDoji = bullishDojiBarCount >= n;
            bool isBullishMajorityDoji = (decimal)bullishDojiBarCount / n > ConsecutiveMaxDojiRatio;
            decimal startOpen = completedBars[startIdx].Open;
            if (isBullishAllDoji || isBullishMajorityDoji || solidBullishBarCount < 1 || completedBars[count - 1].Close <= startOpen)
            {
                isConsecutiveBullish = false;
            }

            decimal bullishTrendPct = (isConsecutiveBullish && startOpen > 0m && maxHigh > decimal.MinValue)
                ? ((maxHigh - startOpen) / startOpen) * 100m
                : 0m;

            // 4. 🌟 最低波段幅度要求：连续上涨波段幅度必须达到门槛 (默认 1.0%，低于此门槛不确立形态)
            if (isConsecutiveBullish && MinConsecutiveTrendPct > 0m && bullishTrendPct < MinConsecutiveTrendPct)
            {
                isConsecutiveBullish = false;
            }

            // 1.2 严格检查过去 N 根大周期 K 线是否全为连续下跌 (允许小十字星阳线作为休整，不打断连跌趋势)
            bool isConsecutiveBearish = true;
            decimal minLow = decimal.MaxValue;
            int solidBearishBarCount = 0;
            int bearishDojiBarCount = 0;

            for (int i = startIdx; i < count; i++)
            {
                var bar = completedBars[i];
                bool isDoji = IsDojiCrossBar(bar.Open, bar.High, bar.Low, bar.Close);
                bool isSolidBearish = bar.Close < bar.Open && !isDoji;

                if (isDoji)
                {
                    bearishDojiBarCount++;
                }
                else if (isSolidBearish)
                {
                    solidBearishBarCount++;
                }

                // 规则：若既不是下跌阴线，也不是小十字星(如上涨实体阳线)，则视为趋势中断
                if (!isSolidBearish && !isDoji)
                {
                    isConsecutiveBearish = false;
                    break;
                }

                // 若收盘价明显高于前一根收盘价且非小十字星，则视为反弹中断
                if (i > startIdx && bar.Close > completedBars[i - 1].Close && !isDoji)
                {
                    isConsecutiveBearish = false;
                    break;
                }

                if (bar.Low < minLow)
                {
                    minLow = bar.Low;
                }
            }

            // 规则约束：
            // 1. 不能都是十字架类型的 K 线 (bearishDojiBarCount < n, 至少有实体阴线支撑)
            // 2. 十字星不能占连续 K 线的大部分 (bearishDojiBarCount / n <= ConsecutiveMaxDojiRatio)
            // 3. 必须有至少1根实体阴线支撑，且末根收盘必须低于首根开盘(整体为净下跌波段)
            bool isBearishAllDoji = bearishDojiBarCount >= n;
            bool isBearishMajorityDoji = (decimal)bearishDojiBarCount / n > ConsecutiveMaxDojiRatio;

            if (isBearishAllDoji || isBearishMajorityDoji || solidBearishBarCount < 1 || completedBars[count - 1].Close >= startOpen)
            {
                isConsecutiveBearish = false;
            }

            decimal bearishTrendPct = (isConsecutiveBearish && startOpen > 0m && minLow < decimal.MaxValue)
                ? ((startOpen - minLow) / startOpen) * 100m
                : 0m;

            // 4. 🌟 最低波段幅度要求：连续下跌波段幅度必须达到门槛 (默认 1.0%，低于此门槛不确立形态)
            if (isConsecutiveBearish && MinConsecutiveTrendPct > 0m && bearishTrendPct < MinConsecutiveTrendPct)
            {
                isConsecutiveBearish = false;
            }

            if (!isConsecutiveBullish && !isConsecutiveBearish)
            {
                return;
            }

            // 杜绝同一根大周期 K 线重复触发形态确立
            if (_lastDetectedMacroBarIndex == finalKline.BarIndex)
            {
                return;
            }
            _lastDetectedMacroBarIndex = finalKline.BarIndex;

            // 2. 提取大周期结束时间戳 T0
            long macroEndMs = finalKline.CloseTimeMs;
            if (macroEndMs <= 0 && finalKline.CloseTime != default)
            {
                macroEndMs = ((DateTimeOffset)finalKline.CloseTime).ToUnixTimeMilliseconds();
            }
            if (macroEndMs <= 0)
            {
                return;
            }

            // 3. 构建观察上下文，等待第 1 个小周期 [T0, T0 + SubPeriodMs) 收盘确认
            long subPeriodMs = (long)ReversalSubPeriodMinutes * 60 * 1000L;
            var direction = isConsecutiveBullish ? ConsecutiveReversalDirection.Short : ConsecutiveReversalDirection.Long;
            decimal benchmarkPrice = isConsecutiveBullish ? maxHigh : minLow;
            decimal trendSpan = isConsecutiveBullish
                ? (maxHigh - startOpen)
                : (startOpen - minLow);
            if (trendSpan <= 0m)
            {
                trendSpan = benchmarkPrice * 0.005m;
            }
            decimal trendPct = isConsecutiveBullish ? bullishTrendPct : bearishTrendPct;

            _currentContext = new ConsecutiveReversalContext
            {
                Id = bucketIdx,
                Direction = direction,
                MacroBarIndex = finalKline.BarIndex,
                MacroEndMs = macroEndMs,
                ConsecutiveBarCount = n,
                BenchmarkPrice = benchmarkPrice,
                TrendSpan = trendSpan,
                TrendPct = trendPct,
                SubPeriodMs = subPeriodMs,
                State = ConsecutiveReversalState.PendingSubPeriod1,
                CurrentObservationPeriodIndex = 1,
                CurrentObservationStartMs = macroEndMs + subPeriodMs,
                CurrentObservationEndMs = macroEndMs + 2 * subPeriodMs,
                CurrentObservationBar = new SubPeriodBar(macroEndMs + subPeriodMs, macroEndMs + 2 * subPeriodMs),
                SubPeriod1 = new SubPeriodBar(macroEndMs, macroEndMs + subPeriodMs)
            };

            if (direction == ConsecutiveReversalDirection.Short)
            {
                OnDetectorLog?.Invoke(
                    $"[连阳形态确立] 连续上涨 {n} 根大周期K线达成 (实体阳线:{solidBullishBarCount}根, 休整十字星:{bullishDojiBarCount}根, Bar #{startIdx} ~ #{finalKline.BarIndex}，波段幅度:{bullishTrendPct:F2}% (门槛:{MinConsecutiveTrendPct:F2}%), 区间高点:{maxHigh:F2}) -> 等待第1个{ReversalSubPeriodMinutes}分钟周期结束确认反转(收阴)...",
                    Color.FromArgb(56, 189, 248)); // 天蓝
            }
            else
            {
                OnDetectorLog?.Invoke(
                    $"[连阴形态确立] 连续下跌 {n} 根大周期K线达成 (实体阴线:{solidBearishBarCount}根, 休整十字星:{bearishDojiBarCount}根, Bar #{startIdx} ~ #{finalKline.BarIndex}，波段幅度:{bearishTrendPct:F2}% (门槛:{MinConsecutiveTrendPct:F2}%), 区间低点:{minLow:F2}) -> 等待第1个{ReversalSubPeriodMinutes}分钟周期结束确认反转(收阳)...",
                    Color.FromArgb(244, 114, 182)); // 珊瑚粉
            }
        }

        private void HandleTickDispatched(RawTick tick, FormingMacroKline formingBar, int curTickIndex, int totalTicksInBucket)
        {
            if (!IsEnabled || _currentContext == null) return;

            int curMacroIndex = formingBar?.BucketIndex ?? 0;
            var ctx = _currentContext;
            if (ctx.State == ConsecutiveReversalState.None ||
                ctx.State == ConsecutiveReversalState.Expired ||
                ctx.State == ConsecutiveReversalState.Completed)
            {
                return;
            }

            long t = tick.Time;
            long t0 = ctx.MacroEndMs;
            long dt = ctx.SubPeriodMs;

            // ================= 阶段 1: 正在聚合第 1 个小周期 [T0, T0 + dt) =================
            if (ctx.State == ConsecutiveReversalState.PendingSubPeriod1)
            {
                if (t < t0)
                {
                    return;
                }

                if (t < t0 + dt)
                {
                    ctx.SubPeriod1.UpdateWithTick(tick);
                    return;
                }

                // 此时当前 Tick 毫秒时间已到达或超过第 1 个小周期结束时间 (t >= T0 + dt)
                // 评估第 1 个小周期是否“出现有效反转”：
                // 1. 做空方向 (连阳发起)：收阴线 Close < Open (ctx.SubPeriod1.IsBearish)
                // 2. 做多方向 (连阴发起)：收阳线 Close > Open (ctx.SubPeriod1.IsBullish)
                // 3. 核心优化：若第 1 个小周期收出的是“小十字星”(振幅内实体微弱)，多空仍胶着，并未形成决定性反转动能，
                //    因此“小十字星不做为反转条件”，不予确认反转！
                bool isSubPeriod1Doji = ctx.SubPeriod1.HasTicks && IsDojiCrossBar(ctx.SubPeriod1.Open, ctx.SubPeriod1.High, ctx.SubPeriod1.Low, ctx.SubPeriod1.Close);
                bool isDirectionReversed = ctx.IsShort ? ctx.SubPeriod1.IsBearish : ctx.SubPeriod1.IsBullish;
                bool isReversalConfirmed = ctx.SubPeriod1.HasTicks && isDirectionReversed && !isSubPeriod1Doji;

                if (isReversalConfirmed)
                {
                    // 🌟 核心优化：反转 K 线太大则跳过 (双重限制：单根绝对涨跌幅 与 相对连续波段回撤比例)
                    decimal reversalChangePct = Math.Abs(ctx.SubPeriod1.ChangePct);
                    decimal reversalPoints = Math.Abs(ctx.SubPeriod1.Close - ctx.SubPeriod1.Open);
                    decimal retracePct = ctx.TrendSpan > 0m ? (reversalPoints / ctx.TrendSpan) * 100m : 0m;

                    bool isTooLargeByChange = MaxReversalBarChangePct > 0m && reversalChangePct > MaxReversalBarChangePct;
                    bool isTooLargeByRetrace = MaxReversalRetracePct > 0m && retracePct > MaxReversalRetracePct;

                    if (isTooLargeByChange || isTooLargeByRetrace)
                    {
                        ctx.State = ConsecutiveReversalState.Expired;
                        string detail = isTooLargeByChange && isTooLargeByRetrace
                            ? $"单根幅值 {reversalChangePct:F2}% (上限 {MaxReversalBarChangePct:F2}%) 且波段回撤 {retracePct:F1}% (上限 {MaxReversalRetracePct:F1}%)"
                            : (isTooLargeByChange
                                ? $"单根幅值 {reversalChangePct:F2}% 超过上限 {MaxReversalBarChangePct:F2}%"
                                : $"波段回撤 {retracePct:F1}% 超过上限 {MaxReversalRetracePct:F1}%");

                        string trendName = ctx.IsShort ? "上涨反转阴线" : "下跌反转阳线";
                        OnDetectorLog?.Invoke(
                            $"[形态跳过] {trendName}过大 ({detail})，反转剧烈不利于二次探顶/探底，跳过本轮观察。",
                            Color.FromArgb(148, 163, 184));
                        return;
                    }

                    // 反转确立！正式进入第 1 轮观察期 (对应大周期结束后的第 2 个小周期)
                    ctx.State = ConsecutiveReversalState.Observing;
                    ctx.CurrentObservationPeriodIndex = 1;
                    ctx.CurrentObservationStartMs = t0 + dt;
                    ctx.CurrentObservationEndMs = t0 + 2 * dt;
                    ctx.CurrentObservationBar = new SubPeriodBar(ctx.CurrentObservationStartMs, ctx.CurrentObservationEndMs);
                    ctx.HasReachedBenchmark = false;
                    ctx.ObservedPeakPrice = decimal.MinValue;
                    ctx.ObservedTroughPrice = decimal.MaxValue;

                    if (ctx.IsShort)
                    {
                        OnDetectorLog?.Invoke(
                            $"[进入观察期] 第1个{ReversalSubPeriodMinutes}分钟周期收阴反转确认 (开:{ctx.SubPeriod1.Open:F2}, 收:{ctx.SubPeriod1.Close:F2}, 跌幅:{ctx.SubPeriod1.ChangePct:F2}%) -> 正式进入第1轮{ReversalSubPeriodMinutes}分钟观察期，监测冲高后回落做空！",
                            Color.FromArgb(250, 204, 21)); // 醒目金黄
                    }
                    else
                    {
                        OnDetectorLog?.Invoke(
                            $"[进入观察期] 第1个{ReversalSubPeriodMinutes}分钟周期收阳反转确认 (开:{ctx.SubPeriod1.Open:F2}, 收:{ctx.SubPeriod1.Close:F2}, 涨幅:{ctx.SubPeriod1.ChangePct:F2}%) -> 正式进入第1轮{ReversalSubPeriodMinutes}分钟观察期，监测探底后回升做多！",
                            Color.FromArgb(250, 204, 21)); // 醒目金黄
                    }
                }
                else
                {
                    // 未能反转，动能未衰竭，形态失效注销
                    ctx.State = ConsecutiveReversalState.Expired;
                    string note = ctx.SubPeriod1.HasTicks
                        ? $"开:{ctx.SubPeriod1.Open:F2}, 收:{ctx.SubPeriod1.Close:F2}"
                        : "无有效Tick";
                    string failureReason = isSubPeriod1Doji
                        ? "收小十字星(微小波动，不作为反转条件)"
                        : (ctx.IsShort ? "未收阴" : "未收阳");
                    string trendName = ctx.IsShort ? "多头" : "空头";
                    OnDetectorLog?.Invoke(
                        $"[形态失效] 第1个{ReversalSubPeriodMinutes}分钟周期{failureReason} ({note})，{trendName}动能未衰竭，退出观察。",
                        Color.FromArgb(148, 163, 184));
                    return;
                }
            }

            // ================= 阶段 2: 正式观察期 (支持多轮小周期滚动顺延) =================
            if (ctx.State == ConsecutiveReversalState.Observing)
            {
                // 🌟 核心规则：若当前观察期走完且未产生明显信号，自动进入下个观察期执行同样逻辑
                while (t >= ctx.CurrentObservationEndMs)
                {
                    if (ctx.CurrentObservationPeriodIndex >= MaxObservationPeriods)
                    {
                        // 达到最大允许观察轮数，形态超时退出
                        ctx.State = ConsecutiveReversalState.Expired;
                        string targetName = ctx.IsShort ? "冲高回落做空" : "探底回升做多";
                        OnDetectorLog?.Invoke(
                            $"[观察期结束] 已连续观察 {ctx.CurrentObservationPeriodIndex} 轮{ReversalSubPeriodMinutes}分钟周期，无明显{targetName}信号，退出观察。",
                            Color.FromArgb(148, 163, 184));
                        return;
                    }

                    int prevIndex = ctx.CurrentObservationPeriodIndex;
                    ctx.CurrentObservationPeriodIndex++;
                    ctx.CurrentObservationStartMs = ctx.CurrentObservationEndMs;
                    ctx.CurrentObservationEndMs = ctx.CurrentObservationStartMs + dt;
                    ctx.CurrentObservationBar = new SubPeriodBar(ctx.CurrentObservationStartMs, ctx.CurrentObservationEndMs);
                    ctx.HasReachedBenchmark = false;
                    ctx.ObservedPeakPrice = decimal.MinValue;
                    ctx.ObservedTroughPrice = decimal.MaxValue;

                    string targetAction = ctx.IsShort ? "二次冲高回落做空" : "二次探底回升做多";
                    OnDetectorLog?.Invoke(
                        $"[进入下个观察期] 第 {prevIndex} 轮{ReversalSubPeriodMinutes}分钟观察期无明显信号 -> 进入第 {ctx.CurrentObservationPeriodIndex} 轮观察期继续监测{targetAction}...",
                        Color.FromArgb(250, 204, 21)); // 醒目金黄
                }

                // 更新当前观察期聚合 K 线
                ctx.CurrentObservationBar.UpdateWithTick(tick);

                decimal lowerBound = ctx.BenchmarkPrice * (1.0m - (TolerancePct / 100.0m));
                decimal upperBound = ctx.BenchmarkPrice * (1.0m + (TolerancePct / 100.0m));

                // 2.1 严重破位失效保护 (若价格暴涨暴跌破坏双顶/双底形态，立即注销)
                if (ctx.IsShort && tick.Price > ctx.BenchmarkPrice * (1.0m + (TolerancePct * 2.0m / 100.0m)))
                {
                    ctx.State = ConsecutiveReversalState.Expired;
                    OnDetectorLog?.Invoke(
                        $"[形态失效] 现价 {tick.Price:F2} 强劲突破连阳前高上界，多头趋势强势延续，反转做空形态失效。",
                        Color.FromArgb(148, 163, 184));
                    return;
                }
                if (ctx.IsLong && tick.Price < ctx.BenchmarkPrice * (1.0m - (TolerancePct * 2.0m / 100.0m)))
                {
                    ctx.State = ConsecutiveReversalState.Expired;
                    OnDetectorLog?.Invoke(
                        $"[形态失效] 现价 {tick.Price:F2} 强劲跌破连阴前低下界，空头趋势强势延续，反转做多形态失效。",
                        Color.FromArgb(148, 163, 184));
                    return;
                }

                // 2.2 【做空逻辑】：必须处于高点后回落
                if (ctx.IsShort)
                {
                    // 步骤 A: 价格进入高点附近区域，记录处于高点及局部最高峰值
                    if (tick.Price >= lowerBound && tick.Price <= upperBound)
                    {
                        ctx.HasReachedBenchmark = true;
                        if (tick.Price > ctx.ObservedPeakPrice)
                        {
                            ctx.ObservedPeakPrice = tick.Price;
                            ctx.PeakOrTroughTick = tick;
                        }
                    }

                    // 步骤 B: 处于高点后回落判定
                    if (ctx.HasReachedBenchmark && ctx.ObservedPeakPrice > decimal.MinValue)
                    {
                        decimal drop = ctx.ObservedPeakPrice - tick.Price;
                        decimal minPullback = ctx.ObservedPeakPrice * (PullbackPct / 100.0m);
                        bool isPullbackConfirmed = drop > 0m && (drop >= minPullback || PullbackPct <= 0m);

                        if (isPullbackConfirmed && tick.Price >= lowerBound * 0.95m)
                        {
                            // 🌟 触发冲高回落做空信号 (严格锁定当前确认回落的真实实时 Tick)
                            var shortSignal = new ChannelSignalItem
                            {
                                Id = _signalIdCounter++,
                                ChannelId = ctx.Id,
                                Type = ChannelSignalType.Short,
                                TriggerTick = tick,
                                TriggerPrice = tick.Price,
                                TriggerTimeMs = tick.Time,
                                InitialTriggerTimeMs = tick.Time,
                                TriggerTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).UtcDateTime,
                                MacroBarIndex = curMacroIndex,
                                MacroProgress = totalTicksInBucket > 0 ? Math.Clamp((double)Math.Max(0, curTickIndex - 1) / totalTicksInBucket, 0.0, 0.999) : 0.0,
                                TickIndexInBucket = Math.Max(0, curTickIndex - 1),
                                ChannelUpper = ctx.ObservedPeakPrice,
                                ChannelLower = lowerBound,
                                ChannelSlope = 0m,
                                Note = $"{ctx.ConsecutiveBarCount}连阳+第{ctx.CurrentObservationPeriodIndex}轮{ReversalSubPeriodMinutes}m冲高回落做空(峰值:{ctx.ObservedPeakPrice:F2})"
                            };

                            lock (_lock)
                            {
                                _signals.Add(shortSignal);
                            }
                            ctx.GeneratedSignal = shortSignal;
                            ctx.State = ConsecutiveReversalState.Triggered; // 严格永久铆钉

                            OnSignalGenerated?.Invoke(shortSignal);
                            OnDetectorLog?.Invoke(
                                $"【做空信号 ▼】 {shortSignal.TriggerTime:yyyy-MM-dd HH:mm:ss} 现价:{tick.Price:F2} 冲高({ctx.ObservedPeakPrice:F2})后回落(前高:{ctx.BenchmarkPrice:F2}, 回落:{drop:F2}, 观察期#{ctx.CurrentObservationPeriodIndex}) Tick#{curTickIndex} -> 触发做空！",
                                Color.FromArgb(248, 113, 113));
                            return;
                        }
                    }
                }
                // 2.3 【做多逻辑】：必须处于低点后回升
                else
                {
                    // 步骤 A: 价格进入低点附近区域，记录处于低点及局部最低谷值
                    if (tick.Price <= upperBound && tick.Price >= lowerBound)
                    {
                        ctx.HasReachedBenchmark = true;
                        if (tick.Price < ctx.ObservedTroughPrice)
                        {
                            ctx.ObservedTroughPrice = tick.Price;
                            ctx.PeakOrTroughTick = tick;
                        }
                    }

                    // 步骤 B: 处于低点后回升判定
                    if (ctx.HasReachedBenchmark && ctx.ObservedTroughPrice < decimal.MaxValue)
                    {
                        decimal rise = tick.Price - ctx.ObservedTroughPrice;
                        decimal minRebound = ctx.ObservedTroughPrice * (PullbackPct / 100.0m);
                        bool isReboundConfirmed = rise > 0m && (rise >= minRebound || PullbackPct <= 0m);

                        if (isReboundConfirmed && tick.Price <= upperBound * 1.05m)
                        {
                            // 🌟 触发探底回升做多信号 (严格锁定当前确认回升的真实实时 Tick)
                            var longSignal = new ChannelSignalItem
                            {
                                Id = _signalIdCounter++,
                                ChannelId = ctx.Id,
                                Type = ChannelSignalType.Long,
                                TriggerTick = tick,
                                TriggerPrice = tick.Price,
                                TriggerTimeMs = tick.Time,
                                InitialTriggerTimeMs = tick.Time,
                                TriggerTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).UtcDateTime,
                                MacroBarIndex = curMacroIndex,
                                MacroProgress = totalTicksInBucket > 0 ? Math.Clamp((double)Math.Max(0, curTickIndex - 1) / totalTicksInBucket, 0.0, 0.999) : 0.0,
                                TickIndexInBucket = Math.Max(0, curTickIndex - 1),
                                ChannelUpper = upperBound,
                                ChannelLower = ctx.ObservedTroughPrice,
                                ChannelSlope = 0m,
                                Note = $"{ctx.ConsecutiveBarCount}连阴+第{ctx.CurrentObservationPeriodIndex}轮{ReversalSubPeriodMinutes}m探底回升做多(谷值:{ctx.ObservedTroughPrice:F2})"
                            };

                            lock (_lock)
                            {
                                _signals.Add(longSignal);
                            }
                            ctx.GeneratedSignal = longSignal;
                            ctx.State = ConsecutiveReversalState.Triggered; // 严格永久铆钉

                            OnSignalGenerated?.Invoke(longSignal);
                            OnDetectorLog?.Invoke(
                                $"【做多信号 ▲】 {longSignal.TriggerTime:yyyy-MM-dd HH:mm:ss} 现价:{tick.Price:F2} 探底({ctx.ObservedTroughPrice:F2})后回升(前低:{ctx.BenchmarkPrice:F2}, 回升:{rise:F2}, 观察期#{ctx.CurrentObservationPeriodIndex}) Tick#{curTickIndex} -> 触发做多！",
                                Color.FromArgb(74, 222, 128));
                            return;
                        }
                    }
                }
            }

            // ================= 阶段 3: 已触发做空/做多信号状态保持 (永久铆钉，坚决不向后移位) =================
            if (ctx.State == ConsecutiveReversalState.Triggered)
            {
                if (t >= ctx.CurrentObservationEndMs)
                {
                    ctx.State = ConsecutiveReversalState.Completed;
                }
                return;
            }
        }
    }
}
