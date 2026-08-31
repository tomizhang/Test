using System;
using System.Collections.Generic;
using Test.PercentageBar.WinForms.Models;

namespace Test.PercentageBar.WinForms.Engine
{
    /// <summary>
    /// 高低点位类型 (高点 / 低点)
    /// </summary>
    public enum PivotPointType
    {
        /// <summary>
        /// 局部波峰 / 高点 (Swing High)
        /// </summary>
        High = 1,

        /// <summary>
        /// 局部波谷 / 低点 (Swing Low)
        /// </summary>
        Low = 2
    }

    /// <summary>
    /// 拐点高低点数据结构
    /// </summary>
    public readonly struct PivotPoint
    {
        public int BarIndex { get; init; }
        public decimal Price { get; init; }
        public PivotPointType Type { get; init; }
        public long Time { get; init; }
        public bool IsGlobalExtreme { get; init; }
        public decimal PriceChangeFromPrev { get; init; }
        public decimal PriceChangePctFromPrev { get; init; }
        public int BarsFromPrev { get; init; }
    }

    /// <summary>
    /// 高低点位计算分析报告
    /// </summary>
    public class PivotAnalysisResult
    {
        public List<PivotPoint> Pivots { get; } = new List<PivotPoint>();
        public (int BarIndex, decimal Price, long Time)? GlobalHigh { get; set; }
        public (int BarIndex, decimal Price, long Time)? GlobalLow { get; set; }
        public int TotalHighPivots { get; set; }
        public int TotalLowPivots { get; set; }
    }

