using Common.Models;
using System;
using System.Collections.Generic;

namespace Common.Helper
{
    /// <summary>
    /// 高性能趋势线生成、增量延伸碰撞检测与突破判定辅助工具类
    /// 支持全量拟合与 O(1)/O(M) 毫秒级多层增量事件驱动计算
    /// </summary>
    public static class TrendLineHelper
    {
        #region 1. 核心增量方法 (Incremental TrendLine Methods)

        /// <summary>
        /// 增量趋势线配对生成：当且仅当产生新极值点时，仅将该新极值点与历史活跃极值点在 MaxSpan 内配对
        /// </summary>
        /// <param name="newPoint">新确认的极值点</param>
        /// <param name="existingPoints">历史同类型极值点列表</param>
        /// <summary>
        /// 增量趋势线配对生成：零内存分配直装模式 (直接写入目标活跃列表与历史库)
        /// </summary>
        public static void GenerateIncrementalTrendLines(
            PivotPoint newPoint,
            IReadOnlyList<PivotPoint> existingPoints,
            IReadOnlyList<RawKline> klines,
            int currentGlobalIndex,
            List<TrendLine> targetActiveLines,
            List<TrendLine> targetHistoryLines,
            int maxSpan = 100,
            bool allowInternalPenetration = false,
            decimal maxSlopePctPerBar = 2.0m)
        {
            if (existingPoints == null || existingPoints.Count == 0 || klines == null || klines.Count == 0)
            {
                return;
            }

            for (int i = existingPoints.Count - 1; i >= 0; i--)
            {
                var pOld = existingPoints[i];
                int span = newPoint.Index - pOld.Index;

                if (span <= 0) continue;
                if (span > maxSpan) break; // 极值点已按 Index 严格递增排序，超出直接 break

                // 1. 【超高斜率过滤】
                decimal dy = newPoint.Price - pOld.Price;
                decimal normalizedK = pOld.Price > 0m ? Math.Abs((dy / pOld.Price) / span * 100m) : 0m;
                if (maxSlopePctPerBar > 0m && normalizedK > maxSlopePctPerBar)
                {
                    continue; // 过滤极端过陡的异常噪音斜率趋势线
                }

                // 3. 检查两点内部是否有 K 线穿透 (严格逻辑剪枝)
                if (!allowInternalPenetration && IsPenetratedInternally(pOld, newPoint, klines, currentGlobalIndex))
                {
                    continue;
                }

                // 构造新趋势线并计算延伸至当前最新 K 线的碰撞状态
                var line = CreateTrendLine(pOld, newPoint, klines, currentGlobalIndex);
                if (line.CollidedKlineIndex == -1)
                {
                    targetActiveLines.Add(line);
                }
                targetHistoryLines?.Add(line);
            }
        }

        /// <summary>
        /// 增量趋势线配对生成：当且仅当产生新极值点时，仅将该新极值点与历史活跃极值点在 MaxSpan 内配对
        /// </summary>
        public static List<TrendLine> GenerateIncrementalTrendLines(
            PivotPoint newPoint,
            IReadOnlyList<PivotPoint> existingPoints,
            IReadOnlyList<RawKline> klines,
            int currentGlobalIndex,
            int maxSpan = 100,
            bool allowInternalPenetration = false,
            decimal maxSlopePctPerBar = 2.0m)
        {
            var newLines = new List<TrendLine>();
            GenerateIncrementalTrendLines(
                newPoint,
                existingPoints,
                klines,
                currentGlobalIndex,
                newLines,
                null,
                maxSpan,
                allowInternalPenetration,
                maxSlopePctPerBar);
            return newLines;
        }

