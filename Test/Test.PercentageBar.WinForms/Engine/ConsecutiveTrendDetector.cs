using System;
using System.Collections.Generic;
using Test.PercentageBar.WinForms.Models;

namespace Test.PercentageBar.WinForms.Engine
{
    /// <summary>
    /// 连续上涨 / 连续下跌动能波段专业识别引擎 (专为 PercentageKline 设计)
    /// 核心逻辑：
    /// 1. 连续上涨：至少连续 5 根及以上收盘走高/收阳，且累计涨幅达到 2.5% 及以上
    /// 2. 连续下跌：至少连续 5 根及以上收盘走低/收阴，且累计跌幅达到 2.5% 及以上
    /// 3. 动态展开：一旦首次满足门槛立即确立，并在行情延续时动态向右扩展，走势反转时锁定终止
    /// 4. 零未来函数：基准通道严格在达成门槛点 [streakStart, confirmedAt] 锁定，无需知晓后续 K 线
    /// 5. 时间轴回滚：支持 SyncTo 历史时间轴回滚与流式推演
    /// </summary>
    public class ConsecutiveTrendDetector
    {
        private readonly List<ConsecutiveTrendItem> _detectedTrends = new();
        private bool _enableDetection = true;
        private int _minBars = 5;
        private decimal _minPriceChangePct = 2.5m;

        /// <summary>
        /// 当前已识别并确认的所有连续涨跌波段列表
        /// </summary>
        public IReadOnlyList<ConsecutiveTrendItem> DetectedTrends => _detectedTrends;

        /// <summary>
        /// 是否开启连续形态识别
        /// </summary>
        public bool EnableDetection
        {
            get => _enableDetection;
            set => _enableDetection = value;
        }

        /// <summary>
        /// 要求的最小连续 K 线根数门槛 (默认 5 根)
        /// </summary>
        public int MinBars
        {
            get => _minBars;
            set => _minBars = Math.Max(2, value);
        }

        /// <summary>
        /// 要求的累计涨跌幅百分比门槛 (默认 2.5%)
        /// </summary>
        public decimal MinPriceChangePct
        {
            get => _minPriceChangePct;
            set => _minPriceChangePct = Math.Max(0m, value);
        }

        private int _maxRecentTrendLines = 10;

        /// <summary>
        /// 每个连续形态附近最多生成的近期趋势线数量 (默认 10 条)
        /// </summary>
        public int MaxRecentTrendLines
        {
            get => _maxRecentTrendLines;
            set => _maxRecentTrendLines = Math.Max(2, value);
        }

        private int _maxMacroLookback = 1000;

        /// <summary>
        /// 连涨/连跌高低点位交互绘制的最大回溯 K 线根数 (默认 1000 根)
        /// </summary>
        public int MaxMacroLookback
        {
            get => _maxMacroLookback;
            set => _maxMacroLookback = Math.Max(10, value);
        }

        private int _maxMacroLines = 6;

        /// <summary>
        /// 连涨/连跌高低点位交互趋势线最大生成条数 (默认 6 条)
        /// </summary>
        public int MaxMacroLines
        {
            get => _maxMacroLines;
            set => _maxMacroLines = Math.Max(1, value);
        }

        private decimal _touchTolerancePct = 0.0005m;

        /// <summary>
        /// 第 3 点落入趋势线的容差百分比阈值 (默认 0.05% 即 0.0005m，满足时显示为粉红色)
        /// </summary>
        public decimal TouchTolerancePct
        {
            get => _touchTolerancePct;
            set => _touchTolerancePct = Math.Max(0.00001m, value);
        }

        /// <summary>
        /// 连续波段确立事件委托 (波段对象, 首次确认 K 线索引)
        /// </summary>
        public event Action<ConsecutiveTrendItem, int>? OnTrendDetected;

        /// <summary>
        /// 重置所有状态与已识别形态
        /// </summary>
        public void Reset()
        {
            _detectedTrends.Clear();
        }

        /// <summary>
        /// 时间轴跳转或回退
        /// </summary>
        public void SyncTo(int targetBarIndex)
        {
            if (targetBarIndex < 0)
            {
                Reset();
                return;
            }

            // 移除在 targetBarIndex 之后才被首次确认的形态
            _detectedTrends.RemoveAll(t => t.ConfirmedBarIndex > targetBarIndex);

            // 对保留下来的形态，截断其截止位置
            foreach (var t in _detectedTrends)
            {
                if (t.EndIndex > targetBarIndex)
                {
                    t.EndIndex = targetBarIndex;
                    t.IsActive = true;
                }
            }
        }

        /// <summary>
        /// 针对当前推进到的最新 K 线序列，全量自适应扫描并更新形态列表
        /// </summary>
        public void ProcessCurrentSequence(IReadOnlyList<PercentageKline> allKlines, int currentBarIndex)
        {
            if (!_enableDetection || allKlines == null || currentBarIndex < _minBars - 1)
            {
                _detectedTrends.Clear();
                return;
            }

            int maxIndex = Math.Clamp(currentBarIndex, 0, allKlines.Count - 1);
            var scanned = ScanTrends(allKlines, maxIndex, _minBars, _minPriceChangePct, _maxRecentTrendLines, _maxMacroLookback, _maxMacroLines, _touchTolerancePct);

            // 比对新触发的确认事件
            var existingIds = new HashSet<int>();
            for (int i = 0; i < _detectedTrends.Count; i++) existingIds.Add(_detectedTrends[i].Id);

            _detectedTrends.Clear();
            _detectedTrends.AddRange(scanned);

            for (int i = 0; i < scanned.Count; i++)
            {
                var t = scanned[i];
                if (!existingIds.Contains(t.Id))
                {
                    OnTrendDetected?.Invoke(t, t.ConfirmedBarIndex);
                }
            }
        }

