using Common;
using System;
using System.Collections.Generic;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    public enum ChannelCalculationMode
    {
        ThreePointAuto = 0,             // 三点智能自适应 (2低1高 / 2高1低 自动择优) [默认推荐]
        ThreePointTwoLowsOneHigh = 1,   // 三点通道 (强制 2低点1高点 · 支撑基准/上升优先)
        ThreePointTwoHighsOneLow = 2,   // 三点通道 (强制 2高点1低点 · 阻力基准/下降优先)
        LinearRegression = 3,           // 经典线性回归外包络 (全量回归均值)
        MinimumHeight = 4               // 极小高度包络 (紧密契合)
    }

    /// <summary>
    /// 动态自适应外包络通道计算引擎
    /// 核心算法：
    /// 1. 支持以三个点确认方向：两个低点一个高点 (支撑基准/上升) 或 两个高点一个低点 (阻力基准/下降)
    /// 2. 上轨严格包含所有高点 (Upper >= High)，下轨严格包含所有低点 (Lower <= Low)
    /// 3. 左侧 100 根基准 K 线分析，右侧延长 100 根未来预测，总长度 200 根
    /// 4. 斜率 k (角度) 与通道截距差 (高度) 随新增 K 线逐根推演实时动态变化
    /// </summary>
    public static class DynamicChannelCalculator
    {
        private struct Candidate3Point
        {
            public bool IsTwoLows;
            public int P1Idx;
            public decimal P1Price;
            public int P2Idx;
            public decimal P2Price;
            public int P3Idx;
            public decimal P3Price;
            public decimal SlopeK;
            public decimal UpperIntercept;
            public decimal LowerIntercept;
            public decimal Height => UpperIntercept - LowerIntercept;
            public int Span => Math.Abs(P2Idx - P1Idx);
            public double Score;
        }

        /// <summary>
        /// 针对当前已回放的 K 线序列，计算当前的动态包络通道
        /// </summary>
        public static DynamicChannelResult Calculate(
            IReadOnlyList<RawKline> klines,
            int currentBarIndex = -1,
            int leftLength = 100,
            int rightExtendLength = 100,
            bool cumulativeMode = false,
            ChannelCalculationMode mode = ChannelCalculationMode.ThreePointAuto)
        {
            if (klines == null || klines.Count < 2)
            {
                return new DynamicChannelResult { IsValid = false };
            }

            int totalAvailable = klines.Count;
            int curr = currentBarIndex >= 0 && currentBarIndex < totalAvailable ? currentBarIndex : totalAvailable - 1;

            int startIdx;
            if (cumulativeMode)
            {
                startIdx = 0;
            }
            else
            {
                startIdx = Math.Max(0, curr - leftLength + 1);
            }

            int count = curr - startIdx + 1;
            if (count < 2)
            {
                return new DynamicChannelResult { IsValid = false };
            }

            // 根据模式分流：三点通道计算 或 经典回归计算
            if (mode == ChannelCalculationMode.LinearRegression || mode == ChannelCalculationMode.MinimumHeight)
            {
                return CalculateLinearRegressionChannel(klines, startIdx, curr, rightExtendLength, mode);
            }
            else
            {
                return CalculateThreePointChannel(klines, startIdx, curr, rightExtendLength, mode);
            }
        }

        #region 三点通道确认核心算法 (2低1高 或 2高1低)

        private static DynamicChannelResult CalculateThreePointChannel(
            IReadOnlyList<RawKline> klines,
            int startIdx,
            int curr,
            int rightExtendLength,
            ChannelCalculationMode mode)
        {
            var lowerHull = ComputeLowerConvexHull(klines, startIdx, curr);
            var upperHull = ComputeUpperConvexHull(klines, startIdx, curr);

            var candidates = new List<Candidate3Point>();

            // 1. 遍历下凸包的所有相邻边生成【2个低点 + 1个高点】候选通道
            if (mode == ChannelCalculationMode.ThreePointAuto || mode == ChannelCalculationMode.ThreePointTwoLowsOneHigh)
            {
                for (int i = 0; i < lowerHull.Count - 1; i++)
                {
                    int idx1 = lowerHull[i];
                    int idx2 = lowerHull[i + 1];
                    decimal low1 = klines[idx1].Low;
                    decimal low2 = klines[idx2].Low;
                    int span = idx2 - idx1;
                    if (span <= 0) continue;

                    decimal k = (low2 - low1) / (decimal)span;
                    decimal bLower = low1 - k * idx1;

                    // 寻找对侧最高触碰点 (1个高点)
                    decimal maxDiffH = decimal.MinValue;
                    int highIdx = startIdx;
                    decimal highPrice = 0m;

                    for (int j = startIdx; j <= curr; j++)
                    {
                        decimal diffH = klines[j].High - k * j;
                        if (diffH > maxDiffH)
                        {
                            maxDiffH = diffH;
                            highIdx = j;
                            highPrice = klines[j].High;
                        }
                    }

                    decimal bUpper = maxDiffH;
                    decimal h = bUpper - bLower;

                    // 评分函数：优先较小高度（契合紧密），适度奖励跨度 Span
                    double spanWeight = Math.Min(1.0, span / 25.0);
                    double score = (double)h / (1.0 + 0.35 * spanWeight);

                    candidates.Add(new Candidate3Point
                    {
                        IsTwoLows = true,
                        P1Idx = idx1,
                        P1Price = low1,
                        P2Idx = idx2,
                        P2Price = low2,
                        P3Idx = highIdx,
                        P3Price = highPrice,
                        SlopeK = k,
                        UpperIntercept = bUpper,
                        LowerIntercept = bLower,
                        Score = score
                    });
                }
            }

            // 2. 遍历上凸包的所有相邻边生成【2个高点 + 1个低点】候选通道
            if (mode == ChannelCalculationMode.ThreePointAuto || mode == ChannelCalculationMode.ThreePointTwoHighsOneLow)
            {
                for (int i = 0; i < upperHull.Count - 1; i++)
                {
                    int idx1 = upperHull[i];
                    int idx2 = upperHull[i + 1];
                    decimal high1 = klines[idx1].High;
                    decimal high2 = klines[idx2].High;
                    int span = idx2 - idx1;
                    if (span <= 0) continue;

                    decimal k = (high2 - high1) / (decimal)span;
                    decimal bUpper = high1 - k * idx1;

                    // 寻找对侧最低触碰点 (1个低点)
                    decimal minDiffL = decimal.MaxValue;
                    int lowIdx = startIdx;
                    decimal lowPrice = 0m;

                    for (int j = startIdx; j <= curr; j++)
                    {
                        decimal diffL = klines[j].Low - k * j;
                        if (diffL < minDiffL)
                        {
                            minDiffL = diffL;
                            lowIdx = j;
                            lowPrice = klines[j].Low;
                        }
                    }

                    decimal bLower = minDiffL;
                    decimal h = bUpper - bLower;

                    double spanWeight = Math.Min(1.0, span / 25.0);
                    double score = (double)h / (1.0 + 0.35 * spanWeight);

                    candidates.Add(new Candidate3Point
                    {
                        IsTwoLows = false,
                        P1Idx = idx1,
                        P1Price = high1,
                        P2Idx = idx2,
                        P2Price = high2,
                        P3Idx = lowIdx,
                        P3Price = lowPrice,
                        SlopeK = k,
                        UpperIntercept = bUpper,
                        LowerIntercept = bLower,
                        Score = score
                    });
                }
            }

            // 若凸包边缘不足以生成三点候选，回退至经典线性回归模式保底
            if (candidates.Count == 0)
            {
                return CalculateLinearRegressionChannel(klines, startIdx, curr, rightExtendLength, ChannelCalculationMode.LinearRegression);
            }

            // 排序选出得分最优的候选三点通道
            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            var best = candidates[0];

            decimal centerPriceAtCurr = (decimal)curr * best.SlopeK + (best.UpperIntercept + best.LowerIntercept) / 2m;
            if (centerPriceAtCurr <= 0m)
            {
                centerPriceAtCurr = (klines[curr].High + klines[curr].Low) / 2m;
            }

            decimal heightPct = centerPriceAtCurr > 0m ? (best.Height / centerPriceAtCurr) * 100m : 0m;
            decimal slopePct = centerPriceAtCurr > 0m ? (best.SlopeK / centerPriceAtCurr) * 100m : 0m;
            double angleDeg = Math.Atan((double)slopePct * 5.0) * (180.0 / Math.PI);

            int endIdx = curr + rightExtendLength;

            var result = new DynamicChannelResult
            {
                IsValid = true,
                DirectionType = best.IsTwoLows ? ChannelDirectionType.TwoLowsOneHigh : ChannelDirectionType.TwoHighsOneLow,
                StartX = startIdx,
                CurrentX = curr,
                EndX = endIdx,
                SlopeK = best.SlopeK,
                SlopePct = slopePct,
                AngleDeg = angleDeg,
                UpperIntercept = best.UpperIntercept,
                LowerIntercept = best.LowerIntercept,
                ChannelHeightPct = heightPct,

                BasePoint1Index = best.P1Idx,
                BasePoint1Price = best.P1Price,
                BasePoint2Index = best.P2Idx,
                BasePoint2Price = best.P2Price,
                OppositePointIndex = best.P3Idx,
                OppositePointPrice = best.P3Price
            };

            if (best.IsTwoLows)
            {
                result.TouchLowIndex = best.P1Idx;
                result.TouchLowPrice = best.P1Price;
                result.TouchLowIndex2 = best.P2Idx;
                result.TouchLowPrice2 = best.P2Price;
                result.TouchHighIndex = best.P3Idx;
                result.TouchHighPrice = best.P3Price;
            }
            else
            {
                result.TouchHighIndex = best.P1Idx;
                result.TouchHighPrice = best.P1Price;
                result.TouchHighIndex2 = best.P2Idx;
                result.TouchHighPrice2 = best.P2Price;
                result.TouchLowIndex = best.P3Idx;
                result.TouchLowPrice = best.P3Price;
            }

            return result;
        }

        private static List<int> ComputeLowerConvexHull(IReadOnlyList<RawKline> klines, int start, int end)
        {
            var hull = new List<int>();
            for (int i = start; i <= end; i++)
            {
                while (hull.Count >= 2)
                {
                    int p1 = hull[hull.Count - 2];
                    int p2 = hull[hull.Count - 1];
                    decimal dx1 = p2 - p1;
                    decimal dy1 = klines[p2].Low - klines[p1].Low;
                    decimal dx2 = i - p2;
                    decimal dy2 = klines[i].Low - klines[p2].Low;
                    decimal cross = dx1 * dy2 - dy1 * dx2;
                    if (cross <= 0) hull.RemoveAt(hull.Count - 1);
                    else break;
                }
                hull.Add(i);
            }
            return hull;
        }

        private static List<int> ComputeUpperConvexHull(IReadOnlyList<RawKline> klines, int start, int end)
        {
            var hull = new List<int>();
            for (int i = start; i <= end; i++)
            {
                while (hull.Count >= 2)
                {
                    int p1 = hull[hull.Count - 2];
                    int p2 = hull[hull.Count - 1];
                    decimal dx1 = p2 - p1;
                    decimal dy1 = klines[p2].High - klines[p1].High;
                    decimal dx2 = i - p2;
                    decimal dy2 = klines[i].High - klines[p2].High;
                    decimal cross = dx1 * dy2 - dy1 * dx2;
                    if (cross >= 0) hull.RemoveAt(hull.Count - 1);
                    else break;
                }
                hull.Add(i);
            }
            return hull;
        }

        #endregion

        #region 线性回归包络算法

        private static DynamicChannelResult CalculateLinearRegressionChannel(
            IReadOnlyList<RawKline> klines,
            int startIdx,
            int curr,
            int rightExtendLength,
            ChannelCalculationMode mode)
        {
            decimal k = CalculateLinearRegressionSlope(klines, startIdx, curr);

            if (mode == ChannelCalculationMode.MinimumHeight)
            {
                k = OptimizeMinimumHeightSlope(klines, startIdx, curr, k);
            }

            decimal maxDiffHigh = decimal.MinValue;
            decimal minDiffLow = decimal.MaxValue;
            int touchHighIdx = startIdx;
            decimal touchHighPrice = 0m;
            int touchLowIdx = startIdx;
            decimal touchLowPrice = 0m;

            for (int i = startIdx; i <= curr; i++)
            {
                var bar = klines[i];
                decimal diffH = bar.High - k * i;
                decimal diffL = bar.Low - k * i;

                if (diffH > maxDiffHigh)
                {
                    maxDiffHigh = diffH;
                    touchHighIdx = i;
                    touchHighPrice = bar.High;
                }

                if (diffL < minDiffLow)
                {
                    minDiffLow = diffL;
                    touchLowIdx = i;
                    touchLowPrice = bar.Low;
                }
            }

            decimal bUpper = maxDiffHigh;
            decimal bLower = minDiffLow;
            decimal height = bUpper - bLower;

            decimal centerPriceAtCurr = (decimal)curr * k + (bUpper + bLower) / 2m;
            if (centerPriceAtCurr <= 0m)
            {
                centerPriceAtCurr = (klines[curr].High + klines[curr].Low) / 2m;
            }

            decimal heightPct = centerPriceAtCurr > 0m ? (height / centerPriceAtCurr) * 100m : 0m;
            decimal slopePct = centerPriceAtCurr > 0m ? (k / centerPriceAtCurr) * 100m : 0m;
            double angleDeg = Math.Atan((double)slopePct * 5.0) * (180.0 / Math.PI);

            int endIdx = curr + rightExtendLength;

            return new DynamicChannelResult
            {
                IsValid = true,
                DirectionType = ChannelDirectionType.LinearRegression,
                StartX = startIdx,
                CurrentX = curr,
                EndX = endIdx,
                SlopeK = k,
                SlopePct = slopePct,
                AngleDeg = angleDeg,
                UpperIntercept = bUpper,
                LowerIntercept = bLower,
                ChannelHeightPct = heightPct,
                TouchHighIndex = touchHighIdx,
                TouchHighPrice = touchHighPrice,
                TouchLowIndex = touchLowIdx,
                TouchLowPrice = touchLowPrice
            };
        }

        private static decimal CalculateLinearRegressionSlope(IReadOnlyList<RawKline> klines, int startIdx, int endIdx)
        {
            int count = endIdx - startIdx + 1;
            decimal sumX = 0m;
            decimal sumY = 0m;

            for (int i = startIdx; i <= endIdx; i++)
            {
                sumX += i;
                sumY += (klines[i].High + klines[i].Low) / 2m;
            }

            decimal avgX = sumX / count;
            decimal avgY = sumY / count;

            decimal covXY = 0m;
            decimal varX = 0m;

            for (int i = startIdx; i <= endIdx; i++)
            {
                decimal dx = i - avgX;
                decimal dy = (klines[i].High + klines[i].Low) / 2m - avgY;
                covXY += dx * dy;
                varX += dx * dx;
            }

            if (varX == 0m) return 0m;
            return covXY / varX;
        }

        private static decimal OptimizeMinimumHeightSlope(IReadOnlyList<RawKline> klines, int startIdx, int endIdx, decimal baseK)
        {
            decimal bestK = baseK;
            decimal minHeight = CalculateHeightForSlope(klines, startIdx, endIdx, baseK);

            decimal step = Math.Abs(baseK) > 0.0001m ? Math.Abs(baseK) * 0.05m : 0.01m;
            for (int i = -10; i <= 10; i++)
            {
                if (i == 0) continue;
                decimal testK = baseK + i * step;
                decimal testH = CalculateHeightForSlope(klines, startIdx, endIdx, testK);
                if (testH < minHeight)
                {
                    minHeight = testH;
                    bestK = testK;
                }
            }

            return bestK;
        }

        private static decimal CalculateHeightForSlope(IReadOnlyList<RawKline> klines, int startIdx, int endIdx, decimal testK)
        {
            decimal maxDiffH = decimal.MinValue;
            decimal minDiffL = decimal.MaxValue;

            for (int i = startIdx; i <= endIdx; i++)
            {
                decimal diffH = klines[i].High - testK * i;
                decimal diffL = klines[i].Low - testK * i;
                if (diffH > maxDiffH) maxDiffH = diffH;
                if (diffL < minDiffL) minDiffL = diffL;
            }

            return maxDiffH - minDiffL;
        }

        #endregion

        /// <summary>
        /// 严格数学验证：检验通道是否完全满足外包络约束 (上轨 >= 全部高点，下轨 <= 全部低点，且包含锚定点)
        /// </summary>
        public static (bool IsValid, string Message) ValidateEnclosure(IReadOnlyList<RawKline> klines, in DynamicChannelResult channel)
        {
            if (!channel.IsValid) return (false, "通道未就绪");

            decimal tolerance = 0.0001m;
            bool touchedHigh = false;
            bool touchedLow = false;

            for (int i = channel.StartX; i <= channel.CurrentX; i++)
            {
                var bar = klines[i];
                decimal up = channel.GetUpperPrice(i);
                decimal low = channel.GetLowerPrice(i);

                if (bar.High > up + tolerance)
                {
                    return (false, $"违反外包络上轨约束: K线 #{i} High={bar.High} > Upper={up}");
                }

                if (bar.Low < low - tolerance)
                {
                    return (false, $"违反外包络下轨约束: K线 #{i} Low={bar.Low} < Lower={low}");
                }

                if (Math.Abs(bar.High - up) <= tolerance) touchedHigh = true;
                if (Math.Abs(bar.Low - low) <= tolerance) touchedLow = true;
            }

            if (!touchedHigh) return (false, "警告: 未检测到贴合上轨的触碰极高点");
            if (!touchedLow) return (false, "警告: 未检测到贴合下轨的触碰极低点");

            return (true, $"验证通过: [{channel.DirectionDescription}] 严格包络 [{channel.StartX}..{channel.CurrentX}] 共 {channel.LeftLength} 根 K 线，高度={channel.ChannelHeight:F2} USDT, 角度={channel.AngleDeg:F1}°");
        }
    }
}