        /// <summary>
        /// 增量单步推进存量活跃趋势线：仅代入当前这一根最新 K 线，完成寿命自增与碰撞更新 (耗时 < 1 微秒)
        /// </summary>
        /// <param name="activeLines">存量活跃趋势线列表</param>
        /// <param name="latestKline">当前收盘的最新一根 K 线</param>
        /// <param name="currentGlobalIndex">当前最新一根 K 线的全局单调下标</param>
        public static void UpdateActiveTrendLinesStep(
            List<TrendLine> activeLines,
            in RawKline latestKline,
            int currentGlobalIndex)
        {
            if (activeLines == null || activeLines.Count == 0) return;

            for (int i = 0; i < activeLines.Count; i++)
            {
                var line = activeLines[i];

                // 只有当当前 K 线在终止点 x2 之后才延伸
                if (currentGlobalIndex > line.X2)
                {
                    line.LineAge = currentGlobalIndex - line.X2;
                    decimal expectedPrice = line.GetPriceAt(currentGlobalIndex);
                    line.CachedCurrentPrice = expectedPrice;

                    // 若该线此前尚未被碰撞击穿，检测当前这根 K 线是否造成首次碰撞
                    if (line.CollidedKlineIndex == -1)
                    {
                        if (line.IsResistance && latestKline.High > expectedPrice)
                        {
                            line.CollidedKlineIndex = currentGlobalIndex;
                            line.LineExtensionRange = currentGlobalIndex - line.X2;
                        }
                        else if (line.IsSupport && latestKline.Low < expectedPrice)
                        {
                            line.CollidedKlineIndex = currentGlobalIndex;
                            line.LineExtensionRange = currentGlobalIndex - line.X2;
                        }
                        else
                        {
                            line.LineExtensionRange = currentGlobalIndex - line.X2;
                        }
                    }

                    activeLines[i] = line;
                }
            }
        }

        #endregion

        #region 2. 趋势线基础构造与全量方法

        /// <summary>
        /// 基于两个极值点构造推导完整的趋势线特征
        /// </summary>
        public static TrendLine CreateTrendLine(
            PivotPoint p1,
            PivotPoint p2,
            IReadOnlyList<RawKline> klines,
            int currentGlobalIndex = -1)
        {
            if (p2.Index <= p1.Index)
            {
                throw new ArgumentException("终止点 p2 索引必须大于起始点 p1 索引！");
            }

            int dx = p2.Index - p1.Index;
            decimal dy = p2.Price - p1.Price;
            decimal rawK = dy / dx;
            decimal normalizedK = p1.Price > 0m ? (dy / p1.Price) / dx * 100m : 0m;

            int latestIndex = currentGlobalIndex >= 0 ? currentGlobalIndex : (klines != null ? klines.Count - 1 : p2.Index);
            int lineAge = Math.Max(0, latestIndex - p2.Index);

            var line = new TrendLine
            {
                X1 = p1.Index,
                Y1 = p1.Price,
                Time1 = p1.Time,
                TimestampMs1 = p1.TimestampMs,
                X2 = p2.Index,
                Y2 = p2.Price,
                Time2 = p2.Time,
                TimestampMs2 = p2.TimestampMs,
                RawK = rawK,
                K = normalizedK,
                Type = p1.Type,
                LineAge = lineAge,
                CollidedKlineIndex = -1,
                LineExtensionRange = lineAge,
                IsThreePointConfirmed = false,
                TouchCount = 2,
                X3 = -1,
                Y3 = 0m,
                CachedCurrentPrice = p1.Price + rawK * (latestIndex - p1.Index)
            };

            // 从 p2 向后延伸检查首次碰撞
            if (klines != null && klines.Count > 0 && latestIndex > p2.Index)
            {
                for (int x = p2.Index + 1; x <= latestIndex; x++)
                {
                    int localIdx = MapGlobalToLocalIndex(x, latestIndex, klines.Count);
                    if (localIdx < 0 || localIdx >= klines.Count) continue;

                    decimal linePrice = line.GetPriceAt(x);
                    if (line.IsResistance && (klines[localIdx].Close > linePrice || klines[localIdx].High > linePrice))
                    {
                        line.CollidedKlineIndex = x;
                        line.LineExtensionRange = x - p2.Index;
                        break;
                    }
                    else if (line.IsSupport && (klines[localIdx].Close < linePrice || klines[localIdx].Low < linePrice))
                    {
                        line.CollidedKlineIndex = x;
                        line.LineExtensionRange = x - p2.Index;
                        break;
                    }
                }
            }

            return line;
        }

