using Common;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Linq;
using Test.PeriodTickPlayback.WinForms.Models;
using Color = ScottPlot.Color;

namespace Test.PeriodTickPlayback.WinForms.Helper
{
    /// <summary>
    /// 高频形态类型：双顶（M顶做空）或双底（W底做多）
    /// </summary>
    public enum TickPatternType
    {
        DoubleTop = 0,    // M顶 / 双顶做空 (连续上涨K线后形成)
        DoubleBottom = 1  // W底 / 双底做多 (连续下跌K线后形成)
    }

    /// <summary>
    /// 基于 Tick 与价格水平切线的高频双顶（M顶）/ 双底（W底）信号数据模型
    /// 当大周期或微观周期出现连续上涨后在价格切线区间内识别双顶，或连续下跌后识别双底
    /// </summary>
    public class TickDoubleTopSignal
    {
        public int Id { get; set; }
        public TickPatternType PatternType { get; set; } = TickPatternType.DoubleTop;
        public bool IsDoubleTop => PatternType == TickPatternType.DoubleTop;
        public bool IsDoubleBottom => PatternType == TickPatternType.DoubleBottom;

        // 点1：双顶为第一顶 (Top 1)，双底为第一底 (Bottom 1)
        public int Top1TickIndex { get; set; }
        public long Top1TimeMs { get; set; }
        public DateTime Top1Time => DateTimeOffset.FromUnixTimeMilliseconds(Top1TimeMs).UtcDateTime;
        public decimal Top1Price { get; set; }

        public int Bottom1TickIndex { get => Top1TickIndex; set => Top1TickIndex = value; }
        public long Bottom1TimeMs { get => Top1TimeMs; set => Top1TimeMs = value; }
        public DateTime Bottom1Time => Top1Time;
        public decimal Bottom1Price { get => Top1Price; set => Top1Price = value; }

        // 颈线点 (Neckline：双顶为回撤谷底，双底为反弹峰顶)
        public int NeckTickIndex { get; set; }
        public long NeckTimeMs { get; set; }
        public DateTime NeckTime => DateTimeOffset.FromUnixTimeMilliseconds(NeckTimeMs).UtcDateTime;
        public decimal NeckPrice { get; set; }

        // 点2：双顶为第二顶 (Top 2)，双底为第二底 (Bottom 2)
        public int Top2TickIndex { get; set; }
        public long Top2TimeMs { get; set; }
        public DateTime Top2Time => DateTimeOffset.FromUnixTimeMilliseconds(Top2TimeMs).UtcDateTime;
        public decimal Top2Price { get; set; }

        public int Bottom2TickIndex { get => Top2TickIndex; set => Top2TickIndex = value; }
        public long Bottom2TimeMs { get => Top2TimeMs; set => Top2TimeMs = value; }
        public DateTime Bottom2Time => Top2Time;
        public decimal Bottom2Price { get => Top2Price; set => Top2Price = value; }

        // 确立点 (Confirmation Point)
        public int ConfirmTickIndex { get; set; }
        public long ConfirmTimeMs { get; set; }
        public DateTime ConfirmTime => DateTimeOffset.FromUnixTimeMilliseconds(ConfirmTimeMs).UtcDateTime;
        public decimal ConfirmPrice { get; set; }

        // 所属价格切线区间
        public decimal TangentLowerPrice { get; set; }
        public decimal TangentUpperPrice { get; set; }
        public long TangentIndex { get; set; }

        // 统计指标
        public decimal PullbackDepth => IsDoubleTop
            ? (Math.Max(Top1Price, Top2Price) - NeckPrice)
            : (NeckPrice - Math.Min(Bottom1Price, Bottom2Price));

        public decimal PullbackPct => NeckPrice > 0 ? (PullbackDepth / NeckPrice) * 100m : 0m;
        public decimal PeakPriceDiff => Math.Abs(Top1Price - Top2Price);
        public TimeSpan Duration => ConfirmTime - Top1Time;
        public int TotalTicks => Math.Max(1, ConfirmTickIndex - Top1TickIndex + 1);
        public int PeakIntervalTicks => Math.Max(1, Top2TickIndex - Top1TickIndex);

        public bool IsNecklineBroken => IsDoubleTop
            ? (ConfirmPrice <= NeckPrice)
            : (ConfirmPrice >= NeckPrice);

        // 大周期坐标映射 (供宏观图平滑呈现)
        public double MacroX1 { get; set; }
        public double MacroXNeck { get; set; }
        public double MacroX2 { get; set; }
        public double MacroXConfirm { get; set; }

        public string Description
        {
            get
            {
                if (IsDoubleTop)
                {
                    return $"[切线 {TangentLowerPrice:F1}~{TangentUpperPrice:F1}] ⚡高频双顶(做空): M1={Top1Price:F2}, M2={Top2Price:F2}, 颈线={NeckPrice:F2}, 🎯判定点位={ConfirmPrice:F2} (双顶间隔:{PeakIntervalTicks}笔Tick, 价差/回撤:{PullbackDepth:F2}({PullbackPct:F2}%), 跌破颈线做空)";
                }
                else
                {
                    return $"[切线 {TangentLowerPrice:F1}~{TangentUpperPrice:F1}] ⚡高频双底(做多): W1={Bottom1Price:F2}, W2={Bottom2Price:F2}, 颈线={NeckPrice:F2}, 🎯判定点位={ConfirmPrice:F2} (双底间隔:{PeakIntervalTicks}笔Tick, 价差/反弹:{PullbackDepth:F2}({PullbackPct:F2}%), 突破颈线做多)";
                }
            }
        }

        public override string ToString() => Description;
    }

