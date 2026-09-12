using Common;
using System;
using System.Collections.Generic;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    public enum ChannelCalculationMode
    {
        LinearRegression,   // 经典线性回归外包络 (默认)
        MinimumHeight       // 极小高度包络 (紧密契合)
    }

    /// <summary>
    /// 动态自适应外包络通道计算引擎
    /// 核心算法：
    /// 1. 左侧 100 根基准 K 线分析，右侧延长 100 根未来预测，总长度 200 根
    /// 2. 上轨严格包含所有高点 (Upper >= High)，下轨严格包含所有低点 (Lower <= Low)
    /// 3. 斜率 k (角度) 与通道截距差 (高度) 随新增 K 线逐根推演实时动态变化
    /// </summary>
    public static class DynamicChannelCalculator
    {
        /// <summary>
        /// 针对当前已回放的 K 线序列，计算当前的动态包络通道
        /// </summary>
        /// <param name="klines">已回放的全部 K 线列表</param>
        /// <param name="currentBarIndex">当前最新推进的 K 线索引 (若小于 0 则默认为最后一根)</param>
        /// <param name="leftLength">左侧计算 K 线跨度 (默认 100)</param>
        /// <param name="rightExtendLength">右侧延长 K 线跨度 (默认 100)</param>
        /// <param name="cumulativeMode">是否为全量累计模式 (若为 true 则包含从第 0 根到当前的所有 K 线；若为 false 则取左侧指定数量窗口)</param>
        /// <param name="mode">计算算法模式 (线性回归包络 / 极小高度包络)</param>
        public static DynamicChannelResult Calculate(
            IReadOnlyList<RawKline> klines,
            int currentBarIndex = -1,
            int leftLength = 100,
            int rightExtendLength = 100,
            bool cumulativeMode = false,
            ChannelCalculationMode mode = ChannelCalculationMode.LinearRegression)
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

            // 1. 计算线性回归斜率 k
            decimal k = CalculateLinearRegressionSlope(klines, startIdx, curr);

            // 若选择极小高度包络模式，在回归斜率周边细粒度搜索使高度极小化的最优斜率
            if (mode == ChannelCalculationMode.MinimumHeight)
            {
                k = OptimizeMinimumHeightSlope(klines, startIdx, curr, k);
            }

            // 2. 求解外包络截距：确保所有高点落在上轨下方或触碰，所有低点落在下轨上方或触碰
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

            // 3. 计算中心价格与归一化百分比指标
            decimal centerPriceAtCurr = (decimal)curr * k + (bUpper + bLower) / 2m;
            if (centerPriceAtCurr <= 0m)
            {
                centerPriceAtCurr = (klines[curr].High + klines[curr].Low) / 2m;
            }

            decimal heightPct = centerPriceAtCurr > 0m ? (height / centerPriceAtCurr) * 100m : 0m;
            decimal slopePct = centerPriceAtCurr > 0m ? (k / centerPriceAtCurr) * 100m : 0m;

            // 4. 估算可视角度 (以百分比斜率 * 5 为感官缩放比例进行 arctan 映射，范围 [-90°, +90°])
            double angleDeg = Math.Atan((double)slopePct * 5.0) * (180.0 / Math.PI);

            int endIdx = curr + rightExtendLength;

            return new DynamicChannelResult
            {
                IsValid = true,
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

        /// <summary>
        /// 计算指定 K 线区间的线性回归斜率 k
        /// </summary>
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

        /// <summary>
        /// 在线性回归斜率附近搜索通道高度最小化的最优斜率
        /// </summary>
        private static decimal OptimizeMinimumHeightSlope(IReadOnlyList<RawKline> klines, int startIdx, int endIdx, decimal baseK)
        {
            decimal bestK = baseK;
            decimal minHeight = CalculateHeightForSlope(klines, startIdx, endIdx, baseK);

            // 搜索范围：在 baseK 的 ±50% 范围内取 20 个采样点
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

        /// <summary>
        /// 严格数学验证：检验通道是否完全满足外包络约束 (上轨 >= 全部高点，下轨 <= 全部低点，且至少有1个触碰高点和1个触碰低点)
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

            return (true, $"验证通过: 严格包络 [{channel.StartX}..{channel.CurrentX}] 共 {channel.LeftLength} 根 K 线，高度={channel.ChannelHeight:F2} USDT, 角度={channel.AngleDeg:F1}°");
        }
    }
}
