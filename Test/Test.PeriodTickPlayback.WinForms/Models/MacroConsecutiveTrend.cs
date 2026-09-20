using System;
using System.Collections.Generic;

namespace Test.PeriodTickPlayback.WinForms.Models
{
    /// <summary>
    /// 大周期连续走势类型 (连续上涨 / 连续下跌)
    /// </summary>
    public enum MacroConsecutiveTrendType
    {
        Bullish = 1, // 连续上涨
        Bearish = -1 // 连续下跌
    }

    /// <summary>
    /// 大周期连续上涨 / 连续下跌形态识别结果模型
    /// </summary>
    public class MacroConsecutiveTrendItem
    {
        public int Id { get; set; }
        public MacroConsecutiveTrendType Type { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public decimal StartPrice { get; set; }
        public decimal EndPrice { get; set; }
        public int ConfirmedBarIndex { get; set; }
        public decimal PriceChangePct { get; set; }
        public decimal MaxHigh { get; set; }
        public decimal MinLow { get; set; }
        public int MaxHighIndex { get; set; }
        public int MinLowIndex { get; set; }
        public bool IsActive { get; set; }

        #region 平行通道几何属性
        public decimal SlopeK { get; set; }
        public decimal UpperIntercept { get; set; }
        public decimal LowerIntercept { get; set; }
        public bool HasChannel { get; set; }
        public int ChannelBaseBars { get; set; }
        public decimal ChannelHeight => Math.Abs(UpperIntercept - LowerIntercept);
        #endregion

        public int BarCount => EndIndex - StartIndex + 1;
        public bool IsBullish => Type == MacroConsecutiveTrendType.Bullish;

        public override string ToString()
        {
            string dir = IsBullish ? "▲连涨" : "▼连跌";
            string chInfo = HasChannel ? $" [通道基准:{ChannelBaseBars}根, k={SlopeK:F4}]" : "";
            return $"[{dir}] #{StartIndex} ~ #{EndIndex} ({BarCount}根, {PriceChangePct:+0.00;-0.00;0.00}%, 确立点:#{ConfirmedBarIndex}){chInfo}";
        }
    }

    /// <summary>
    /// 大周期连续上涨 / 连续下跌专业识别引擎 (零未来函数)
    /// 满足门槛：连续 5 根及以上收涨/收跌，且累计幅度 >= 2.5% (界面参数可控)
    /// </summary>
    public static class MacroConsecutiveTrendDetector
    {
        /// <summary>
        /// 统一轻量 K 线结构快照，兼顾已定型 K 线与正在凝聚形成的 K 线
        /// </summary>
        public readonly struct BarSnapshot
        {
            public readonly int Index;
            public readonly decimal Open;
            public readonly decimal High;
            public readonly decimal Low;
            public readonly decimal Close;

            public BarSnapshot(int index, decimal open, decimal high, decimal low, decimal close)
            {
                Index = index;
                Open = open;
                High = high;
                Low = low;
                Close = close;
            }
        }

        /// <summary>
        /// 针对指定 K 线区间拟合严格平行的外包络通道
        /// 核心：通过线性回归计算波段整体斜率 k，并通过最大 High 差值与最小 Low 差值确定平行的上轨与下轨
        /// </summary>
        public static void FitParallelChannel(
            IReadOnlyList<BarSnapshot> bars,
            int startIndex,
            int fitEndIndex,
            out decimal slopeK,
            out decimal upperB,
            out decimal lowerB)
        {
            slopeK = 0m;
            upperB = 0m;
            lowerB = 0m;
            if (bars == null || startIndex < 0 || fitEndIndex >= bars.Count || fitEndIndex < startIndex)
            {
                return;
            }

            int n = fitEndIndex - startIndex + 1;
            if (n < 2)
            {
                upperB = bars[startIndex].High;
                lowerB = bars[startIndex].Low;
                return;
            }

            decimal sumX = 0m;
            decimal sumY = 0m;
            for (int idx = startIndex; idx <= fitEndIndex; idx++)
            {
                sumX += idx;
                sumY += (bars[idx].High + bars[idx].Low) / 2m;
            }
            decimal meanX = sumX / n;
            decimal meanY = sumY / n;

            decimal num = 0m;
            decimal den = 0m;
            for (int idx = startIndex; idx <= fitEndIndex; idx++)
            {
                decimal dx = idx - meanX;
                decimal dy = ((bars[idx].High + bars[idx].Low) / 2m) - meanY;
                num += dx * dy;
                den += dx * dx;
            }

            slopeK = den != 0m ? num / den : 0m;

            decimal maxDiffH = decimal.MinValue;
            decimal minDiffL = decimal.MaxValue;
            for (int idx = startIndex; idx <= fitEndIndex; idx++)
            {
                decimal diffH = bars[idx].High - slopeK * idx;
                decimal diffL = bars[idx].Low - slopeK * idx;
                if (diffH > maxDiffH) maxDiffH = diffH;
                if (diffL < minDiffL) minDiffL = diffL;
            }

            upperB = maxDiffH;
            lowerB = minDiffL;
        }

        /// <summary>
        /// 扫描大周期 K 线序列中所有符合门槛的连续涨跌波段
        /// </summary>
        public static List<MacroConsecutiveTrendItem> ScanTrends(
            IReadOnlyList<MacroKline>? completedBars,
            FormingMacroKline? formingBar,
            int minBars = 5,
            decimal minChangePct = 2.5m)
        {
            int completedCount = completedBars?.Count ?? 0;
            bool hasForming = formingBar != null && formingBar.HasTicks;
            int totalCount = completedCount + (hasForming ? 1 : 0);
            if (totalCount < minBars) return new List<MacroConsecutiveTrendItem>();

            var bars = new List<BarSnapshot>(totalCount);
            if (completedBars != null)
            {
                for (int idx = 0; idx < completedBars.Count; idx++)
                {
                    var b = completedBars[idx];
                    bars.Add(new BarSnapshot(idx, b.Open, b.High, b.Low, b.Close));
                }
            }
            if (hasForming)
            {
                bars.Add(new BarSnapshot(completedCount, formingBar!.Open, formingBar.High, formingBar.Low, formingBar.CurrentPrice));
            }

            return ScanTrends(bars, minBars, minChangePct);
        }

