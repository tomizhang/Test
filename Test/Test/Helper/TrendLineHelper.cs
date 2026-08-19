using Common.Models;
using System;
using System.Collections.Generic;

namespace Common.Helper
{
    /// <summary>
    /// 高性能趋势线生成、延伸碰撞检测与突破判定辅助工具类
    /// 深度集成 RawKline、PivotPoint 与 100 条滑动历史缓存
    /// </summary>
    public static class TrendLineHelper
    {
        /// <summary>
        /// 基于两个极值点 (PivotPoint) 与 K 线历史序列构造并推导完整的趋势线特征
        /// </summary>
        public static TrendLine CreateTrendLine(PivotPoint p1, PivotPoint p2, IReadOnlyList<RawKline> klines)
        {
            if (p2.Index <= p1.Index)
            {
                throw new ArgumentException("终止点 p2 索引必须大于起始点 p1 索引！");
            }

            int dx = p2.Index - p1.Index;
            decimal dy = p2.Price - p1.Price;
            decimal rawK = dy / dx;
            decimal normalizedK = p1.Price > 0 ? (dy / p1.Price) / dx * 100m : 0m;

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
                LineAge = klines != null && klines.Count > 0 ? Math.Max(0, (klines.Count - 1) - p2.Index) : 0,
                CollidedKlineIndex = -1,
                LineExtensionRange = 0
            };

            // 计算延伸碰撞
            if (klines != null && klines.Count > 0)
            {
                CalculateLineCollision(ref line, klines);
            }

            return line;
        }

        /// <summary>
        /// 计算趋势线向后延伸首次被 K 线穿越/碰撞的位置与延伸距离
        /// </summary>
        public static void CalculateLineCollision(ref TrendLine line, IReadOnlyList<RawKline> klines)
        {
            int latestIndex = klines.Count - 1;
            line.LineAge = Math.Max(0, latestIndex - line.X2);
            line.CollidedKlineIndex = -1;
            line.LineExtensionRange = Math.Max(0, latestIndex - line.X2);

            for (int x = line.X2 + 1; x <= latestIndex; x++)
            {
                decimal linePrice = line.GetPriceAt(x);

                if (line.IsResistance)
                {
                    // 阻力线向后延伸被向上穿透 (High 超过趋势线)
                    if (klines[x].High > linePrice)
                    {
                        line.CollidedKlineIndex = x;
                        line.LineExtensionRange = x - line.X2;
                        break;
                    }
                }
                else if (line.IsSupport)
                {
                    // 支撑线向后延伸被向下跌破 (Low 低于趋势线)
                    if (klines[x].Low < linePrice)
                    {
                        line.CollidedKlineIndex = x;
                        line.LineExtensionRange = x - line.X2;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 从极值点集合中自动拟合生成所有有效的支撑与阻力趋势线
        /// </summary>
        /// <param name="klines">K线历史数据</param>
        /// <param name="peaks">识别出的波峰集合</param>
        /// <param name="valleys">识别出的波谷集合</param>
        /// <param name="maxSpan">两点之间最大 K 线跨度限制 (默认 100 根)</param>
        /// <param name="allowInternalPenetration">是否允许内部 K 线穿透 (默认 false，即严格拟合外包络趋势线)</param>
        public static (List<TrendLine> ResistanceLines, List<TrendLine> SupportLines) GenerateTrendLines(
            IReadOnlyList<RawKline> klines,
            IReadOnlyList<PivotPoint> peaks,
            IReadOnlyList<PivotPoint> valleys,
            int maxSpan = 100,
            bool allowInternalPenetration = false)
        {
            var resistanceLines = new List<TrendLine>();
            var supportLines = new List<TrendLine>();

            if (klines == null || klines.Count < 2)
            {
                return (resistanceLines, supportLines);
            }

            // 1. 生成阻力趋势线 (波峰与波峰配对)
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

                        if (!allowInternalPenetration && IsResistancePenetratedInternally(p1, p2, klines))
                        {
                            continue;
                        }

                        var line = CreateTrendLine(p1, p2, klines);
                        resistanceLines.Add(line);
                    }
                }
            }

            // 2. 生成支撑趋势线 (波谷与波谷配对)
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

                        if (!allowInternalPenetration && IsSupportPenetratedInternally(p1, p2, klines))
                        {
                            continue;
                        }

                        var line = CreateTrendLine(p1, p2, klines);
                        supportLines.Add(line);
                    }
                }
            }

            return (resistanceLines, supportLines);
        }

        /// <summary>
        /// 一键计算当前 K 线历史中所有活跃趋势线 (自动完成极值点提取与趋势线拟合)
        /// </summary>
        public static (List<TrendLine> ResistanceLines, List<TrendLine> SupportLines) FindActiveTrendLines(
            IReadOnlyList<RawKline> klines,
            int leftLen = 5,
            int rightLen = 5,
            int maxSpan = 100)
        {
            var (peaks, valleys) = PivotHelper.CalculatePeaks(klines, leftLen, rightLen);
            return GenerateTrendLines(klines, peaks, valleys, maxSpan: maxSpan);
        }

        /// <summary>
        /// 检查当前最新 K 线是否突破了指定趋势线
        /// </summary>
        /// <param name="line">待检测趋势线</param>
        /// <param name="kline">当前最新 K 线</param>
        /// <param name="currentIndex">当前 K 线在全局/窗口中的索引</param>
        /// <returns>1: 向上突破阻力线, -1: 向下跌破支撑线, 0: 未突破</returns>
        public static int CheckBreakout(TrendLine line, RawKline kline, int currentIndex)
        {
            if (currentIndex <= line.X2) return 0;

            decimal expectedPrice = line.GetPriceAt(currentIndex);

            if (line.IsResistance && kline.Close > expectedPrice)
            {
                return 1; // 向上有效突破阻力线
            }

            if (line.IsSupport && kline.Close < expectedPrice)
            {
                return -1; // 向下有效跌破支撑线
            }

            return 0;
        }

        #region 内部私有严格包络线校验 (确保两极值点之间没有 K 线穿越线段)

        private static bool IsResistancePenetratedInternally(PivotPoint p1, PivotPoint p2, IReadOnlyList<RawKline> klines)
        {
            int dx = p2.Index - p1.Index;
            decimal dy = p2.Price - p1.Price;
            decimal rawK = dy / dx;

            for (int x = p1.Index + 1; x < p2.Index; x++)
            {
                decimal linePrice = p1.Price + rawK * (x - p1.Index);
                if (klines[x].High > linePrice)
                {
                    return true; // 中间有 K 线穿透了阻力线
                }
            }
            return false;
        }

        private static bool IsSupportPenetratedInternally(PivotPoint p1, PivotPoint p2, IReadOnlyList<RawKline> klines)
        {
            int dx = p2.Index - p1.Index;
            decimal dy = p2.Price - p1.Price;
            decimal rawK = dy / dx;

            for (int x = p1.Index + 1; x < p2.Index; x++)
            {
                decimal linePrice = p1.Price + rawK * (x - p1.Index);
                if (klines[x].Low < linePrice)
                {
                    return true; // 中间有 K 线跌破了支撑线
                }
            }
            return false;
        }

        #endregion
    }
}