        /// <summary>
        /// 静态扫描算法：以 O(N) 线性遍历精准识别所有达标连续涨跌波段
        /// </summary>
        public static List<ConsecutiveTrendItem> ScanTrends(
            IReadOnlyList<PercentageKline> klines,
            int maxIndex,
            int minBars,
            decimal minChangePct,
            int maxRecentTrendLines = 10,
            int maxMacroLookback = 1000,
            int maxMacroLines = 6,
            decimal touchTolerancePct = 0.0005m)
        {
            var result = new List<ConsecutiveTrendItem>();
            if (klines == null || maxIndex < minBars - 1) return result;

            int n = maxIndex + 1;
            int i = 0;

            while (i < n)
            {
                // 检查第 i 根 K 线是上涨还是下跌 (严格根据当前已走完的 K 线，零未来函数)
                int currentStreakDir = 0;
                if (IsRisingBar(klines, i)) currentStreakDir = 1;
                else if (IsFallingBar(klines, i)) currentStreakDir = -1;
                else
                {
                    i++;
                    continue;
                }

                int streakStart = i; // 连涨/连跌的第一根达标 K 线
                // 基准启动价格：优先取该波段启动前一根的收盘价，若不存在则取第一根的开盘价
                decimal startPrice = streakStart > 0 ? klines[streakStart - 1].Close : klines[streakStart].Open;
                if (startPrice <= 0m) startPrice = klines[streakStart].Open;

                int confirmedAt = -1;
                int j = i;

                while (j < n)
                {
                    bool continues = currentStreakDir == 1 ? IsRisingBar(klines, j) : IsFallingBar(klines, j);
                    if (!continues)
                    {
                        break;
                    }

                    int countSoFar = j - streakStart + 1;
                    decimal endPrice = klines[j].Close;
                    decimal changePct = startPrice > 0m
                        ? ((endPrice - startPrice) / startPrice) * 100m
                        : 0m;

                    decimal absChange = Math.Abs(changePct);
                    // 一旦达到最小根数门槛 (默认 5 根) 且累计幅度达到门槛 (默认 2.5%)，立即在当根确立！
                    if (countSoFar >= minBars && absChange >= minChangePct && confirmedAt < 0)
                    {
                        confirmedAt = j;
                    }

                    j++;
                }

                int streakEnd = j - 1;
                int totalCount = streakEnd - streakStart + 1;
                decimal finalEndPrice = klines[streakEnd].Close;
                decimal finalChangePct = startPrice > 0m
                    ? ((finalEndPrice - startPrice) / startPrice) * 100m
                    : 0m;

                if (confirmedAt >= 0 && totalCount >= minBars && Math.Abs(finalChangePct) >= minChangePct)
                {
                    // 若当前连涨/连跌延续到了最新推进的 K 线 (maxIndex)，则处于活跃延伸状态
                    bool isActive = streakEnd == maxIndex;

                    // 确定性稳定 ID，保证在后续帧动态延伸时不重复触发确立事件
                    int stableId = (currentStreakDir > 0 ? 1000000 : 2000000) + streakStart;

                    // 核心关键：平行通道基准形态严格在首次达到门槛的 K 线 [streakStart, confirmedAt]（例如正好第 5 根）确立！
                    // 无需知道下一根（第 6 根、第 7 根...），在第 5 根闭合时通道即刻完全成型并立马绘制！
                    FitParallelChannel(klines, streakStart, confirmedAt, out decimal slopeK, out decimal upperB, out decimal lowerB);

                    var item = new ConsecutiveTrendItem
                    {
                        Id = stableId,
                        Type = currentStreakDir == 1 ? ConsecutiveTrendType.Bullish : ConsecutiveTrendType.Bearish,
                        StartIndex = streakStart,
                        StartPrice = startPrice,
                        EndIndex = streakEnd,
                        EndPrice = finalEndPrice,
                        PriceChangePct = finalChangePct,
                        ConfirmedBarIndex = confirmedAt,
                        IsActive = isActive,
                        SlopeK = slopeK,
                        UpperIntercept = upperB,
                        LowerIntercept = lowerB,
                        HasChannel = upperB > lowerB
                    };

                    // 零未来函数：在首次满足门槛确立点 [streakStart, confirmedAt] 锁定近期主趋势线及前序突破线
                    CalculateRecentTrendLines(klines, item, maxRecentTrendLines, touchTolerancePct);

                    // 零未来函数：在当前波段最新推进 K 线与前序 1000 根可能的高低点位交互绘制宏观趋势线 (连涨交互高点，连跌交互低点)
                    Calculate1000BarInteractiveTrendLines(klines, item, maxMacroLookback, maxMacroLines, touchTolerancePct);

                    result.Add(item);
                }

                // 下一次从断点 j 开始继续寻找下一个趋势波段
                i = j;
            }

            return result;
        }

        /// <summary>
        /// 针对指定连续 K 线区间拟合严格平行的外包络通道
        /// 核心：通过线性回归计算波段整体斜率 k，并通过最大 High 差值与最小 Low 差值确定平行的上轨与下轨
        /// </summary>
        public static void FitParallelChannel(
            IReadOnlyList<PercentageKline> klines,
            int startIndex,
            int endIndex,
            out decimal slopeK,
            out decimal upperB,
            out decimal lowerB)
        {
            slopeK = 0m;
            upperB = 0m;
            lowerB = 0m;
            if (klines == null || startIndex < 0 || endIndex >= klines.Count || endIndex < startIndex)
            {
                return;
            }

            int n = endIndex - startIndex + 1;
            if (n < 2)
            {
                upperB = klines[startIndex].High;
                lowerB = klines[startIndex].Low;
                return;
            }

            decimal sumX = 0m;
            decimal sumY = 0m;
            for (int idx = startIndex; idx <= endIndex; idx++)
            {
                sumX += idx;
                sumY += (klines[idx].High + klines[idx].Low) / 2m;
            }
            decimal meanX = sumX / n;
            decimal meanY = sumY / n;

            decimal num = 0m;
            decimal den = 0m;
            for (int idx = startIndex; idx <= endIndex; idx++)
            {
                decimal dx = idx - meanX;
                decimal dy = ((klines[idx].High + klines[idx].Low) / 2m) - meanY;
                num += dx * dy;
                den += dx * dx;
            }

            slopeK = den != 0m ? num / den : 0m;

            decimal maxDiffH = decimal.MinValue;
            decimal minDiffL = decimal.MaxValue;
            for (int idx = startIndex; idx <= endIndex; idx++)
            {
                decimal diffH = klines[idx].High - slopeK * idx;
                decimal diffL = klines[idx].Low - slopeK * idx;
                if (diffH > maxDiffH) maxDiffH = diffH;
                if (diffL < minDiffL) minDiffL = diffL;
            }

            upperB = maxDiffH;
            lowerB = minDiffL;
        }