    /// <summary>
    /// 高低点位与波段极值计算引擎 (支持局部拐点扫描、ZigZag 趋势交替滤波与全局历史极值检测)
    /// </summary>
    public static class PivotDetector
    {
        /// <summary>
        /// 计算并提取 K 线序列的高低极值点与波段拐点 (支持左右 N 根确认窗口与交替波段提取)
        /// </summary>
        public static PivotAnalysisResult CalculatePivots(
            IReadOnlyList<PercentageKline> bars,
            int window = 3,
            bool alternateHighLow = true)
        {
            var result = new PivotAnalysisResult();
            if (bars == null || bars.Count == 0) return result;

            int count = bars.Count;

            // 1. 全局最高点与全局最低点检测
            int globalHighIdx = 0;
            decimal globalHighPrice = bars[0].High;
            int globalLowIdx = 0;
            decimal globalLowPrice = bars[0].Low;

            for (int i = 0; i < count; i++)
            {
                if (bars[i].High > globalHighPrice)
                {
                    globalHighPrice = bars[i].High;
                    globalHighIdx = i;
                }
                if (bars[i].Low < globalLowPrice)
                {
                    globalLowPrice = bars[i].Low;
                    globalLowIdx = i;
                }
            }

            result.GlobalHigh = (globalHighIdx, globalHighPrice, bars[globalHighIdx].CloseTime);
            result.GlobalLow = (globalLowIdx, globalLowPrice, bars[globalLowIdx].CloseTime);

            if (count < 3) return result;

            int win = Math.Max(1, window);
            var rawPivots = new List<PivotPoint>();

            // 2. 局部波峰波谷扫描 (左右 win 根 Bar 极值判定)
            for (int i = 0; i < count; i++)
            {
                int left = Math.Max(0, i - win);
                int right = Math.Min(count - 1, i + win);

                bool isHigh = true;
                bool isLow = true;

                decimal curHigh = bars[i].High;
                decimal curLow = bars[i].Low;

                for (int j = left; j <= right; j++)
                {
                    if (j == i) continue;
                    if (bars[j].High > curHigh) isHigh = false;
                    if (bars[j].Low < curLow) isLow = false;
                }

                if (isHigh && !isLow)
                {
                    rawPivots.Add(new PivotPoint
                    {
                        BarIndex = i,
                        Price = curHigh,
                        Type = PivotPointType.High,
                        Time = bars[i].CloseTime,
                        IsGlobalExtreme = (i == globalHighIdx)
                    });
                }
                else if (isLow && !isHigh)
                {
                    rawPivots.Add(new PivotPoint
                    {
                        BarIndex = i,
                        Price = curLow,
                        Type = PivotPointType.Low,
                        Time = bars[i].CloseTime,
                        IsGlobalExtreme = (i == globalLowIdx)
                    });
                }
                else if (isHigh && isLow)
                {
                    // 单根独立大振幅 Bar，根据实体方向判定
                    var type = bars[i].Close >= bars[i].Open ? PivotPointType.High : PivotPointType.Low;
                    decimal price = type == PivotPointType.High ? curHigh : curLow;
                    rawPivots.Add(new PivotPoint
                    {
                        BarIndex = i,
                        Price = price,
                        Type = type,
                        Time = bars[i].CloseTime,
                        IsGlobalExtreme = (i == globalHighIdx || i == globalLowIdx)
                    });
                }
            }

            // 3. 交替滤波 (若开启 alternateHighLow，确保高点与低点严格交替，形成标准的 ZigZag 走势结构)
            List<PivotPoint> filteredPivots;
            if (alternateHighLow && rawPivots.Count > 1)
            {
                filteredPivots = new List<PivotPoint>(rawPivots.Count);
                PivotPoint current = rawPivots[0];

                for (int i = 1; i < rawPivots.Count; i++)
                {
                    var next = rawPivots[i];
                    if (next.Type == current.Type)
                    {
                        // 相同类型连续出现：保留更极端的点
                        if (current.Type == PivotPointType.High)
                        {
                            if (next.Price >= current.Price) current = next;
                        }
                        else
                        {
                            if (next.Price <= current.Price) current = next;
                        }
                    }
                    else
                    {
                        filteredPivots.Add(current);
                        current = next;
                    }
                }
                filteredPivots.Add(current);
            }
            else
            {
                filteredPivots = rawPivots;
            }

            // 4. 计算相邻拐点之间的价差与幅度
            for (int i = 0; i < filteredPivots.Count; i++)
            {
                var p = filteredPivots[i];
                decimal diff = 0;
                decimal diffPct = 0;
                int barsDiff = 0;

                if (i > 0)
                {
                    var prev = filteredPivots[i - 1];
                    diff = p.Price - prev.Price;
                    diffPct = prev.Price > 0 ? (diff / prev.Price) * 100m : 0m;
                    barsDiff = p.BarIndex - prev.BarIndex;
                }

                var populated = new PivotPoint
                {
                    BarIndex = p.BarIndex,
                    Price = p.Price,
                    Type = p.Type,
                    Time = p.Time,
                    IsGlobalExtreme = p.IsGlobalExtreme,
                    PriceChangeFromPrev = diff,
                    PriceChangePctFromPrev = diffPct,
                    BarsFromPrev = barsDiff
                };

                result.Pivots.Add(populated);
                if (p.Type == PivotPointType.High) result.TotalHighPivots++;
                else result.TotalLowPivots++;
            }

            return result;
        }