    /// <summary>
    /// 连续 K 线收盘完成且出现首根反转 K 线后的双顶/双底判定窗口
    /// </summary>
    public class DoublePatternWindow
    {
        public MacroConsecutiveTrendItem Trend { get; set; } = null!;
        public bool IsBullish => Trend.IsBullish;
        public int StreakEndBar { get; set; }
        public int FirstReversalBar { get; set; }
        public int EvalStartBar { get; set; }
        public int EvalEndBar { get; set; }
        public bool IsActiveWindow { get; set; }
        public bool IsHistorical { get; set; }

        public override string ToString()
        {
            string dir = IsBullish ? "连涨后双顶(做空)" : "连跌后双底(做多)";
            string status = IsActiveWindow ? "正在活跃判定中" : (IsHistorical ? "历史保留形态" : "已结束");
            return $"[{dir}] 状态:{status}, 连K结束:#{StreakEndBar}, 首根反转:#{FirstReversalBar}, 判定窗口:#{FirstReversalBar}~#{EvalEndBar}";
        }
    }

    /// <summary>
    /// 基于连续 K 线后在价格水平切线内出现的 Tick 级别双顶（做空）/ 双底（做多）高频信号检测器与绘制助手
    /// </summary>
    public static class TickDoubleTopHelper
    {
        /// <summary>
        /// 检查连续 K 线走势是否满足：连续K线收盘完成 + 出现第一个反转K线 + 处于后续 N 根 K 线判定窗口
        /// </summary>
        public static DoublePatternWindow? EvaluateTrendWindow(
            IReadOnlyList<MacroConsecutiveTrendDetector.BarSnapshot> bars,
            int completedBarsCount,
            MacroConsecutiveTrendItem tr,
            int subsequentBarsN,
            int? currentBarIndex = null)
        {
            if (bars == null || bars.Count == 0 || tr == null || subsequentBarsN < 1) return null;

            // 1. 连续上涨/连续下跌 K 线必须收盘完成 (即连涨/连跌的最后一根不能是仍在凝聚中未收盘的 K 线)
            if (tr.EndIndex >= completedBarsCount)
            {
                return null; // 仍在连续走势凝聚中，尚未 close 完成
            }

            // 2. 检查在连续 K 线结束后，是否出现了“第一个反转 K 线”
            // 连涨后寻找首根收跌/阴线 K 线；连跌后寻找首根收涨/阳线 K 线
            // 🌟 核心硬性约束：
            //   - 连续趋势本身必须已收盘完成 (tr.EndIndex < completedBarsCount)
            //   - 首根反转 K 线可以是已完成收盘定型的 K 线 (k < completedBarsCount)，
            //     或者是当前正在回放凝聚中、且已出现反转特征的 formingBar (k == completedBarsCount)，
            //     以支持回放过程中的实时高频形态计算与绘制！
            //   - 且在连续趋势结束后与首根反转出现之前，不能出现继续顺延原趋势的 K 线
            bool isBull = tr.IsBullish;
            int firstRevIdx = -1;

            for (int k = tr.EndIndex + 1; k < bars.Count; k++)
            {
                var b = bars[k];
                var prevB = bars[k - 1];
                bool isReversal = isBull
                    ? (b.Close < b.Open || b.Close < prevB.Close) // 连涨后出现下跌K线
                    : (b.Close > b.Open || b.Close > prevB.Close); // 连跌后出现上涨K线

                if (isReversal)
                {
                    firstRevIdx = k;
                    break;
                }

                // 若在反转之前出现顺延原趋势的K线，则说明连续趋势并未终结反转
                bool isContinuation = isBull
                    ? (b.Close > prevB.Close || (b.Close > b.Open && b.Close >= prevB.Close))
                    : (b.Close < prevB.Close || (b.Close < b.Open && b.Close <= prevB.Close));
                if (isContinuation)
                {
                    return null;
                }
            }

            if (firstRevIdx < 0)
            {
                return null; // 尚未出现完成收盘的首个反转 K 线
            }

            // 3. 确定后续 N 根 K 线的判定窗口范围: [firstRevIdx, firstRevIdx + subsequentBarsN]
            // 双顶由连涨最高点或首根阴线开始测试阻力，因此起始提取点为波段极值点
            int evalStartBar = isBull ? tr.MaxHighIndex : tr.MinLowIndex;
            int evalEndBar = firstRevIdx + subsequentBarsN;

            // 若指定了当前正在播放/正在观察的 K 线位置进行动态判断
            bool isActive = true;
            bool isHistorical = false;
            if (currentBarIndex.HasValue)
            {
                int curBar = currentBarIndex.Value;
                if (curBar < firstRevIdx)
                {
                    return null; // 当前回放进度尚未到达首根反转 K 线，反转尚未发生
                }
                if (curBar > evalEndBar)
                {
                    isActive = false;
                    isHistorical = true; // 🌟 核心：回放进度已推进至后续K线，该走势的反转窗口已成为历史，必须作为历史形态持续保留！
                }
            }

            return new DoublePatternWindow
            {
                Trend = tr,
                StreakEndBar = tr.EndIndex,
                FirstReversalBar = firstRevIdx,
                EvalStartBar = evalStartBar,
                EvalEndBar = evalEndBar,
                IsActiveWindow = isActive,
                IsHistorical = isHistorical
            };
        }