        /// <summary>
        /// 针对达到门槛确立的连续波段，计算近期趋势线及前序突破结构趋势线
        /// 零未来函数：仅基于 [startIndex, confirmedAt] 之间已走完的 K 线确立斜率，不依赖后续 K 线
        /// </summary>
        /// <summary>
        /// 针对达到门槛确立的连续波段，计算附近多维立体近期趋势线集合 (涵盖主支撑、主阻力、动能中枢、前序波峰波谷突破、收敛楔形及大级别极值映射)
        /// 零未来函数：仅基于 [startIndex, confirmedAt] 之间已走完的 K 线确立斜率，不依赖后续 K 线
        /// </summary>
        public static void CalculateRecentTrendLines(
            IReadOnlyList<PercentageKline> klines,
            ConsecutiveTrendItem item,
            int maxLines = 10,
            decimal touchTolerancePct = 0.0005m)
        {
            if (klines == null || item == null || item.ConfirmedBarIndex < item.StartIndex || item.ConfirmedBarIndex >= klines.Count)
            {
                return;
            }

            item.RecentTrendLines.Clear();

            int startIdx = item.StartIndex;
            int confIdx = item.ConfirmedBarIndex;
            if (confIdx <= startIdx)
            {
                return;
            }

            bool isBull = item.Type == ConsecutiveTrendType.Bullish;
            int totalBars = klines.Count;
            int rayEndTarget = Math.Min(totalBars - 1, item.EndIndex + 12);

            // =========================================================================
            // 1. 本次连涨/连跌：波段主支撑线 (Streak Lower Support)
            // =========================================================================
            {
                decimal startY = klines[startIdx].Low;
                int bestAnchor = confIdx;
                decimal bestSlope = 0m;
                bool foundValid = false;

                if (isBull)
                {
                    // 连涨下凸包络支撑线：寻找在 [startIdx + 1, confIdx] 间斜率最小的正斜率
                    decimal minPositiveSlope = decimal.MaxValue;
                    int minSlopeIdx = -1;

                    for (int j = startIdx + 1; j <= confIdx; j++)
                    {
                        decimal dy = klines[j].Low - startY;
                        decimal dx = j - startIdx;
                        decimal slope = dy / dx;
                        if (slope > 0m && slope < minPositiveSlope)
                        {
                            minPositiveSlope = slope;
                            minSlopeIdx = j;
                        }
                    }

                    if (minSlopeIdx > startIdx)
                    {
                        bestAnchor = minSlopeIdx;
                        bestSlope = minPositiveSlope;
                        foundValid = true;
                    }
                    else
                    {
                        bestAnchor = confIdx;
                        bestSlope = (klines[confIdx].Low - startY) / (confIdx - startIdx);
                        foundValid = true;
                    }
                }
                else
                {
                    // 连跌下轨恐慌底线：寻找在 [startIdx + 1, confIdx] 间斜率最陡的负斜率 (最小负值)
                    decimal minNegativeSlope = decimal.MaxValue;
                    int minSlopeIdx = -1;

                    for (int j = startIdx + 1; j <= confIdx; j++)
                    {
                        decimal dy = klines[j].Low - startY;
                        decimal dx = j - startIdx;
                        decimal slope = dy / dx;
                        if (slope < 0m && slope < minNegativeSlope)
                        {
                            minNegativeSlope = slope;
                            minSlopeIdx = j;
                        }
                    }

                    if (minSlopeIdx > startIdx)
                    {
                        bestAnchor = minSlopeIdx;
                        bestSlope = minNegativeSlope;
                        foundValid = true;
                    }
                    else
                    {
                        bestAnchor = confIdx;
                        bestSlope = (klines[confIdx].Low - startY) / (confIdx - startIdx);
                        foundValid = true;
                    }
                }

                if (foundValid)
                {
                    int rEnd = Math.Max(bestAnchor, rayEndTarget);
                    decimal rPrice = startY + bestSlope * (rEnd - startIdx);

                    var tlSup = new ConsecutiveRecentTrendLine
                    {
                        LineType = ConsecutiveRecentTrendLineType.StreakSupport,
                        Name = isBull ? "连涨下轨主支撑线" : "连跌下轨恐慌底线",
                        StartX = startIdx,
                        StartY = startY,
                        EndX = bestAnchor,
                        EndY = klines[bestAnchor].Low,
                        Slope = bestSlope,
                        RayEndX = rEnd,
                        RayEndY = rPrice,
                        ColorHex = isBull ? "#38bdf8" : "#fb7185", // 天蓝 / 浅玫瑰
                        LineWidth = isBull ? 1.4f : 1.0f,
                        TagText = isBull ? $"📈 连涨支撑: {rPrice:F2}" : $"📉 连跌下轨: {rPrice:F2}",
                        IsPrimary = isBull
                    };
                    item.RecentTrendLines.Add(tlSup);

                    if (isBull)
                    {
                        item.HasTrendLine = true;
                        item.TrendLineStartX = startIdx;
                        item.TrendLineStartY = startY;
                        item.TrendLineEndX = bestAnchor;
                        item.TrendLineEndY = klines[bestAnchor].Low;
                        item.TrendLineSlope = bestSlope;
                    }
                }
            }

            // =========================================================================
            // 2. 本次连涨/连跌：波段主阻力/天花板线 (Streak Upper Resistance)
            // =========================================================================
            {
                decimal startY = klines[startIdx].High;
                int bestAnchor = confIdx;
                decimal bestSlope = 0m;
                bool foundValid = false;

                if (!isBull)
                {
                    // 连跌上凸包络阻力线：寻找在 [startIdx + 1, confIdx] 间最大负斜率 (负数中绝对值最小者)
                    decimal maxNegativeSlope = decimal.MinValue;
                    int maxSlopeIdx = -1;

                    for (int j = startIdx + 1; j <= confIdx; j++)
                    {
                        decimal dy = klines[j].High - startY;
                        decimal dx = j - startIdx;
                        decimal slope = dy / dx;
                        if (slope < 0m && slope > maxNegativeSlope)
                        {
                            maxNegativeSlope = slope;
                            maxSlopeIdx = j;
                        }
                    }

                    if (maxSlopeIdx > startIdx)
                    {
                        bestAnchor = maxSlopeIdx;
                        bestSlope = maxNegativeSlope;
                        foundValid = true;
                    }
                    else
                    {
                        bestAnchor = confIdx;
                        bestSlope = (klines[confIdx].High - startY) / (confIdx - startIdx);
                        foundValid = true;
                    }
                }
                else
                {
                    // 连涨加速天花板线：寻找在 [startIdx + 1, confIdx] 间最大正斜率
                    decimal maxPositiveSlope = decimal.MinValue;
                    int maxSlopeIdx = -1;

                    for (int j = startIdx + 1; j <= confIdx; j++)
                    {
                        decimal dy = klines[j].High - startY;
                        decimal dx = j - startIdx;
                        decimal slope = dy / dx;
                        if (slope > 0m && slope > maxPositiveSlope)
                        {
                            maxPositiveSlope = slope;
                            maxSlopeIdx = j;
                        }
                    }

                    if (maxSlopeIdx > startIdx)
                    {
                        bestAnchor = maxSlopeIdx;
                        bestSlope = maxPositiveSlope;
                        foundValid = true;
                    }
                    else
                    {
                        bestAnchor = confIdx;
                        bestSlope = (klines[confIdx].High - startY) / (confIdx - startIdx);
                        foundValid = true;
                    }
                }

                if (foundValid)
                {
                    int rEnd = Math.Max(bestAnchor, rayEndTarget);
                    decimal rPrice = startY + bestSlope * (rEnd - startIdx);

                    var tlRes = new ConsecutiveRecentTrendLine
                    {
                        LineType = ConsecutiveRecentTrendLineType.StreakResistance,
                        Name = isBull ? "连涨上轨加速天花板" : "连跌上轨主阻力线",
                        StartX = startIdx,
                        StartY = startY,
                        EndX = bestAnchor,
                        EndY = klines[bestAnchor].High,
                        Slope = bestSlope,
                        RayEndX = rEnd,
                        RayEndY = rPrice,
                        ColorHex = isBull ? "#06b6d4" : "#f43f5e", // 青蓝 / 玫瑰红
                        LineWidth = !isBull ? 1.4f : 1.0f,
                        TagText = isBull ? $"🚀 连涨天花板: {rPrice:F2}" : $"📉 连跌阻力: {rPrice:F2}",
                        IsPrimary = !isBull
                    };
                    item.RecentTrendLines.Add(tlRes);

                    if (!isBull)
                    {
                        item.HasTrendLine = true;
                        item.TrendLineStartX = startIdx;
                        item.TrendLineStartY = startY;
                        item.TrendLineEndX = bestAnchor;
                        item.TrendLineEndY = klines[bestAnchor].High;
                        item.TrendLineSlope = bestSlope;
                    }
                }
            }

            // =========================================================================
            // 3. 本次连涨/连跌：实体中枢动能向量轴 (Streak Center Momentum Vector)
            // =========================================================================
            {
                decimal startMid = (klines[startIdx].Open + klines[startIdx].Close) / 2m;
                decimal confMid = (klines[confIdx].Open + klines[confIdx].Close) / 2m;
                decimal midSlope = (confMid - startMid) / (confIdx - startIdx);
                int rEnd = Math.Max(confIdx, rayEndTarget);
                decimal rPrice = startMid + midSlope * (rEnd - startIdx);

                var tlMid = new ConsecutiveRecentTrendLine
                {
                    LineType = ConsecutiveRecentTrendLineType.StreakCenter,
                    Name = isBull ? "连涨实体中枢动能轴" : "连跌实体中枢动能轴",
                    StartX = startIdx,
                    StartY = startMid,
                    EndX = confIdx,
                    EndY = confMid,
                    Slope = midSlope,
                    RayEndX = rEnd,
                    RayEndY = rPrice,
                    ColorHex = isBull ? "#22c55e" : "#ef4444", // 翡翠绿 / 亮红
                    LineWidth = 0.9f,
                    TagText = $"⚡ 动能中心: {rPrice:F2}",
                    IsPrimary = false
                };
                item.RecentTrendLines.Add(tlMid);
            }

            // =========================================================================
            // 4. 前序形态分析回溯 (向左回溯 6 ~ 35 根 K 线寻找前序结构)
            // =========================================================================
            int lookback = 35;
            int lbStart = Math.Max(0, startIdx - lookback);
            int lbEnd = startIdx - 1;

            if (lbEnd - lbStart >= 3)
            {
                // --- 4.1 前序波峰阻力突破线 (Preceding Peak Resistance) ---
                var peaks = new List<int>();
                for (int k = lbStart + 1; k < lbEnd; k++)
                {
                    if (klines[k].High >= klines[k - 1].High && klines[k].High >= klines[k + 1].High)
                    {
                        peaks.Add(k);
                    }
                }

                int p1 = -1, p2 = -1;
                if (peaks.Count >= 2)
                {
                    p1 = peaks[0];
                    p2 = peaks[^1];
                }
                else
                {
                    int maxHIdx = lbStart;
                    for (int k = lbStart + 1; k <= lbEnd; k++)
                    {
                        if (klines[k].High > klines[maxHIdx].High) maxHIdx = k;
                    }
                    if (lbEnd > maxHIdx)
                    {
                        p1 = maxHIdx;
                        p2 = lbEnd;
                    }
                }

                if (p1 >= 0 && p2 > p1)
                {
                    decimal slopeP = (klines[p2].High - klines[p1].High) / (p2 - p1);
                    int rEnd = Math.Max(startIdx + 2, rayEndTarget);
                    decimal rPrice = klines[p1].High + slopeP * (rEnd - p1);
                    decimal breakPrice = klines[p1].High + slopeP * (startIdx - p1);

                    var tlPrecRes = new ConsecutiveRecentTrendLine
                    {
                        LineType = ConsecutiveRecentTrendLineType.PrecedingResistance,
                        Name = "前序波峰突破阻力线",
                        StartX = p1,
                        StartY = klines[p1].High,
                        EndX = p2,
                        EndY = klines[p2].High,
                        Slope = slopeP,
                        RayEndX = rEnd,
                        RayEndY = rPrice,
                        ColorHex = "#fbbf24", // 金黄色 (Amber 400)
                        LineWidth = 1.0f,
                        TagText = isBull ? $"⚡ 突破前序阻力@{breakPrice:F2}" : $"⚡ 跌破前阻转压制@{breakPrice:F2}",
                        IsPrimary = isBull
                    };
                    item.RecentTrendLines.Add(tlPrecRes);

                    if (isBull)
                    {
                        item.HasPrecedingTrendLine = true;
                        item.PrecedingTrendStartX = p1;
                        item.PrecedingTrendStartY = klines[p1].High;
                        item.PrecedingTrendEndX = p2;
                        item.PrecedingTrendEndY = klines[p2].High;
                        item.PrecedingTrendSlope = slopeP;
                    }
                }

                // --- 4.2 前序波谷结构托底线 (Preceding Trough Support) ---
                var troughs = new List<int>();
                for (int k = lbStart + 1; k < lbEnd; k++)
                {
                    if (klines[k].Low <= klines[k - 1].Low && klines[k].Low <= klines[k + 1].Low)
                    {
                        troughs.Add(k);
                    }
                }

                int t1 = -1, t2 = -1;
                if (troughs.Count >= 2)
                {
                    t1 = troughs[0];
                    t2 = troughs[^1];
                }
                else
                {
                    int minLIdx = lbStart;
                    for (int k = lbStart + 1; k <= lbEnd; k++)
                    {
                        if (klines[k].Low < klines[minLIdx].Low) minLIdx = k;
                    }
                    if (lbEnd > minLIdx)
                    {
                        t1 = minLIdx;
                        t2 = lbEnd;
                    }
                }

                if (t1 >= 0 && t2 > t1)
                {
                    decimal slopeT = (klines[t2].Low - klines[t1].Low) / (t2 - t1);
                    int rEnd = Math.Max(startIdx + 2, rayEndTarget);
                    decimal rPrice = klines[t1].Low + slopeT * (rEnd - t1);
                    decimal breakPrice = klines[t1].Low + slopeT * (startIdx - t1);

                    var tlPrecSup = new ConsecutiveRecentTrendLine
                    {
                        LineType = ConsecutiveRecentTrendLineType.PrecedingSupport,
                        Name = "前序波谷结构托底线",
                        StartX = t1,
                        StartY = klines[t1].Low,
                        EndX = t2,
                        EndY = klines[t2].Low,
                        Slope = slopeT,
                        RayEndX = rEnd,
                        RayEndY = rPrice,
                        ColorHex = "#f59e0b", // 琥珀黄 (Amber 500)
                        LineWidth = 0.9f,
                        TagText = !isBull ? $"⚡ 跌破前序支撑@{breakPrice:F2}" : $"🛡️ 前序结构底@{breakPrice:F2}",
                        IsPrimary = !isBull
                    };
                    item.RecentTrendLines.Add(tlPrecSup);

                    if (!isBull)
                    {
                        item.HasPrecedingTrendLine = true;
                        item.PrecedingTrendStartX = t1;
                        item.PrecedingTrendStartY = klines[t1].Low;
                        item.PrecedingTrendEndX = t2;
                        item.PrecedingTrendEndY = klines[t2].Low;
                        item.PrecedingTrendSlope = slopeT;
                    }
                }

                // --- 4.3 前序微型收敛楔形切线 (Preceding Micro Convergence) ---
                int microLbStart = Math.Max(0, startIdx - 12);
                if (startIdx - 1 > microLbStart + 2)
                {
                    int m1 = microLbStart;
                    int m2 = startIdx - 1;
                    if (isBull)
                    {
                        // 取微型区间最高点连至启动前一根高点
                        for (int k = microLbStart; k < startIdx - 1; k++)
                        {
                            if (klines[k].High > klines[m1].High) m1 = k;
                        }
                        if (m2 > m1)
                        {
                            decimal slopeM = (klines[m2].High - klines[m1].High) / (m2 - m1);
                            int rEnd = Math.Min(totalBars - 1, startIdx + 4);
                            var tlMicro = new ConsecutiveRecentTrendLine
                            {
                                LineType = ConsecutiveRecentTrendLineType.PrecedingConvergence,
                                Name = "启动前微型收敛切线",
                                StartX = m1,
                                StartY = klines[m1].High,
                                EndX = m2,
                                EndY = klines[m2].High,
                                Slope = slopeM,
                                RayEndX = rEnd,
                                RayEndY = klines[m1].High + slopeM * (rEnd - m1),
                                ColorHex = "#a855f7", // 紫色 (Purple 500)
                                LineWidth = 0.8f,
                                TagText = "📐 楔形收敛突破",
                                IsPrimary = false
                            };
                            item.RecentTrendLines.Add(tlMicro);
                        }
                    }
                    else
                    {
                        // 取微型区间最低点连至启动前一根低点
                        for (int k = microLbStart; k < startIdx - 1; k++)
                        {
                            if (klines[k].Low < klines[m1].Low) m1 = k;
                        }
                        if (m2 > m1)
                        {
                            decimal slopeM = (klines[m2].Low - klines[m1].Low) / (m2 - m1);
                            int rEnd = Math.Min(totalBars - 1, startIdx + 4);
                            var tlMicro = new ConsecutiveRecentTrendLine
                            {
                                LineType = ConsecutiveRecentTrendLineType.PrecedingConvergence,
                                Name = "启动前微型收敛切线",
                                StartX = m1,
                                StartY = klines[m1].Low,
                                EndX = m2,
                                EndY = klines[m2].Low,
                                Slope = slopeM,
                                RayEndX = rEnd,
                                RayEndY = klines[m1].Low + slopeM * (rEnd - m1),
                                ColorHex = "#a855f7", // 紫色
                                LineWidth = 0.8f,
                                TagText = "📐 楔形跌破",
                                IsPrimary = false
                            };
                            item.RecentTrendLines.Add(tlMicro);
                        }
                    }
                }
            }

            // =========================================================================
            // 5. 近端大级别全局极值跨波段映射趋势线 (Major Swing Projection)
            // =========================================================================
            int extLbStart = Math.Max(0, startIdx - 50);
            if (startIdx - 1 > extLbStart + 5)
            {
                if (isBull)
                {
                    // 寻找 50 根内的全局大底 (最低点) 映射至本次启动点 Low
                    int gLowIdx = extLbStart;
                    for (int k = extLbStart + 1; k < startIdx; k++)
                    {
                        if (klines[k].Low < klines[gLowIdx].Low) gLowIdx = k;
                    }
                    if (startIdx > gLowIdx + 4)
                    {
                        decimal slopeG = (klines[startIdx].Low - klines[gLowIdx].Low) / (startIdx - gLowIdx);
                        if (slopeG > 0)
                        {
                            int rEnd = Math.Max(confIdx, rayEndTarget);
                            decimal rPrice = klines[gLowIdx].Low + slopeG * (rEnd - gLowIdx);

                            var tlGlobal = new ConsecutiveRecentTrendLine
                            {
                                LineType = ConsecutiveRecentTrendLineType.MajorSwingProjection,
                                Name = "跨波段大级别支撑基准线",
                                StartX = gLowIdx,
                                StartY = klines[gLowIdx].Low,
                                EndX = startIdx,
                                EndY = klines[startIdx].Low,
                                Slope = slopeG,
                                RayEndX = rEnd,
                                RayEndY = rPrice,
                                ColorHex = "#818cf8", // 靛蓝 (Indigo 400)
                                LineWidth = 0.9f,
                                TagText = $"🚀 大级别起涨支撑: {rPrice:F2}",
                                IsPrimary = false
                            };
                            item.RecentTrendLines.Add(tlGlobal);
                        }
                    }
                }
                else
                {
                    // 寻找 50 根内的全局大顶 (最高点) 映射至本次启动点 High
                    int gHighIdx = extLbStart;
                    for (int k = extLbStart + 1; k < startIdx; k++)
                    {
                        if (klines[k].High > klines[gHighIdx].High) gHighIdx = k;
                    }
                    if (startIdx > gHighIdx + 4)
                    {
                        decimal slopeG = (klines[startIdx].High - klines[gHighIdx].High) / (startIdx - gHighIdx);
                        if (slopeG < 0)
                        {
                            int rEnd = Math.Max(confIdx, rayEndTarget);
                            decimal rPrice = klines[gHighIdx].High + slopeG * (rEnd - gHighIdx);

                            var tlGlobal = new ConsecutiveRecentTrendLine
                            {
                                LineType = ConsecutiveRecentTrendLineType.MajorSwingProjection,
                                Name = "跨波段大级别阻力基准线",
                                StartX = gHighIdx,
                                StartY = klines[gHighIdx].High,
                                EndX = startIdx,
                                EndY = klines[startIdx].High,
                                Slope = slopeG,
                                RayEndX = rEnd,
                                RayEndY = rPrice,
                                ColorHex = "#f472b6", // 粉红 (Pink 400)
                                LineWidth = 0.9f,
                                TagText = $"📉 大级别见顶压制: {rPrice:F2}",
                                IsPrimary = false
                            };
                            item.RecentTrendLines.Add(tlGlobal);
                        }
                    }
                }
            }

            // 🌸 3 点共线诊断：检查是否存在第 3 个价格点位落于趋势线容差范围内，若满足则自动升级为粉红色 (#f472b6)
            for (int i = 0; i < item.RecentTrendLines.Count; i++)
            {
                CheckAndApplyThirdPointTouch(klines, item.RecentTrendLines[i], touchTolerancePct);
            }

            // 限制最多数量
            if (item.RecentTrendLines.Count > maxLines)
            {
                item.RecentTrendLines.RemoveRange(maxLines, item.RecentTrendLines.Count - maxLines);
            }
        }

