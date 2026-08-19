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
            bool allowInternalPenetration = false)
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

                // 检查两点内部是否有 K 线穿透 (严格逻辑剪枝)
                if (!allowInternalPenetration && IsPenetratedInternally(pOld, newPoint, klines, currentGlobalIndex))
                {
                    continue;
                }

                // 构造新趋势线并计算延伸至当前最新 K 线的碰撞状态
                var line = CreateTrendLine(pOld, newPoint, klines, currentGlobalIndex);
                targetActiveLines.Add(line);
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
            bool allowInternalPenetration = false)
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
                allowInternalPenetration);
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

                    // 若该线此前尚未被碰撞击穿，检测当前这根 K 线是否造成首次碰撞
                    if (line.CollidedKlineIndex == -1)
                    {
                        decimal expectedPrice = line.GetPriceAt(currentGlobalIndex);

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
                LineExtensionRange = lineAge
            };

            // 从 p2 向后延伸检查首次碰撞
            if (klines != null && klines.Count > 0 && latestIndex > p2.Index)
            {
                for (int x = p2.Index + 1; x <= latestIndex; x++)
                {
                    int localIdx = MapGlobalToLocalIndex(x, latestIndex, klines.Count);
                    if (localIdx < 0 || localIdx >= klines.Count) continue;

                    decimal linePrice = line.GetPriceAt(x);
                    if (line.IsResistance && klines[localIdx].High > linePrice)
                    {
                        line.CollidedKlineIndex = x;
                        line.LineExtensionRange = x - p2.Index;
                        break;
                    }
                    else if (line.IsSupport && klines[localIdx].Low < linePrice)
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
                if (p1.IsPeak && klines[localIdx].High > linePrice)
                {
                    return true;
                }
                else if (p1.IsValley && klines[localIdx].Low < linePrice)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion
    }
}