        /// <summary>
        /// 扫描任意 K 线快照序列 (支持宏观大周期 K 线与微观子周期 K 线) 中所有符合门槛的连续涨跌波段
        /// 并基于 (最低要求K线数 - 1) 自动拟合平行通道
        /// </summary>
        public static List<MacroConsecutiveTrendItem> ScanTrends(
            IReadOnlyList<BarSnapshot> bars,
            int minBars = 5,
            decimal minChangePct = 2.5m)
        {
            var result = new List<MacroConsecutiveTrendItem>();
            if (bars == null || bars.Count < minBars) return result;
            if (minBars < 2) minBars = 2;
            if (minChangePct < 0m) minChangePct = 0m;

            int n = bars.Count;
            int i = 0;

            while (i < n)
            {
                int streakDir = 0;
                if (IsRisingBar(bars, i)) streakDir = 1;
                else if (IsFallingBar(bars, i)) streakDir = -1;
                else
                {
                    i++;
                    continue;
                }

                int streakStart = i;
                decimal startPrice = streakStart > 0 ? bars[streakStart - 1].Close : bars[streakStart].Open;
                if (startPrice <= 0m) startPrice = bars[streakStart].Open;

                int confirmedAt = -1;
                int j = i;

                while (j < n)
                {
                    bool continues = streakDir == 1 ? IsRisingBar(bars, j) : IsFallingBar(bars, j);
                    if (!continues)
                    {
                        break;
                    }

                    int countSoFar = j - streakStart + 1;
                    decimal endPrice = bars[j].Close;
                    decimal changePct = startPrice > 0m
                        ? ((endPrice - startPrice) / startPrice) * 100m
                        : 0m;

                    // 一旦连续根数达到门槛且累计涨跌幅度达到门槛，在当前根立即确立！
                    if (countSoFar >= minBars && Math.Abs(changePct) >= minChangePct && confirmedAt < 0)
                    {
                        confirmedAt = j;
                    }

                    j++;
                }

                int streakEnd = j - 1;
                int currentTotal = streakEnd - streakStart + 1;
                decimal finalEndPrice = bars[streakEnd].Close;
                decimal finalChangePct = startPrice > 0m
                    ? ((finalEndPrice - startPrice) / startPrice) * 100m
                    : 0m;

                if (confirmedAt >= 0 && currentTotal >= minBars && Math.Abs(finalChangePct) >= minChangePct)
                {
                    decimal maxH = decimal.MinValue;
                    decimal minL = decimal.MaxValue;
                    int maxHIdx = streakStart;
                    int minLIdx = streakStart;

                    for (int k = streakStart; k <= streakEnd; k++)
                    {
                        if (bars[k].High > maxH) { maxH = bars[k].High; maxHIdx = k; }
                        if (bars[k].Low < minL) { minL = bars[k].Low; minLIdx = k; }
                    }

                    int stableId = (streakDir > 0 ? 1000000 : 2000000) + streakStart;

                    // 核心关键：严格根据 (最低要求K线数 - 1) 作为基准跨度拟合平行通道！
                    int channelBase = Math.Max(2, minBars - 1);
                    int fitEnd = Math.Min(streakEnd, streakStart + channelBase - 1);
                    FitParallelChannel(bars, streakStart, fitEnd, out decimal slopeK, out decimal upperB, out decimal lowerB);

                    result.Add(new MacroConsecutiveTrendItem
                    {
                        Id = stableId,
                        Type = streakDir == 1 ? MacroConsecutiveTrendType.Bullish : MacroConsecutiveTrendType.Bearish,
                        StartIndex = streakStart,
                        EndIndex = streakEnd,
                        StartPrice = startPrice,
                        EndPrice = finalEndPrice,
                        ConfirmedBarIndex = confirmedAt,
                        PriceChangePct = finalChangePct,
                        MaxHigh = maxH,
                        MinLow = minL,
                        MaxHighIndex = maxHIdx,
                        MinLowIndex = minLIdx,
                        IsActive = streakEnd == n - 1,
                        SlopeK = slopeK,
                        UpperIntercept = upperB,
                        LowerIntercept = lowerB,
                        HasChannel = upperB > lowerB,
                        ChannelBaseBars = channelBase
                    });
                }

                i = j; // 步进至下一个不同走势的起点
            }

            return result;
        }

        private static bool IsRisingBar(IReadOnlyList<BarSnapshot> bars, int index)
        {
            if (index < 0 || index >= bars.Count) return false;
            var curr = bars[index];
            if (index == 0) return curr.Close > curr.Open;
            var prev = bars[index - 1];
            return curr.Close > prev.Close || (curr.Close > curr.Open && curr.Close >= prev.Close);
        }

        private static bool IsFallingBar(IReadOnlyList<BarSnapshot> bars, int index)
        {
            if (index < 0 || index >= bars.Count) return false;
            var curr = bars[index];
            if (index == 0) return curr.Close < curr.Open;
            var prev = bars[index - 1];
            return curr.Close < prev.Close || (curr.Close < curr.Open && curr.Close <= prev.Close);
        }
    }
}