        /// <summary>
        /// 针对达到门槛确立的连续波段，提取最近 1000 根 K 线的可能高低点位，与最新 K 线进行交互绘制
        /// 1. 连续上涨：在最近 1000 根 K 线内提取可能的高点位 (Swing Highs 及千根阻力线)，与最新 K 线的高点交互绘制阻力/压制趋势线及投射位
        /// 2. 连续下跌：在最近 1000 根 K 线内提取可能的低点位 (Swing Lows 及千根支撑线)，与最新 K 线的低点交互绘制支撑/底线趋势线及投射位
        /// 零未来函数：所有数据严格取自 [Math.Max(0, latestIdx - maxLookback), latestIdx] 范围，绝不依赖任何未来 K 线
        /// </summary>
        public static void Calculate1000BarInteractiveTrendLines(
            IReadOnlyList<PercentageKline> klines,
            ConsecutiveTrendItem item,
            int maxLookback = 1000,
            int maxLines = 6,
            decimal touchTolerancePct = 0.0005m)
        {
            if (klines == null || item == null || klines.Count == 0) return;

            item.Macro1000BarLines.Clear();

            // 最新 K 线索引 (当前波段已走到的最新柱，严格以已完成的 EndIndex 为准)
            int latestIdx = item.EndIndex;
            if (latestIdx < 0 || latestIdx >= klines.Count) return;

            int lookbackStart = Math.Max(0, latestIdx - maxLookback);
            int historyEnd = latestIdx - 1; // 仅在历史区间内寻找高低点锚点，防止与最新柱重合
            if (historyEnd <= lookbackStart) return;

            bool isBull = item.Type == ConsecutiveTrendType.Bullish;
            decimal latestPrice = isBull ? klines[latestIdx].High : klines[latestIdx].Low;
            int rayExtension = Math.Min(klines.Count - 1, latestIdx + 10);

            if (isBull)
            {
                // =========================================================================
                // 连续上涨：提取前 1000 根的高点位与最新 K 线的 High 交互绘制
                // =========================================================================

                // 1. 全局最高点 (Macro Absolute Peak)
                int maxPeakIdx = lookbackStart;
                for (int k = lookbackStart + 1; k <= historyEnd; k++)
                {
                    if (klines[k].High > klines[maxPeakIdx].High)
                    {
                        maxPeakIdx = k;
                    }
                }

                // 2. 局部波峰点 (Swing Highs, 局部窗口半径 3)
                var swingHighs = new List<int>();
                int w = 3;
                for (int k = lookbackStart + w; k <= historyEnd - w; k++)
                {
                    decimal h = klines[k].High;
                    bool isLocalPeak = true;
                    for (int step = 1; step <= w; step++)
                    {
                        if (klines[k - step].High > h || klines[k + step].High > h)
                        {
                            isLocalPeak = false;
                            break;
                        }
                    }
                    if (isLocalPeak)
                    {
                        swingHighs.Add(k);
                    }
                }

                // 若窗口半径 3 找到的峰值过少，降低半径至 2 进行补充
                if (swingHighs.Count < 2)
                {
                    for (int k = lookbackStart + 2; k <= historyEnd - 2; k++)
                    {
                        if (!swingHighs.Contains(k) &&
                            klines[k].High >= klines[k - 1].High && klines[k].High >= klines[k + 1].High &&
                            klines[k].High >= klines[k - 2].High && klines[k].High >= klines[k + 2].High)
                        {
                            swingHighs.Add(k);
                        }
                    }
                }

                // 选取具有代表性的候选高点位：包含绝对最高峰与多跨度波峰
                var selectedHighAnchors = new List<int>();
                if (!selectedHighAnchors.Contains(maxPeakIdx))
                {
                    selectedHighAnchors.Add(maxPeakIdx);
                }

                var candidateHighs = swingHighs
                    .OrderByDescending(idx => klines[idx].High)
                    .ToList();

                foreach (var sh in candidateHighs)
                {
                    if (selectedHighAnchors.Count >= 4) break;
                    if (selectedHighAnchors.All(a => Math.Abs(a - sh) >= 15) && (latestIdx - sh) >= 5)
                    {
                        selectedHighAnchors.Add(sh);
                    }
                }

                // 按时间先后排序
                selectedHighAnchors.Sort();

                // 3. 逐个与最新 K 线 High 交互生成直接趋势连线
                foreach (var highIdx in selectedHighAnchors)
                {
                    decimal anchorH = klines[highIdx].High;
                    decimal slope = (latestPrice - anchorH) / (latestIdx - highIdx);
                    int barsAgo = latestIdx - highIdx;

                    bool isAbsMax = highIdx == maxPeakIdx;
                    string tag = isAbsMax
                        ? $"🎯 1000B峰值压制(-{barsAgo}B): {anchorH:F2}"
                        : $"🎯 1000B高点交互(-{barsAgo}B): {anchorH:F2}";

                    var tl = new ConsecutiveRecentTrendLine
                    {
                        LineType = ConsecutiveRecentTrendLineType.Interactive1000BarHigh,
                        Name = isAbsMax ? "1000根全局最高压制交互线" : $"1000根高点位交互线(-{barsAgo}B)",
                        StartX = highIdx,
                        StartY = anchorH,
                        EndX = latestIdx,
                        EndY = latestPrice,
                        Slope = slope,
                        RayEndX = rayExtension,
                        RayEndY = latestPrice + slope * (rayExtension - latestIdx),
                        ColorHex = isAbsMax ? "#f59e0b" : "#fbbf24", // 琥珀橙 / 明黄
                        LineWidth = isAbsMax ? 1.4f : 1.0f,
                        TagText = tag,
                        IsPrimary = isAbsMax
                    };
                    item.Macro1000BarLines.Add(tl);
                }

                // 4. 生成前序 1000 根两个最强波峰构成的宏观阻力线推演至最新 K 线的投射交互线
                if (selectedHighAnchors.Count >= 2)
                {
                    int h1 = selectedHighAnchors[0];
                    int h2 = selectedHighAnchors[^1];
                    if (h2 == maxPeakIdx && selectedHighAnchors.Count >= 3)
                    {
                        h1 = selectedHighAnchors[0];
                        h2 = selectedHighAnchors[1];
                    }

                    if (h2 > h1)
                    {
                        decimal slopeM = (klines[h2].High - klines[h1].High) / (h2 - h1);
                        decimal projY = klines[h1].High + slopeM * (latestIdx - h1);

                        var tlProj = new ConsecutiveRecentTrendLine
                        {
                            LineType = ConsecutiveRecentTrendLineType.Projected1000BarTarget,
                            Name = "1000根宏观波峰阻力推演投射线",
                            StartX = h1,
                            StartY = klines[h1].High,
                            EndX = latestIdx,
                            EndY = projY,
                            Slope = slopeM,
                            RayEndX = rayExtension,
                            RayEndY = klines[h1].High + slopeM * (rayExtension - h1),
                            ColorHex = "#f97316", // 橘红 (Orange 500)
                            LineWidth = 1.2f,
                            TagText = $"🎯 1000B阻力投射: {projY:F2}",
                            IsPrimary = true
                        };
                        item.Macro1000BarLines.Add(tlProj);
                    }
                }
            }
            else
            {
                // =========================================================================
                // 连续下跌：提取前 1000 根的低点位与最新 K 线的 Low 交互绘制 (反之同理)
                // =========================================================================

                // 1. 全局最低点 (Macro Absolute Trough)
                int minTroughIdx = lookbackStart;
                for (int k = lookbackStart + 1; k <= historyEnd; k++)
                {
                    if (klines[k].Low < klines[minTroughIdx].Low)
                    {
                        minTroughIdx = k;
                    }
                }

                // 2. 局部波谷点 (Swing Lows, 局部窗口半径 3)
                var swingLows = new List<int>();
                int w = 3;
                for (int k = lookbackStart + w; k <= historyEnd - w; k++)
                {
                    decimal l = klines[k].Low;
                    bool isLocalTrough = true;
                    for (int step = 1; step <= w; step++)
                    {
                        if (klines[k - step].Low < l || klines[k + step].Low < l)
                        {
                            isLocalTrough = false;
                            break;
                        }
                    }
                    if (isLocalTrough)
                    {
                        swingLows.Add(k);
                    }
                }

                // 若窗口半径 3 找到的谷值过少，降低半径至 2 进行补充
                if (swingLows.Count < 2)
                {
                    for (int k = lookbackStart + 2; k <= historyEnd - 2; k++)
                    {
                        if (!swingLows.Contains(k) &&
                            klines[k].Low <= klines[k - 1].Low && klines[k].Low <= klines[k + 1].Low &&
                            klines[k].Low <= klines[k - 2].Low && klines[k].Low <= klines[k + 2].Low)
                        {
                            swingLows.Add(k);
                        }
                    }
                }

                // 选取具有代表性的候选低点位
                var selectedLowAnchors = new List<int>();
                if (!selectedLowAnchors.Contains(minTroughIdx))
                {
                    selectedLowAnchors.Add(minTroughIdx);
                }

                var candidateLows = swingLows
                    .OrderBy(idx => klines[idx].Low)
                    .ToList();

                foreach (var sl in candidateLows)
                {
                    if (selectedLowAnchors.Count >= 4) break;
                    if (selectedLowAnchors.All(a => Math.Abs(a - sl) >= 15) && (latestIdx - sl) >= 5)
                    {
                        selectedLowAnchors.Add(sl);
                    }
                }

                selectedLowAnchors.Sort();

                // 3. 逐个与最新 K 线 Low 交互生成直接趋势连线
                foreach (var lowIdx in selectedLowAnchors)
                {
                    decimal anchorL = klines[lowIdx].Low;
                    decimal slope = (latestPrice - anchorL) / (latestIdx - lowIdx);
                    int barsAgo = latestIdx - lowIdx;

                    bool isAbsMin = lowIdx == minTroughIdx;
                    string tag = isAbsMin
                        ? $"🎯 1000B谷值支撑(-{barsAgo}B): {anchorL:F2}"
                        : $"🎯 1000B低点交互(-{barsAgo}B): {anchorL:F2}";

                    var tl = new ConsecutiveRecentTrendLine
                    {
                        LineType = ConsecutiveRecentTrendLineType.Interactive1000BarLow,
                        Name = isAbsMin ? "1000根全局最低支撑交互线" : $"1000根低点位交互线(-{barsAgo}B)",
                        StartX = lowIdx,
                        StartY = anchorL,
                        EndX = latestIdx,
                        EndY = latestPrice,
                        Slope = slope,
                        RayEndX = rayExtension,
                        RayEndY = latestPrice + slope * (rayExtension - latestIdx),
                        ColorHex = isAbsMin ? "#10b981" : "#14b8a6", // 翡翠绿 / 湖绿
                        LineWidth = isAbsMin ? 1.4f : 1.0f,
                        TagText = tag,
                        IsPrimary = isAbsMin
                    };
                    item.Macro1000BarLines.Add(tl);
                }

                // 4. 生成前序 1000 根两个最强波谷构成的宏观支撑线推演至最新 K 线的投射交互线
                if (selectedLowAnchors.Count >= 2)
                {
                    int t1 = selectedLowAnchors[0];
                    int t2 = selectedLowAnchors[^1];
                    if (t2 == minTroughIdx && selectedLowAnchors.Count >= 3)
                    {
                        t1 = selectedLowAnchors[0];
                        t2 = selectedLowAnchors[1];
                    }

                    if (t2 > t1)
                    {
                        decimal slopeM = (klines[t2].Low - klines[t1].Low) / (t2 - t1);
                        decimal projY = klines[t1].Low + slopeM * (latestIdx - t1);

                        var tlProj = new ConsecutiveRecentTrendLine
                        {
                            LineType = ConsecutiveRecentTrendLineType.Projected1000BarTarget,
                            Name = "1000根宏观波谷支撑推演投射线",
                            StartX = t1,
                            StartY = klines[t1].Low,
                            EndX = latestIdx,
                            EndY = projY,
                            Slope = slopeM,
                            RayEndX = rayExtension,
                            RayEndY = klines[t1].Low + slopeM * (rayExtension - t1),
                            ColorHex = "#06b6d4", // 青蓝 (Cyan 500)
                            LineWidth = 1.2f,
                            TagText = $"🎯 1000B支撑投射: {projY:F2}",
                            IsPrimary = true
                        };
                        item.Macro1000BarLines.Add(tlProj);
                    }
                }
            }

            // 🌸 3 点共线诊断：检查是否存在第 3 个价格点位落于千根交互趋势线容差范围内，若满足则自动升级为粉红色 (#f472b6)
            for (int i = 0; i < item.Macro1000BarLines.Count; i++)
            {
                CheckAndApplyThirdPointTouch(klines, item.Macro1000BarLines[i], touchTolerancePct);
            }

            // 限制最多数量
            if (item.Macro1000BarLines.Count > maxLines)
            {
                item.Macro1000BarLines.RemoveRange(maxLines, item.Macro1000BarLines.Count - maxLines);
            }
        }