        /// <summary>
        /// 基于大周期 K 线序列快照评估双顶/双底判定窗口 (便利重载)
        /// </summary>
        public static DoublePatternWindow? EvaluateTrendWindow(
            IReadOnlyList<MacroKline>? completedBars,
            FormingMacroKline? formingBar,
            MacroConsecutiveTrendItem tr,
            int subsequentBarsN,
            int? currentBarIndex = null)
        {
            int completedCount = completedBars?.Count ?? 0;
            bool hasForming = formingBar != null && formingBar.HasTicks;
            int totalCount = completedCount + (hasForming ? 1 : 0);
            if (totalCount == 0 || tr == null) return null;

            var bars = new List<MacroConsecutiveTrendDetector.BarSnapshot>(totalCount);
            if (completedBars != null)
            {
                for (int i = 0; i < completedBars.Count; i++)
                {
                    var b = completedBars[i];
                    bars.Add(new MacroConsecutiveTrendDetector.BarSnapshot(i, b.Open, b.High, b.Low, b.Close));
                }
            }
            if (hasForming)
            {
                bars.Add(new MacroConsecutiveTrendDetector.BarSnapshot(completedCount, formingBar!.Open, formingBar.High, formingBar.Low, formingBar.CurrentPrice));
            }

            return EvaluateTrendWindow(bars, completedCount, tr, subsequentBarsN, currentBarIndex);
        }
        private struct TickExtremum
        {
            public int Index;
            public long TimeMs;
            public decimal Price;
            public bool IsPeak;
        }

        /// <summary>
        /// 检测双顶信号 (做空)，严格对应连续上涨K线背景
        /// </summary>
        public static List<TickDoubleTopSignal> DetectDoubleTops(
            IReadOnlyList<RawTick>? ticks,
            int tickCursor,
            decimal fixedPriceStep,
            int searchStartIndex = 0,
            int minIntervalTicks = 20,
            decimal minPriceDiffPct = 0.10m)
        {
            return DetectPatterns(ticks, tickCursor, fixedPriceStep, TickPatternType.DoubleTop, searchStartIndex, minIntervalTicks, minPriceDiffPct);
        }

        /// <summary>
        /// 检测双底信号 (做多)，严格对应连续下跌K线背景
        /// </summary>
        public static List<TickDoubleTopSignal> DetectDoubleBottoms(
            IReadOnlyList<RawTick>? ticks,
            int tickCursor,
            decimal fixedPriceStep,
            int searchStartIndex = 0,
            int minIntervalTicks = 20,
            decimal minPriceDiffPct = 0.10m)
        {
            return DetectPatterns(ticks, tickCursor, fixedPriceStep, TickPatternType.DoubleBottom, searchStartIndex, minIntervalTicks, minPriceDiffPct);
        }