        /// <summary>
        /// 全量生成所有阻力与支撑趋势线 (用于历史对齐)
        /// </summary>
        public static (List<TrendLine> ResistanceLines, List<TrendLine> SupportLines) GenerateTrendLines(
            IReadOnlyList<RawKline> klines,
            IReadOnlyList<PivotPoint> peaks,
            IReadOnlyList<PivotPoint> valleys,
            int maxSpan = 100,
            bool allowInternalPenetration = false,
            int currentGlobalIndex = -1)
        {
            var resistanceLines = new List<TrendLine>();
            var supportLines = new List<TrendLine>();

            if (klines == null || klines.Count < 2)
            {
                return (resistanceLines, supportLines);
            }

            int latestIdx = currentGlobalIndex >= 0 ? currentGlobalIndex : klines.Count - 1;

            if (peaks != null && peaks.Count >= 2)
            {
                for (int i = 0; i < peaks.Count - 1; i++)
                {
                    for (int j = i + 1; j < peaks.Count; j++)
                    {
                        var p1 = peaks[i];
                        var p2 = peaks[j];
                        int span = p2.Index - p1.Index;
                        if (span <= 0 || span > maxSpan) continue;

                        // 过滤超高斜率
                        decimal dy = p2.Price - p1.Price;
                        decimal normalizedK = p1.Price > 0m ? Math.Abs((dy / p1.Price) / span * 100m) : 0m;
                        if (normalizedK > 2.0m) continue;

                        if (!allowInternalPenetration && IsPenetratedInternally(p1, p2, klines, latestIdx))
                            continue;

                        resistanceLines.Add(CreateTrendLine(p1, p2, klines, latestIdx));
                    }
                }
            }

            if (valleys != null && valleys.Count >= 2)
            {
                for (int i = 0; i < valleys.Count - 1; i++)
                {
                    for (int j = i + 1; j < valleys.Count; j++)
                    {
                        var p1 = valleys[i];
                        var p2 = valleys[j];
                        int span = p2.Index - p1.Index;
                        if (span <= 0 || span > maxSpan) continue;

                        // 过滤超高斜率
                        decimal dy = p2.Price - p1.Price;
                        decimal normalizedK = p1.Price > 0m ? Math.Abs((dy / p1.Price) / span * 100m) : 0m;
                        if (normalizedK > 2.0m) continue;

                        if (!allowInternalPenetration && IsPenetratedInternally(p1, p2, klines, latestIdx))
                            continue;

                        supportLines.Add(CreateTrendLine(p1, p2, klines, latestIdx));
                    }
                }
            }

            return (resistanceLines, supportLines);
        }

        #endregion

        #region 3. 内部索引映射与穿透校验辅助

        private static int MapGlobalToLocalIndex(int globalIndex, int currentGlobalIndex, int klineWindowCount)
        {
            int offsetFromLatest = currentGlobalIndex - globalIndex;
            return klineWindowCount - 1 - offsetFromLatest;
        }