        /// <summary>
        /// 检查趋势线在实体起止区间内是否存在第 3 个价格极值点落于趋势线容差范围内 (≤ touchTolerancePct)
        /// 若满足条件，则该线达成 3 点共线确认，颜色自动升级为粉红色 (#f472b6)，端点标牌追加 🌸3点
        /// </summary>
        public static void CheckAndApplyThirdPointTouch(
            IReadOnlyList<PercentageKline> klines,
            ConsecutiveRecentTrendLine line,
            decimal touchTolerancePct = 0.0005m)
        {
            if (klines == null || line == null) return;
            int x1 = Math.Min(line.StartX, line.EndX);
            int x2 = Math.Max(line.StartX, line.EndX);
            if (x2 - x1 < 2) return; // 至少需要 3 根 K 线跨度才存在中间第 3 点

            bool isHighLine = line.LineType == ConsecutiveRecentTrendLineType.StreakResistance ||
                              line.LineType == ConsecutiveRecentTrendLineType.PrecedingResistance ||
                              line.LineType == ConsecutiveRecentTrendLineType.Interactive1000BarHigh ||
                              line.LineType == ConsecutiveRecentTrendLineType.MajorSwingProjection ||
                              line.LineType == ConsecutiveRecentTrendLineType.Projected1000BarTarget;

            int bestBar = -1;
            decimal bestPrice = 0m;
            decimal minDevPct = decimal.MaxValue;

            for (int k = x1 + 1; k < x2; k++)
            {
                if (k < 0 || k >= klines.Count) continue;

                decimal lineY = line.StartY + line.Slope * (k - line.StartX);
                if (lineY <= 0m) continue;

                decimal checkPrice = isHighLine ? klines[k].High : klines[k].Low;
                decimal devPct = Math.Abs(checkPrice - lineY) / lineY;

                if (devPct <= touchTolerancePct && devPct < minDevPct)
                {
                    minDevPct = devPct;
                    bestBar = k;
                    bestPrice = checkPrice;
                }
            }

            if (bestBar >= 0)
            {
                line.HasThirdPointTouch = true;
                line.ThirdPointBarIndex = bestBar;
                line.ThirdPointPrice = bestPrice;
                line.ThirdPointDistancePct = minDevPct * 100m;
                line.ColorHex = "#f472b6"; // 🌸 改为粉红色显示 (Pink 400)
                line.LineWidth = Math.Max(line.LineWidth, 1.3f);
                line.IsPrimary = true;
                if (!string.IsNullOrEmpty(line.TagText) && !line.TagText.Contains("🌸"))
                {
                    line.TagText += $" 🌸3点({minDevPct * 100m:F2}%)";
                }
            }
        }

        /// <summary>
        /// 判定第 index 根 K 线是否属于上涨 K 线 (相较前一根收盘价上涨，或自身为收红阳线且不低于前收)
        /// </summary>
        private static bool IsRisingBar(IReadOnlyList<PercentageKline> klines, int index)
        {
            if (index < 0 || index >= klines.Count) return false;
            var curr = klines[index];
            if (index == 0) return curr.Close > curr.Open;
            var prev = klines[index - 1];

            return curr.Close > prev.Close || (curr.Close > curr.Open && curr.Close >= prev.Close);
        }

        /// <summary>
        /// 判定第 index 根 K 线是否属于下跌 K 线 (相较前一根收盘价下跌，或自身为阴线且不高于前收)
        /// </summary>
        private static bool IsFallingBar(IReadOnlyList<PercentageKline> klines, int index)
        {
            if (index < 0 || index >= klines.Count) return false;
            var curr = klines[index];
            if (index == 0) return curr.Close < curr.Open;
            var prev = klines[index - 1];

            return curr.Close < prev.Close || (curr.Close < curr.Open && curr.Close <= prev.Close);
        }
    }
}