        /// <summary>
        /// 从指定 Tick 序列中检测价格切线内形成的双顶（做空）或双底（做多）高频信号
        /// </summary>
        public static List<TickDoubleTopSignal> DetectPatterns(
            IReadOnlyList<RawTick>? ticks,
            int tickCursor,
            decimal fixedPriceStep,
            TickPatternType? targetType = null,
            int searchStartIndex = 0,
            int minIntervalTicks = 20,
            decimal minPriceDiffPct = 0.10m)
        {
            if (ticks == null || tickCursor <= 0 || fixedPriceStep <= 0)
                return new List<TickDoubleTopSignal>();

            int count = Math.Min(ticks.Count, tickCursor);
            int startIdx = Math.Clamp(searchStartIndex, 0, count);
            int availableCount = count - startIdx;

            int safeMinInterval = Math.Max(4, minIntervalTicks);
            int minTotalIntervalTicks = safeMinInterval;
            int minLegTicks = Math.Max(2, (int)Math.Round(safeMinInterval * 0.25));

            if (availableCount < minTotalIntervalTicks + 4) return new List<TickDoubleTopSignal>();

            // 1. 计算价格切线基准与动态摆动门槛 (Swing Threshold)
            decimal sumPrice = 0m;
            int sampleStep = Math.Max(1, availableCount / 50);
            int sampleCount = 0;
            for (int i = startIdx; i < count; i += sampleStep)
            {
                sumPrice += ticks[i].Price;
                sampleCount++;
            }
            decimal avgPrice = sampleCount > 0 ? (sumPrice / sampleCount) : ticks[startIdx].Price;

            // 🌟 核心优化：双顶双底需要一定的价差，过滤微小抖动误判 (默认 0.10%，如 128 币价为 0.128 USDT，60000 币价为 60 USDT)
            decimal effectiveMinPct = Math.Max(0.01m, minPriceDiffPct);
            decimal minPriceSpread = avgPrice * (effectiveMinPct / 100m);

            // 摆动阈值：结合币价百分比、切线步长与最小价差门槛，确保 ZigZag 既敏锐捕捉转折，又不被微幅抖动扰动
            decimal swingThreshold = Math.Max(avgPrice * 0.0003m, Math.Min(minPriceSpread * 0.35m, fixedPriceStep * 0.15m));
            if (swingThreshold <= 0.00001m) swingThreshold = 0.001m;

            // 2. 线性 ZigZag 提取 Tick 极值序列 (波峰与波谷)
            var extrema = new List<TickExtremum>(64);
            decimal curExtremumPrice = ticks[startIdx].Price;
            int curExtremumIdx = startIdx;
            long curExtremumTime = ticks[startIdx].Time;
            bool? isSeekingPeak = null; // null: 初始寻找方向

            for (int i = startIdx + 1; i < count; i++)
            {
                decimal p = ticks[i].Price;
                long t = ticks[i].Time;

                if (!isSeekingPeak.HasValue)
                {
                    if (p - curExtremumPrice >= swingThreshold)
                    {
                        extrema.Add(new TickExtremum { Index = curExtremumIdx, TimeMs = curExtremumTime, Price = curExtremumPrice, IsPeak = false });
                        curExtremumPrice = p;
                        curExtremumIdx = i;
                        curExtremumTime = t;
                        isSeekingPeak = false; // 已录入波谷，接下来寻找波峰
                    }
                    else if (curExtremumPrice - p >= swingThreshold)
                    {
                        extrema.Add(new TickExtremum { Index = curExtremumIdx, TimeMs = curExtremumTime, Price = curExtremumPrice, IsPeak = true });
                        curExtremumPrice = p;
                        curExtremumIdx = i;
                        curExtremumTime = t;
                        isSeekingPeak = true; // 已录入波峰，接下来寻找波谷
                    }
                }
                else if (!isSeekingPeak.Value)
                {
                    // 寻找波峰中
                    if (p > curExtremumPrice)
                    {
                        curExtremumPrice = p;
                        curExtremumIdx = i;
                        curExtremumTime = t;
                    }
                    else if (curExtremumPrice - p >= swingThreshold)
                    {
                        extrema.Add(new TickExtremum { Index = curExtremumIdx, TimeMs = curExtremumTime, Price = curExtremumPrice, IsPeak = true });
                        curExtremumPrice = p;
                        curExtremumIdx = i;
                        curExtremumTime = t;
                        isSeekingPeak = true; // 转向寻找波谷
                    }
                }
                else
                {
                    // 寻找波谷中
                    if (p < curExtremumPrice)
                    {
                        curExtremumPrice = p;
                        curExtremumIdx = i;
                        curExtremumTime = t;
                    }
                    else if (p - curExtremumPrice >= swingThreshold)
                    {
                        extrema.Add(new TickExtremum { Index = curExtremumIdx, TimeMs = curExtremumTime, Price = curExtremumPrice, IsPeak = false });
                        curExtremumPrice = p;
                        curExtremumIdx = i;
                        curExtremumTime = t;
                        isSeekingPeak = false; // 转向寻找波峰
                    }
                }
            }

            // 补入当前最新极值点
            if (isSeekingPeak.HasValue)
            {
                extrema.Add(new TickExtremum
                {
                    Index = curExtremumIdx,
                    TimeMs = curExtremumTime,
                    Price = curExtremumPrice,
                    IsPeak = !isSeekingPeak.Value
                });
            }

            if (extrema.Count < 3) return new List<TickDoubleTopSignal>();

            var signals = new List<TickDoubleTopSignal>();
            decimal maxExtremumDiff = Math.Max(minPriceSpread * 0.85m, Math.Max(fixedPriceStep * 0.35m, avgPrice * 0.006m));

            bool checkDoubleTop = !targetType.HasValue || targetType.Value == TickPatternType.DoubleTop;
            bool checkDoubleBottom = !targetType.HasValue || targetType.Value == TickPatternType.DoubleBottom;

            // 核心门槛：回撤/反弹深度必须达到指定最小价差 (minPriceSpread)
            decimal minPullbackDepth = Math.Max(minPriceSpread, swingThreshold * 1.1m);
            decimal minReboundHeight = minPullbackDepth;

            // 3. 遍历候选极值序列 (支持跨越 1~3 个次级极值波段的多极值候选搜索，大幅提高形态检出率)
            for (int i = 0; i < extrema.Count - 2; i++)
            {
                var pt1 = extrema[i];

                // ----------------------------------------------------
                // 分支 A: 识别双顶 (Top1 [峰] -> 下跌颈线 [谷] -> 重新上涨至 Top2 [峰])
                // ----------------------------------------------------
                if (checkDoubleTop && pt1.IsPeak)
                {
                    // 允许寻找后续第 1 个、第 2 个或第 3 个波峰作为第二顶 (j 步进 2，最多跨越 2~3 个次级极值波段)
                    int maxJ = Math.Min(extrema.Count - 1, i + 6);
                    for (int j = i + 2; j <= maxJ; j += 2)
                    {
                        var pt2 = extrema[j];
                        if (!pt2.IsPeak) continue;

                        // ① 间隔条件：第一顶与第二顶之间必须有足够 Tick 间隔
                        int totalIntervalTicks = pt2.Index - pt1.Index;
                        if (totalIntervalTicks < minTotalIntervalTicks) continue;

                        // ② 两顶价差容差 (双顶要求测试相近阻力位)
                        decimal peakDiff = Math.Abs(pt1.Price - pt2.Price);
                        if (peakDiff > maxExtremumDiff) continue;

                        // 在 pt1 与 pt2 之间寻找最深的回撤谷底作为颈线 (Neck)
                        int bestNeckIdx = -1;
                        decimal lowestValleyPrice = decimal.MaxValue;
                        bool intermediateExceeded = false;

                        for (int k = i + 1; k < j; k++)
                        {
                            var midExt = extrema[k];
                            if (!midExt.IsPeak)
                            {
                                if (midExt.Price < lowestValleyPrice)
                                {
                                    lowestValleyPrice = midExt.Price;
                                    bestNeckIdx = k;
                                }
                            }
                            else
                            {
                                // 若中间存在次级波峰，该波峰不能明显突破两顶最大值 (不能出现更高的高点破坏双顶阻力)
                                if (midExt.Price > Math.Max(pt1.Price, pt2.Price) + maxExtremumDiff * 0.5m)
                                {
                                    intermediateExceeded = true;
                                    break;
                                }
                            }
                        }

                        if (intermediateExceeded || bestNeckIdx < 0) continue;
                        var neck = extrema[bestNeckIdx];

                        int leg1Ticks = neck.Index - pt1.Index;
                        int leg2Ticks = pt2.Index - neck.Index;
                        if (leg1Ticks < minLegTicks || leg2Ticks < minLegTicks) continue;

                        // ③ 中间必须存在实质性下跌：第一顶后回撤深度必须显著满足最小价差
                        decimal minPeakPrice = Math.Min(pt1.Price, pt2.Price);
                        decimal pullback = minPeakPrice - neck.Price;
                        if (pullback < minPullbackDepth) continue;
                        if (peakDiff > pullback * 0.55m) continue; // 两峰之间差异不能过大，必须相较于回调有真正的双顶对称性

                        // ④ 验证两顶之间确实经历过实质性价格下跌（而非顶部横向微幅杂波走平）：
                        // 计算两顶中间区间的 Tick 均价，必须显著低于两顶均价，证明价格确实下探到了颈线谷底区间
                        decimal peakAvg = (pt1.Price + pt2.Price) / 2m;
                        decimal sumInter = 0m;
                        for (int ti = pt1.Index; ti <= pt2.Index; ti++)
                        {
                            sumInter += ticks[ti].Price;
                        }
                        decimal avgInter = sumInter / (totalIntervalTicks + 1);
                        if ((peakAvg - avgInter) < pullback * 0.08m) continue;

                        // ⑤ 价格切线判定：“价格切线里面出现的双顶”
                        long k1 = (long)Math.Floor(pt1.Price / fixedPriceStep);
                        long k2 = (long)Math.Floor(pt2.Price / fixedPriceStep);
                        bool sameTangentZone = (k1 == k2);

                        decimal distToTangent1 = Math.Abs(pt1.Price - Math.Round(pt1.Price / fixedPriceStep) * fixedPriceStep);
                        decimal distToTangent2 = Math.Abs(pt2.Price - Math.Round(pt2.Price / fixedPriceStep) * fixedPriceStep);
                        bool nearTangentLine = (distToTangent1 <= fixedPriceStep * 0.40m || distToTangent2 <= fixedPriceStep * 0.40m);
                        bool closePeaks = (peakDiff <= Math.Max(fixedPriceStep * 0.35m, minPriceSpread * 0.60m));

                        if (sameTangentZone || nearTangentLine || closePeaks)
                        {
                            long tangentK = sameTangentZone ? k1 : (long)Math.Round(((pt1.Price + pt2.Price) / 2m) / fixedPriceStep);
                            decimal tLow = tangentK * fixedPriceStep;
                            decimal tHigh = tLow + fixedPriceStep;

                            int confirmIdx = count - 1;
                            long confirmTime = ticks[confirmIdx].Time;
                            decimal confirmPrice = ticks[confirmIdx].Price;
                            bool isConfirmed = false;

                            int afterTop2Start = Math.Min(count - 1, pt2.Index + 1);
                            decimal breakOutTolerance = Math.Max(pullback * 0.25m, Math.Max(swingThreshold * 0.80m, avgPrice * 0.0015m));
                            decimal neckTolerance = Math.Min(pullback * 0.08m, Math.Max(swingThreshold * 0.25m, avgPrice * 0.0003m));

                            for (int ti = afterTop2Start; ti < count; ti++)
                            {
                                decimal tp = ticks[ti].Price;
                                // 价格向上突破双顶阻力过多则形态破位失败
                                if (tp > Math.Max(pt1.Price, pt2.Price) + breakOutTolerance)
                                {
                                    isConfirmed = false;
                                    break;
                                }

                                // 🌟 核心规范：双顶做空必须跌破颈线（过线做空）！
                                // 价格自第二顶回落，必须真正下探并击穿/跌破颈线才确立，绝不能在峰顶附近仅做小幅回撤就错判做空
                                if (tp <= neck.Price + neckTolerance)
                                {
                                    isConfirmed = true;
                                    confirmIdx = ti;
                                    confirmTime = ticks[ti].Time;
                                    confirmPrice = tp;
                                    break; // 首次跌破颈线即确立锁定判定点位
                                }
                            }

                            if (isConfirmed)
                            {
                                signals.Add(new TickDoubleTopSignal
                                {
                                    Id = signals.Count + 1,
                                    PatternType = TickPatternType.DoubleTop,
                                    Top1TickIndex = pt1.Index,
                                    Top1TimeMs = pt1.TimeMs,
                                    Top1Price = pt1.Price,
                                    NeckTickIndex = neck.Index,
                                    NeckTimeMs = neck.TimeMs,
                                    NeckPrice = neck.Price,
                                    Top2TickIndex = pt2.Index,
                                    Top2TimeMs = pt2.TimeMs,
                                    Top2Price = pt2.Price,
                                    ConfirmTickIndex = confirmIdx,
                                    ConfirmTimeMs = confirmTime,
                                    ConfirmPrice = confirmPrice,
                                    TangentLowerPrice = tLow,
                                    TangentUpperPrice = tHigh,
                                    TangentIndex = tangentK
                                });
                            }
                        }
                    }
                }

                // ----------------------------------------------------
                // 分支 B: 识别双底 (Bottom1 [谷] -> 上涨颈线 [峰] -> 重新下跌至 Bottom2 [谷])
                // ----------------------------------------------------
                if (checkDoubleBottom && !pt1.IsPeak)
                {
                    // 允许寻找后续第 1 个、第 2 个或第 3 个波谷作为第二底 (j 步进 2，最多跨越 2~3 个次级极值波段)
                    int maxJ = Math.Min(extrema.Count - 1, i + 6);
                    for (int j = i + 2; j <= maxJ; j += 2)
                    {
                        var pt2 = extrema[j];
                        if (pt2.IsPeak) continue;

                        // ① 间隔条件：第一底与第二底之间必须有足够 Tick 间隔
                        int totalIntervalTicks = pt2.Index - pt1.Index;
                        if (totalIntervalTicks < minTotalIntervalTicks) continue;

                        // ② 两底价差容差 (双底要求测试相近支撑位)
                        decimal bottomDiff = Math.Abs(pt1.Price - pt2.Price);
                        if (bottomDiff > maxExtremumDiff) continue;

                        // 在 pt1 与 pt2 之间寻找最高反弹波峰作为颈线 (Neck)
                        int bestNeckIdx = -1;
                        decimal highestPeakPrice = decimal.MinValue;
                        bool intermediateExceeded = false;

                        for (int k = i + 1; k < j; k++)
                        {
                            var midExt = extrema[k];
                            if (midExt.IsPeak)
                            {
                                if (midExt.Price > highestPeakPrice)
                                {
                                    highestPeakPrice = midExt.Price;
                                    bestNeckIdx = k;
                                }
                            }
                            else
                            {
                                // 若中间存在次级波谷，该波谷不能明显跌破两底最小值 (不能出现更低的低点破坏双底支撑)
                                if (midExt.Price < Math.Min(pt1.Price, pt2.Price) - maxExtremumDiff * 0.5m)
                                {
                                    intermediateExceeded = true;
                                    break;
                                }
                            }
                        }

                        if (intermediateExceeded || bestNeckIdx < 0) continue;
                        var neck = extrema[bestNeckIdx];

                        int leg1Ticks = neck.Index - pt1.Index;
                        int leg2Ticks = pt2.Index - neck.Index;
                        if (leg1Ticks < minLegTicks || leg2Ticks < minLegTicks) continue;

                        // ③ 中间必须存在实质性上涨：第一底后反弹高度必须显著满足最小价差
                        decimal maxBottomPrice = Math.Max(pt1.Price, pt2.Price);
                        decimal rebound = neck.Price - maxBottomPrice;
                        if (rebound < minReboundHeight) continue;
                        if (bottomDiff > rebound * 0.55m) continue; // 两底之间差异不能过大，必须相较于反弹有真正的双底对称性

                        // ④ 验证两底之间确实经历过实质性价格上涨（而非底部横向微幅杂波走平）：
                        // 计算两底中间区间的 Tick 均价，必须显著高于两底均价，证明价格确实回升到了颈线峰顶区间
                        decimal bottomAvg = (pt1.Price + pt2.Price) / 2m;
                        decimal sumInter = 0m;
                        for (int ti = pt1.Index; ti <= pt2.Index; ti++)
                        {
                            sumInter += ticks[ti].Price;
                        }
                        decimal avgInter = sumInter / (totalIntervalTicks + 1);
                        if ((avgInter - bottomAvg) < rebound * 0.08m) continue;

                        // ⑤ 价格切线判定：“价格切线里面出现的双底”
                        long k1 = (long)Math.Floor(pt1.Price / fixedPriceStep);
                        long k2 = (long)Math.Floor(pt2.Price / fixedPriceStep);
                        bool sameTangentZone = (k1 == k2);

                        decimal distToTangent1 = Math.Abs(pt1.Price - Math.Round(pt1.Price / fixedPriceStep) * fixedPriceStep);
                        decimal distToTangent2 = Math.Abs(pt2.Price - Math.Round(pt2.Price / fixedPriceStep) * fixedPriceStep);
                        bool nearTangentLine = (distToTangent1 <= fixedPriceStep * 0.40m || distToTangent2 <= fixedPriceStep * 0.40m);
                        bool closePeaks = (bottomDiff <= Math.Max(fixedPriceStep * 0.35m, minPriceSpread * 0.60m));

                        if (sameTangentZone || nearTangentLine || closePeaks)
                        {
                            long tangentK = sameTangentZone ? k1 : (long)Math.Round(((pt1.Price + pt2.Price) / 2m) / fixedPriceStep);
                            decimal tLow = tangentK * fixedPriceStep;
                            decimal tHigh = tLow + fixedPriceStep;

                            int confirmIdx = count - 1;
                            long confirmTime = ticks[confirmIdx].Time;
                            decimal confirmPrice = ticks[confirmIdx].Price;
                            bool isConfirmed = false;

                            int afterBottom2Start = Math.Min(count - 1, pt2.Index + 1);
                            decimal breakDownTolerance = Math.Max(rebound * 0.25m, Math.Max(swingThreshold * 0.80m, avgPrice * 0.0015m));
                            decimal neckTolerance = Math.Min(rebound * 0.08m, Math.Max(swingThreshold * 0.25m, avgPrice * 0.0003m));

                            for (int ti = afterBottom2Start; ti < count; ti++)
                            {
                                decimal tp = ticks[ti].Price;
                                // 跌破双底支撑过多则形态破位失败
                                if (tp < Math.Min(pt1.Price, pt2.Price) - breakDownTolerance)
                                {
                                    isConfirmed = false;
                                    break;
                                }

                                // 🌟 核心规范：双底做多必须突破颈线（过线做多）！
                                // 价格自第二底反弹，必须真正回升并向上击穿/突破颈线才确立，绝不能在谷底附近仅做小幅微弹就错判做多
                                if (tp >= neck.Price - neckTolerance)
                                {
                                    isConfirmed = true;
                                    confirmIdx = ti;
                                    confirmTime = ticks[ti].Time;
                                    confirmPrice = tp;
                                    break; // 首次突破颈线即确立锁定判定点位
                                }
                            }

                            if (isConfirmed)
                            {
                                signals.Add(new TickDoubleTopSignal
                                {
                                    Id = signals.Count + 1,
                                    PatternType = TickPatternType.DoubleBottom,
                                    Top1TickIndex = pt1.Index,
                                    Top1TimeMs = pt1.TimeMs,
                                    Top1Price = pt1.Price,
                                    NeckTickIndex = neck.Index,
                                    NeckTimeMs = neck.TimeMs,
                                    NeckPrice = neck.Price,
                                    Top2TickIndex = pt2.Index,
                                    Top2TimeMs = pt2.TimeMs,
                                    Top2Price = pt2.Price,
                                    ConfirmTickIndex = confirmIdx,
                                    ConfirmTimeMs = confirmTime,
                                    ConfirmPrice = confirmPrice,
                                    TangentLowerPrice = tLow,
                                    TangentUpperPrice = tHigh,
                                    TangentIndex = tangentK
                                });
                            }
                        }
                    }
                }
            }

            // 过滤相邻紧密重复的重叠双顶/双底，保留独立清晰的高频形态 (若存在重叠优先保留深度更显著的形态)
            if (signals.Count > 1)
            {
                var filtered = new List<TickDoubleTopSignal>();
                for (int i = 0; i < signals.Count; i++)
                {
                    var sig = signals[i];
                    int dupIdx = filtered.FindIndex(f =>
                        f.PatternType == sig.PatternType &&
                        (Math.Abs(f.Top2TickIndex - sig.Top2TickIndex) < 10 ||
                        (f.TangentIndex == sig.TangentIndex && Math.Abs(f.Top2Price - sig.Top2Price) < maxExtremumDiff * 0.5m)));

                    if (dupIdx < 0)
                    {
                        filtered.Add(sig);
                    }
                    else
                    {
                        if (sig.PullbackDepth > filtered[dupIdx].PullbackDepth)
                        {
                            filtered[dupIdx] = sig;
                        }
                    }
                }
                return filtered;
            }

            return signals;
        }