        private static bool IsPenetratedInternally(
            PivotPoint p1,
            PivotPoint p2,
            IReadOnlyList<RawKline> klines,
            int currentGlobalIndex)
        {
            int dx = p2.Index - p1.Index;
            decimal dy = p2.Price - p1.Price;
            decimal rawK = dy / dx;

            for (int x = p1.Index + 1; x < p2.Index; x++)
            {
                int localIdx = MapGlobalToLocalIndex(x, currentGlobalIndex, klines.Count);
                if (localIdx < 0 || localIdx >= klines.Count) continue;

                decimal linePrice = p1.Price + rawK * (x - p1.Index);
                if (p1.IsPeak && (klines[localIdx].High > linePrice || klines[localIdx].Close > linePrice))
                {
                    return true;
                }
                else if (p1.IsValley && (klines[localIdx].Low < linePrice || klines[localIdx].Close < linePrice))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region 4. 趋势通道 (Trend Channel) 检测与精细加工

        /// <summary>
        /// 从满足条件的阻力趋势线与支撑趋势线中精细加工与过滤出高品质趋势通道 (Trend Channels)
        /// 精细过滤规则：
        /// 1. 基准线跨度门槛：上轨与下轨自身的跨度均需 >= minLineSpan (默认 30 根)
        /// 2. 重叠有效跨度：两轨在时间轴上的重叠跨度需 >= minOverlapSpan (默认 30 根)
        /// 3. 严格平行度：斜率同向，归一化斜率绝对偏差 <= maxSlopeDiffPct (默认 0.08%/bar)，且相对偏差 <= maxRelativeSlopeDiff (默认 18%)
        /// 4. 等宽平行度校验：起始宽度与终止宽度的比例 >= minWidthRatio (默认 0.70，过滤喇叭形或收敛三角形伪通道)
        /// 5. 通道有效宽度：平均百分比宽度处于合理区间 [minChannelWidthPct, maxChannelWidthPct] (默认 0.20% ~ 15.0%)
        /// 6. 趋势整体倾斜：两轨整体斜率具备有效倾斜幅度 >= minOverallSlopePct (默认 0.20%)
        /// 7. 最优品质去重：按品质得分降序贪心优选，消除重叠冗余通道，确保每组通道清晰权威
        /// </summary>
        /// <param name="resistanceLines">阻力趋势线列表 (上轨候选)</param>
        /// <param name="supportLines">支撑趋势线列表 (下轨候选)</param>
        /// <param name="currentGlobalIndex">当前最新全局 K 线索引</param>
        /// <param name="minLineSpan">单根趋势线自身最小跨度 (默认 30 根)</param>
        /// <param name="minOverlapSpan">两轨在时间轴上最小重叠跨度 (默认 30 根)</param>
        /// <param name="maxStartXDiff">两轨起点 X1 差值绝对值最大允许值 (默认 30 根)</param>
        /// <param name="maxSlopeDiffPct">允许的最大绝对斜率差 (%/bar，默认 0.08%/bar)</param>
        /// <param name="maxRelativeSlopeDiff">允许的最大相对斜率偏差 (默认 0.18 即 18%)</param>
        /// <param name="minWidthRatio">通道两端宽度一致性比例 (默认 0.70)</param>
        /// <param name="minChannelWidthPct">最小通道宽度百分比 (默认 0.20%)</param>
        /// <param name="maxChannelWidthPct">最大通道宽度百分比 (默认 15.0%)</param>
        /// <param name="minOverallSlopePct">最小整体倾斜百分比 (默认 0.20%)</param>
        /// <returns>加工出的高品质有效趋势通道列表</returns>
        public static List<TrendChannel> DetectTrendChannels(
            List<TrendLine> resistanceLines,
            List<TrendLine> supportLines,
            int currentGlobalIndex = -1,
            int minLineSpan = 30,
            int minOverlapSpan = 30,
            int maxStartXDiff = 30,
            decimal maxSlopeDiffPct = 0.08m,
            decimal maxRelativeSlopeDiff = 0.18m,
            decimal minWidthRatio = 0.70m,
            decimal minChannelWidthPct = 0.20m,
            decimal maxChannelWidthPct = 15.0m,
            decimal minOverallSlopePct = 0.20m)
        {
            var channels = new List<TrendChannel>();
            if (resistanceLines == null || resistanceLines.Count == 0 || supportLines == null || supportLines.Count == 0)
            {
                return channels;
            }

            // 先重置所有线条的通道标记
            for (int i = 0; i < resistanceLines.Count; i++)
            {
                var r = resistanceLines[i];
                r.IsInChannel = false;
                r.ChannelId = 0;
                resistanceLines[i] = r;
            }
            for (int i = 0; i < supportLines.Count; i++)
            {
                var s = supportLines[i];
                s.IsInChannel = false;
                s.ChannelId = 0;
                supportLines[i] = s;
            }

            var candidatePairs = new List<(int RIndex, int SIndex, double Score, TrendChannel Channel)>();

            // 1. 严格多重精细过滤遍历
            for (int i = 0; i < resistanceLines.Count; i++)
            {
                var r = resistanceLines[i];
                if (!r.IsValid) continue;
                if (r.LineX1X2 < minLineSpan) continue; // 过滤微型噪音阻力线

                for (int j = 0; j < supportLines.Count; j++)
                {
                    var s = supportLines[j];
                    if (!s.IsValid) continue;
                    if (s.LineX1X2 < minLineSpan) continue; // 过滤微型噪音支撑线

                    // 0. 起点对齐约束：两轨起点 X1 差值绝对值 <= maxStartXDiff (初步暂定 30 根 K 线内)
                    if (Math.Abs(r.X1 - s.X1) > maxStartXDiff)
                    {
                        continue;
                    }

                    // 1. 斜率同向与严格平行度检查
                    // 若斜率异号 (一正一负) 且绝对值均明显大于 0，说明两线会大幅交叉发散/收敛
                    if ((r.K > 0.015m && s.K < -0.015m) || (r.K < -0.015m && s.K > 0.015m))
                    {
                        continue;
                    }

                    decimal slopeDiff = Math.Abs(r.K - s.K);
                    decimal maxAbsK = Math.Max(Math.Abs(r.K), Math.Abs(s.K));
                    decimal relSlopeDiff = maxAbsK > 0.015m ? slopeDiff / maxAbsK : slopeDiff;

                    if (slopeDiff > maxSlopeDiffPct || relSlopeDiff > maxRelativeSlopeDiff)
                    {
                        continue;
                    }

                    // 2. 整体斜率倾斜幅度门槛
                    if (Math.Abs(r.OverallSlopePct) < minOverallSlopePct || Math.Abs(s.OverallSlopePct) < minOverallSlopePct)
                    {
                        continue;
                    }

                    // 3. 时间/X 轴有效重叠跨度计算
                    int rEnd = r.CollidedKlineIndex >= 0 ? r.CollidedKlineIndex : (currentGlobalIndex >= 0 ? currentGlobalIndex : r.X2 + r.LineAge);
                    int sEnd = s.CollidedKlineIndex >= 0 ? s.CollidedKlineIndex : (currentGlobalIndex >= 0 ? currentGlobalIndex : s.X2 + s.LineAge);

                    int startX = Math.Max(r.X1, s.X1);
                    int endX = Math.Min(rEnd, sEnd);
                    int overlapSpan = endX - startX;

                    if (overlapSpan < minOverlapSpan)
                    {
                        continue;
                    }

                    // 4. 上下位置与非相交检查 (阻力线在上，支撑线在下)
                    decimal rPriceStart = r.GetPriceAt(startX);
                    decimal sPriceStart = s.GetPriceAt(startX);
                    decimal rPriceEnd = r.GetPriceAt(endX);
                    decimal sPriceEnd = s.GetPriceAt(endX);

                    if (rPriceStart <= sPriceStart || rPriceEnd <= sPriceEnd)
                    {
                        continue; // 两线相交或支撑线在阻力线上方
                    }

                    // 5. 通道宽度合理性检查
                    decimal widthPctStart = sPriceStart > 0m ? (rPriceStart - sPriceStart) / sPriceStart * 100m : 0m;
                    decimal widthPctEnd = sPriceEnd > 0m ? (rPriceEnd - sPriceEnd) / sPriceEnd * 100m : 0m;

                    if (widthPctStart < minChannelWidthPct || widthPctEnd < minChannelWidthPct)
                    {
                        continue; // 通道过窄
                    }

                    if (widthPctStart > maxChannelWidthPct || widthPctEnd > maxChannelWidthPct)
                    {
                        continue; // 通道过宽
                    }

                    // 6. 等宽平行度校验 (过滤喇叭口和收敛楔形)
                    decimal minW = Math.Min(widthPctStart, widthPctEnd);
                    decimal maxW = Math.Max(widthPctStart, widthPctEnd);
                    decimal widthRatio = maxW > 0m ? minW / maxW : 0m;

                    if (widthRatio < minWidthRatio)
                    {
                        continue;
                    }

                    // 7. 计算通道品质综合得分
                    double score = (double)overlapSpan * (1.0 - (double)relSlopeDiff) * (double)widthRatio;
                    if (r.IsThreePointConfirmed) score *= 1.3;
                    if (s.IsThreePointConfirmed) score *= 1.3;
                    if (r.CollidedKlineIndex == -1 && s.CollidedKlineIndex == -1) score *= 1.2; // 双轨活跃加分

                    var channel = new TrendChannel
                    {
                        UpperLine = r,
                        LowerLine = s
                    };

                    candidatePairs.Add((i, j, score, channel));
                }
            }

            if (candidatePairs.Count == 0)
            {
                return channels;
            }

            // 2. 按得分降序排序，贪心优选非重叠的权威通道
            candidatePairs.Sort((a, b) => b.Score.CompareTo(a.Score));

            var usedR = new HashSet<int>();
            var usedS = new HashSet<int>();
            int channelIdCounter = 1;

            foreach (var item in candidatePairs)
            {
                if (usedR.Contains(item.RIndex) || usedS.Contains(item.SIndex))
                {
                    continue; // 消除同一线条的冗余重叠通道，确保清晰
                }

                usedR.Add(item.RIndex);
                usedS.Add(item.SIndex);

                int chId = channelIdCounter++;
                var ch = item.Channel;
                ch.ChannelId = chId;

                var r = resistanceLines[item.RIndex];
                r.IsInChannel = true;
                r.ChannelId = chId;
                resistanceLines[item.RIndex] = r;
                ch.UpperLine = r;

                var s = supportLines[item.SIndex];
                s.IsInChannel = true;
                s.ChannelId = chId;
                supportLines[item.SIndex] = s;
                ch.LowerLine = s;

                channels.Add(ch);
            }

            return channels;
        }

        #endregion
    }
}