        /// <summary>
        /// 趋势线交互生成与穿透过滤引擎：
        /// 1. 所有历史高低点与新产生的高低点进行两两交互配对生成多条候选趋势线
        /// 2. 严格检查区间内部与区间之后的所有 K 线，若被 K 线触碰穿透则立即删除剔除
        /// 3. 保留并输出未被穿透的有效活跃趋势线（所有 3点及以上高精度共线趋势线 100% 强制保留）
        /// </summary>
        public static List<TrendLine> CalculateTrendLines(
            IReadOnlyList<PercentageKline> bars,
            PivotAnalysisResult pivotResult,
            int maxLines = 1000,
            int maxSpanBars = 1000,
            int extensionBars = 8,
            bool strictWickPenetration = true,
            decimal touchTolerancePct = 0.0003m) 
        {
            var validTrendLines = new List<TrendLine>(Math.Min(1024, maxLines));
            if (bars == null || bars.Count < 3 || pivotResult == null || pivotResult.Pivots.Count < 2)
                return validTrendLines;

            int lastBarIdx = bars.Count - 1;
            int extIdx = lastBarIdx + Math.Max(3, extensionBars);
            decimal tol = Math.Max(0.00005m, touchTolerancePct);

            var highPivots = pivotResult.Pivots.Where(p => p.Type == PivotPointType.High).ToList();
            var lowPivots = pivotResult.Pivots.Where(p => p.Type == PivotPointType.Low).ToList();

            var highPivotDict = new Dictionary<int, PivotPoint>(highPivots.Count);
            foreach (var p in highPivots) highPivotDict[p.BarIndex] = p;

            var lowPivotDict = new Dictionary<int, PivotPoint>(lowPivots.Count);
            foreach (var p in lowPivots) lowPivotDict[p.BarIndex] = p;

            var candidateResistance = new List<TrendLine>();
            var candidateSupport = new List<TrendLine>();

            if (highPivots.Count >= 2)
            {
                for (int i = 0; i < highPivots.Count - 1; i++)
                {
                    var p1 = highPivots[i];
                    int x1 = p1.BarIndex;
                    decimal y1 = p1.Price;

                    for (int j = i + 1; j < highPivots.Count; j++)
                    {
                        var p2 = highPivots[j];
                        int x2 = p2.BarIndex;
                        decimal y2 = p2.Price;

                        int span = x2 - x1;
                        if (span <= 0) continue;
                        if (span > maxSpanBars) break;

                        decimal slope = (y2 - y1) / span;

                        var touchBars = new List<int> { x1 };
                        int breakBarIdx = -1;

                        for (int m = x1 + 1; m <= lastBarIdx; m++)
                        {
                            decimal lineY = y1 + slope * (m - x1);
                            decimal checkPrice = strictWickPenetration ? bars[m].High : bars[m].Close;

                            if (checkPrice > lineY + 0.000001m)
                            {
                                breakBarIdx = m;
                                break;
                            }

                            if (highPivotDict.TryGetValue(m, out var hp))
                            {
                                if (lineY > 0 && Math.Abs(hp.Price - lineY) / lineY <= tol)
                                {
                                    touchBars.Add(m);
                                }
                            }
                        }

                        if (breakBarIdx > 0 && breakBarIdx <= x2) continue;

                        bool isThreePoint = touchBars.Count >= 3;
                        int finalStartIdx = touchBars[0];
                        int finalEndIdx = touchBars[touchBars.Count - 1];
                        decimal finalStartPrice = y1 + slope * (finalStartIdx - x1);
                        decimal finalEndPrice = y1 + slope * (finalEndIdx - x1);

                        if (breakBarIdx > 0)
                        {
                            if (!isThreePoint) continue;
                            candidateResistance.Add(new TrendLine
                            {
                                StartBarIndex = finalStartIdx,
                                StartPrice = finalStartPrice,
                                StartTime = p1.Time,
                                EndBarIndex = finalEndIdx,
                                EndPrice = finalEndPrice,
                                EndTime = bars[finalEndIdx].CloseTime,
                                Slope = slope,
                                Type = TrendLineType.Resistance,
                                ExtendedBarIndex = breakBarIdx,
                                ExtendedPrice = y1 + slope * (breakBarIdx - x1),
                                CurrentBarPrice = y1 + slope * (lastBarIdx - x1),
                                SpanBars = finalEndIdx - finalStartIdx,
                                AgeBars = breakBarIdx - finalEndIdx,
                                TouchCount = touchBars.Count,
                                TouchBarIndices = touchBars.ToArray(),
                                BreakBarIndex = breakBarIdx,
                                IsBroken = true
                            });
                        }
                        else
                        {
                            candidateResistance.Add(new TrendLine
                            {
                                StartBarIndex = finalStartIdx,
                                StartPrice = finalStartPrice,
                                StartTime = p1.Time,
                                EndBarIndex = finalEndIdx,
                                EndPrice = finalEndPrice,
                                EndTime = bars[finalEndIdx].CloseTime,
                                Slope = slope,
                                Type = TrendLineType.Resistance,
                                ExtendedBarIndex = extIdx,
                                ExtendedPrice = y1 + slope * (extIdx - x1),
                                CurrentBarPrice = y1 + slope * (lastBarIdx - x1),
                                SpanBars = finalEndIdx - finalStartIdx,
                                AgeBars = lastBarIdx - finalEndIdx,
                                TouchCount = touchBars.Count,
                                TouchBarIndices = touchBars.ToArray(),
                                BreakBarIndex = -1,
                                IsBroken = false
                            });
                        }
                    }
                }
            }

            // 2. 低点交互配对生成支撑趋势线 (Support Trendlines，严格单向时间流前向扫描与穿透即刻冻结)
            if (lowPivots.Count >= 2)
            {
                for (int i = 0; i < lowPivots.Count - 1; i++)
                {
                    var q1 = lowPivots[i];
                    int x1 = q1.BarIndex;
                    decimal y1 = q1.Price;

                    for (int j = i + 1; j < lowPivots.Count; j++)
                    {
                        var q2 = lowPivots[j];
                        int x2 = q2.BarIndex;
                        decimal y2 = q2.Price;

                        int span = x2 - x1;
                        if (span <= 0) continue;
                        if (span > maxSpanBars) break;

                        decimal slope = (y2 - y1) / span;

                        var touchBars = new List<int> { x1 };
                        int breakBarIdx = -1;

                        for (int m = x1 + 1; m <= lastBarIdx; m++)
                        {
                            decimal lineY = y1 + slope * (m - x1);
                            decimal checkPrice = strictWickPenetration ? bars[m].Low : bars[m].Close;

                            // 1. 穿透判定：一旦在 Bar m 处被向下跌破穿透，立即终止扫描！
                            // 【核心保证】：后续所有 K 线与点位绝不再参与该趋势线的任何计算！
                            if (checkPrice < lineY - 0.000001m)
                            {
                                breakBarIdx = m;
                                break; // ⚡ 首次被穿透，生命周期彻底终结，时间流立即停止！
                            }

                            // 2. 触碰判定：仅在尚未发生任何穿透的连续健康区间内，统计低点极值点触碰
                            if (lowPivotDict.TryGetValue(m, out var lp))
                            {
                                if (lineY > 0 && Math.Abs(lp.Price - lineY) / lineY <= tol)
                                {
                                    touchBars.Add(m);
                                }
                            }
                        }

                        // 如果在到达锚定点 x2 之前就已经被击穿，说明内部已被破坏，直接舍弃
                        if (breakBarIdx > 0 && breakBarIdx <= x2)
                        {
                            continue;
                        }

                        bool isThreePoint = touchBars.Count >= 3;
                        int finalStartIdx = touchBars[0];
                        int finalEndIdx = touchBars[touchBars.Count - 1];
                        decimal finalStartPrice = y1 + slope * (finalStartIdx - x1);
                        decimal finalEndPrice = y1 + slope * (finalEndIdx - x1);

                        if (breakBarIdx > 0)
                        {
                            // 发生穿透：普通 2点线直接删除抛弃；穿透前已达成 3点+ 的紫色线保留（射线精准截断在破坏点 breakBarIdx）
                            if (!isThreePoint) continue;

                            candidateSupport.Add(new TrendLine
                            {
                                StartBarIndex = finalStartIdx,
                                StartPrice = finalStartPrice,
                                StartTime = q1.Time,
                                EndBarIndex = finalEndIdx,
                                EndPrice = finalEndPrice,
                                EndTime = bars[finalEndIdx].CloseTime,
                                Slope = slope,
                                Type = TrendLineType.Support,
                                ExtendedBarIndex = breakBarIdx, // 射线截断在被穿透的 Bar 处，不再向后延伸
                                ExtendedPrice = y1 + slope * (breakBarIdx - x1),
                                CurrentBarPrice = y1 + slope * (lastBarIdx - x1),
                                SpanBars = finalEndIdx - finalStartIdx,
                                AgeBars = breakBarIdx - finalEndIdx,
                                TouchCount = touchBars.Count,
                                TouchBarIndices = touchBars.ToArray(),
                                BreakBarIndex = breakBarIdx,
                                IsBroken = true
                            });
                        }
                        else
                        {
                            // 未被穿透：健康活跃趋势线，正常延伸至最新与未来
                            candidateSupport.Add(new TrendLine
                            {
                                StartBarIndex = finalStartIdx,
                                StartPrice = finalStartPrice,
                                StartTime = q1.Time,
                                EndBarIndex = finalEndIdx,
                                EndPrice = finalEndPrice,
                                EndTime = bars[finalEndIdx].CloseTime,
                                Slope = slope,
                                Type = TrendLineType.Support,
                                ExtendedBarIndex = extIdx,
                                ExtendedPrice = y1 + slope * (extIdx - x1),
                                CurrentBarPrice = y1 + slope * (lastBarIdx - x1),
                                SpanBars = finalEndIdx - finalStartIdx,
                                AgeBars = lastBarIdx - finalEndIdx,
                                TouchCount = touchBars.Count,
                                TouchBarIndices = touchBars.ToArray(),
                                BreakBarIndex = -1,
                                IsBroken = false
                            });
                        }
                    }
                }
            }

            // 3. 汇总并控制最大保留容量 (所有 3点及以上共线趋势线去重并 100% 优先保留，剩余名额分配给最新 2点线)
            var confirmed3PointLines = new List<TrendLine>();
            var regular2PointRes = new List<TrendLine>();
            var regular2PointSup = new List<TrendLine>();
            var unique3PointKeys = new HashSet<string>();

            foreach (var tl in candidateResistance)
            {
                if (tl.IsThreePointConfirmed)
                {
                    string key = "RES_" + string.Join("-", tl.TouchBarIndices ?? Array.Empty<int>());
                    if (unique3PointKeys.Add(key))
                    {
                        confirmed3PointLines.Add(tl);
                    }
                }
                else regular2PointRes.Add(tl);
            }

            foreach (var tl in candidateSupport)
            {
                if (tl.IsThreePointConfirmed)
                {
                    string key = "SUP_" + string.Join("-", tl.TouchBarIndices ?? Array.Empty<int>());
                    if (unique3PointKeys.Add(key))
                    {
                        confirmed3PointLines.Add(tl);
                    }
                }
                else regular2PointSup.Add(tl);
            }

            // 先将所有 3点+ 强趋势线全部保留
            validTrendLines.AddRange(confirmed3PointLines);

            // 剩余名额分配给 2点趋势线 (至少保证总容量保留到 maxLines)
            int remainingCapacity = Math.Max(0, maxLines - validTrendLines.Count);
            int halfRemaining = remainingCapacity / 2;
            int resTake = Math.Min(regular2PointRes.Count, halfRemaining);
            int supTake = Math.Min(regular2PointSup.Count, remainingCapacity - resTake);
            if (regular2PointRes.Count > resTake && regular2PointSup.Count < halfRemaining)
            {
                resTake = Math.Min(regular2PointRes.Count, remainingCapacity - regular2PointSup.Count);
            }

            for (int i = 0; i < resTake; i++)
            {
                validTrendLines.Add(regular2PointRes[i]);
            }
            for (int i = 0; i < supTake; i++)
            {
                validTrendLines.Add(regular2PointSup[i]);
            }

            return validTrendLines;
        }
    }