        /// <summary>
        /// 在 ScottPlot 图表上高品质绘制高频双顶（做空）/ 双底（做多）信号与 M/W 型结构连接线、颈线与提示徽章
        /// </summary>
        public static void DrawDoubleTopSignals(
            Plot plot,
            IReadOnlyList<TickDoubleTopSignal>? signals,
            IReadOnlyList<PeriodBucket>? subBuckets,
            string fontName,
            bool isMacroChart = false,
            bool isRawTickChart = false)
        {
            if (plot == null || signals == null || signals.Count == 0) return;

            var topColor = Color.FromHex("#f43f5e");       // 双顶活力绯红
            var topNeckColor = Color.FromHex("#fb7185");   // 双顶颈线柔粉
            var bottomColor = Color.FromHex("#10b981");    // 双底翡翠绿
            var bottomNeckColor = Color.FromHex("#34d399"); // 双底颈线碧绿
            var bgColor = Color.FromHex("#0f172a").WithAlpha(235); // 科技暗黑底

            foreach (var sig in signals)
            {
                bool isTop = sig.IsDoubleTop;
                var alertColor = isTop ? topColor : bottomColor;
                var neckColor = isTop ? topNeckColor : bottomNeckColor;

                double x1, xNeck, x2, xConfirm;

                if (isMacroChart)
                {
                    // 大周期 K 线图：按时间平滑对齐到大周期 Bar 坐标
                    x1 = sig.MacroX1;
                    xNeck = sig.MacroXNeck;
                    x2 = sig.MacroX2;
                    xConfirm = sig.MacroXConfirm;
                }
                else if (isRawTickChart || subBuckets == null || subBuckets.Count == 0)
                {
                    // 原始 Tick 走势折线图：直接使用 Tick 索引
                    x1 = sig.Top1TickIndex;
                    xNeck = sig.NeckTickIndex;
                    x2 = sig.Top2TickIndex;
                    xConfirm = sig.ConfirmTickIndex;
                }
                else
                {
                    // 微观子周期 K 线图：利用时间精确对齐到小周期桶 X 轴
                    x1 = TickPlotHelper.MapTimeToSubBucketX(sig.Top1Time, subBuckets);
                    xNeck = TickPlotHelper.MapTimeToSubBucketX(sig.NeckTime, subBuckets);
                    x2 = TickPlotHelper.MapTimeToSubBucketX(sig.Top2Time, subBuckets);
                    xConfirm = TickPlotHelper.MapTimeToSubBucketX(sig.ConfirmTime, subBuckets);
                }

                double y1 = (double)sig.Top1Price;
                double yNeck = (double)sig.NeckPrice;
                double y2 = (double)sig.Top2Price;
                double yConfirm = (double)sig.ConfirmPrice;

                // 1. 绘制切线区间的半透明微光警示条带
                double bandLeft = Math.Min(x1, xNeck) - 0.4;
                double bandRight = Math.Max(x2, xConfirm) + 1.2;
                var bandRect = plot.Add.Rectangle(bandLeft, bandRight, (double)sig.TangentLowerPrice, (double)sig.TangentUpperPrice);
                bandRect.FillColor = alertColor.WithAlpha(18);
                bandRect.LineColor = alertColor.WithAlpha(60);
                bandRect.LineWidth = 0.8f;
                bandRect.LinePattern = LinePattern.Dotted;

                // 2. 绘制 "M" 顶 / "W" 底特征骨架折线: Point1 -> Neck -> Point2 -> Confirm
                var line1 = plot.Add.Line(x1, y1, xNeck, yNeck);
                line1.Color = alertColor;
                line1.LineWidth = 2.2f;

                var line2 = plot.Add.Line(xNeck, yNeck, x2, y2);
                line2.Color = alertColor;
                line2.LineWidth = 2.2f;

                var line3 = plot.Add.Line(x2, y2, xConfirm, yConfirm);
                line3.Color = alertColor;
                line3.LineWidth = 2.2f;

                // 3. 绘制颈线水平虚线 (向右延展至突破确认区)
                double xForward = isRawTickChart ? Math.Max(16.0, (x2 - x1) * 0.45) : (isMacroChart ? 1.8 : 2.5);
                double neckRight = Math.Max(xConfirm, x2) + xForward;
                var neckLine = plot.Add.Line(x1 - 0.2, yNeck, neckRight, yNeck);
                neckLine.Color = neckColor;
                neckLine.LineWidth = 1.3f;
                neckLine.LinePattern = LinePattern.Dashed;

                // 4. 标注极值点与形态标记 (顶1/底1、颈线、顶2/底2)
                var m1 = plot.Add.Marker(x1, y1);
                m1.Shape = isTop ? MarkerShape.FilledTriangleDown : MarkerShape.FilledTriangleUp;
                m1.Size = 8;
                m1.Color = alertColor;

                var m2 = plot.Add.Marker(x2, y2);
                m2.Shape = isTop ? MarkerShape.FilledTriangleDown : MarkerShape.FilledTriangleUp;
                m2.Size = 9;
                m2.Color = alertColor;

                var mNeck = plot.Add.Marker(xNeck, yNeck);
                mNeck.Shape = MarkerShape.FilledCircle;
                mNeck.Size = 5;
                mNeck.Color = neckColor;

                // 4.1 核心强化：在图表（特别在 Tick 窗口）醒目标记出【双顶/双底判定点位】靶心复合标记
                // 外圈高亮光环
                var mConfirmRing = plot.Add.Marker(xConfirm, yConfirm);
                mConfirmRing.Shape = MarkerShape.OpenCircle;
                mConfirmRing.Size = 13;
                mConfirmRing.Color = alertColor;
                mConfirmRing.LineWidth = 2.0f;

                // 核心实心焦点
                var mConfirmCore = plot.Add.Marker(xConfirm, yConfirm);
                mConfirmCore.Shape = MarkerShape.FilledSquare;
                mConfirmCore.Size = 6;
                mConfirmCore.Color = Color.FromHex("#ffffff");

                // 4.2 绘制判定点位的水平参考基准线与价格浮标
                double confirmLineStart = Math.Min(x1, x2) - 0.2;
                double confirmLineEnd = xConfirm + xForward;
                var confirmLine = plot.Add.Line(confirmLineStart, yConfirm, confirmLineEnd, yConfirm);
                confirmLine.Color = alertColor.WithAlpha(170);
                confirmLine.LineWidth = 1.1f;
                confirmLine.LinePattern = LinePattern.Dotted;

                // 4.3 颈线至判定点位的突破/破位纵向引导虚线
                var triggerGuide = plot.Add.Line(xConfirm, yNeck, xConfirm, yConfirm);
                triggerGuide.Color = neckColor.WithAlpha(160);
                triggerGuide.LineWidth = 1.0f;
                triggerGuide.LinePattern = LinePattern.Dotted;

                // 5. 醒目标注文字标签
                string p1Label = isTop ? $"M1: {sig.Top1Price:F2}" : $"W1: {sig.Bottom1Price:F2}";
                var txtP1 = plot.Add.Text(p1Label, x1, y1);
                txtP1.LabelFontName = fontName;
                txtP1.LabelFontSize = 8.5f;
                txtP1.LabelBold = true;
                txtP1.LabelFontColor = alertColor;
                txtP1.LabelBackgroundColor = bgColor;
                txtP1.LabelBorderColor = alertColor;
                txtP1.LabelBorderWidth = 1f;
                txtP1.LabelAlignment = isTop ? Alignment.LowerCenter : Alignment.UpperCenter;

                string p2Label = isTop ? $"M2: {sig.Top2Price:F2}" : $"W2: {sig.Bottom2Price:F2}";
                var txtP2 = plot.Add.Text(p2Label, x2, y2);
                txtP2.LabelFontName = fontName;
                txtP2.LabelFontSize = 8.5f;
                txtP2.LabelBold = true;
                txtP2.LabelFontColor = alertColor;
                txtP2.LabelBackgroundColor = bgColor;
                txtP2.LabelBorderColor = alertColor;
                txtP2.LabelBorderWidth = 1f;
                txtP2.LabelAlignment = isTop ? Alignment.LowerCenter : Alignment.UpperCenter;

                // 颈线文字
                var txtNeck = plot.Add.Text($"颈线: {sig.NeckPrice:F2}", neckRight, yNeck);
                txtNeck.LabelFontName = fontName;
                txtNeck.LabelFontSize = 8.0f;
                txtNeck.LabelBold = true;
                txtNeck.LabelFontColor = neckColor;
                txtNeck.LabelBackgroundColor = bgColor;
                txtNeck.LabelBorderColor = neckColor;
                txtNeck.LabelBorderWidth = 1f;
                txtNeck.LabelAlignment = Alignment.MiddleLeft;



                // 判定点位专属高频信号警报徽章 (明确标注 🎯判定点位: 价格 及确立依据: 跌破颈线做空 / 突破颈线做多)
                string reasonTag = isTop ? "跌破颈线做空" : "突破颈线做多";
                string confirmBadgeText = $"🎯判定点位: {sig.ConfirmPrice:F2} [{reasonTag}, 价差:{sig.PullbackDepth:F2}]";

                Color badgeBgColor = isTop ? Color.FromHex("#e11d48") : Color.FromHex("#059669");
                Color badgeBorderColor = isTop ? Color.FromHex("#fda4af") : Color.FromHex("#6ee7b7");
                Alignment badgeAlign = isTop ? Alignment.UpperCenter : Alignment.LowerCenter;

                var txtSig = plot.Add.Text(confirmBadgeText, xConfirm, yConfirm);
                txtSig.LabelFontName = fontName;
                txtSig.LabelFontSize = 9.0f;
                txtSig.LabelBold = true;
                txtSig.LabelFontColor = Color.FromHex("#ffffff");
                txtSig.LabelBackgroundColor = badgeBgColor;
                txtSig.LabelBorderColor = badgeBorderColor;
                txtSig.LabelBorderWidth = 1.2f;
                txtSig.LabelAlignment = badgeAlign;
            }
        }
    }
}
