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
    }
}