    /// <summary>
    /// 趋势线类型 (高点阻力线 / 低点支撑线)
    /// </summary>
    public enum TrendLineType
    {
        /// <summary>
        /// 阻力趋势线 (由高点连接并向右延伸)
        /// </summary>
        Resistance = 1,

        /// <summary>
        /// 支撑趋势线 (由低点连接并向右延伸)
        /// </summary>
        Support = 2
    }

    /// <summary>
    /// 自动高低点趋势线数据结构 (支持 3点及以上共线强趋势线标识)
    /// </summary>
    public readonly struct TrendLine
    {
        public int StartBarIndex { get; init; }
        public decimal StartPrice { get; init; }
        public long StartTime { get; init; }
        public int EndBarIndex { get; init; }
        public decimal EndPrice { get; init; }
        public long EndTime { get; init; }
        public decimal Slope { get; init; }
        public TrendLineType Type { get; init; }
        public int ExtendedBarIndex { get; init; }
        public decimal ExtendedPrice { get; init; }
        public decimal CurrentBarPrice { get; init; }
        public int SpanBars { get; init; }
        public int AgeBars { get; init; }
        public int TouchCount { get; init; } // 触碰/共线极值点数量 (>= 3 为高强度多点共线趋势线)
        public int[]? TouchBarIndices { get; init; } // 共线极值点 Bar 序号列表
        public int BreakBarIndex { get; init; } // 首次被 K 线穿透破坏的 Bar 序号 (-1 表示尚未被穿透)
        public bool IsBroken { get; init; } // 是否在后续行进中被 K 线穿透突破

        public bool IsThreePointConfirmed => TouchCount >= 3;

        public bool IsMatching(int startIdx, int endIdx, TrendLineType type) =>
            StartBarIndex == startIdx && EndBarIndex == endIdx && Type == type;
    }
}
