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
    /// 决策区数据模型
    /// 当多根连续上涨后进入决策区，按固定价格水平切线粒度进行切割形成决策区。
    /// 包含：成交 Tick 数量、价格在当前区域停留的总时间、成交量等指标。
    /// </summary>
    public class DecisionZoneItem
    {
        public decimal LowerPrice { get; set; }
        public decimal UpperPrice { get; set; }
        public int TickCount { get; set; }
        public TimeSpan ResidenceTime { get; set; }
        public decimal Volume { get; set; }
        public double DepthRatio { get; set; } // 0.0 ~ 1.0 (相对当前周期最大停留时间的深度比例)

        public string FormattedTime
        {
            get
            {
                if (ResidenceTime.TotalHours >= 1)
                    return $"{(int)ResidenceTime.TotalHours}时{ResidenceTime.Minutes:D2}分";
                if (ResidenceTime.TotalMinutes >= 1)
                    return $"{(int)ResidenceTime.TotalMinutes}分{ResidenceTime.Seconds:D2}秒";
                if (ResidenceTime.TotalSeconds >= 1)
                    return $"{ResidenceTime.TotalSeconds:F1}秒";
                if (ResidenceTime.TotalMilliseconds > 0)
                    return $"{(int)ResidenceTime.TotalMilliseconds}毫秒";
                return "0秒";
            }
        }

        public string FormattedVolume
        {
            get
            {
                if (Volume >= 1_000_000m) return $"{Volume / 1_000_000m:F2}M";
                if (Volume >= 1_000m) return $"{Volume / 1_000m:F1}K";
                return $"{Volume:F1}";
            }
        }

        public override string ToString()
        {
            return $"[{LowerPrice:F2} ~ {UpperPrice:F2}] Ticks:{TickCount}, 停留:{FormattedTime}, 量:{FormattedVolume}, 深度:{DepthRatio:P0}";
        }
    }

    /// <summary>
    /// 决策区计算与图表绘制助手
    /// </summary>
    public static class DecisionZoneHelper
    {
        /// <summary>
        /// 根据 Tick 序列与价格水平切线粒度计算决策区统计指标
        /// </summary>
        public static List<DecisionZoneItem> ComputeDecisionZones(
            IReadOnlyList<RawTick>? ticks,
            int tickCursor,
            decimal fixedPriceStep)
        {
            if (ticks == null || tickCursor <= 0 || fixedPriceStep <= 0)
                return new List<DecisionZoneItem>();

            int count = Math.Min(ticks.Count, tickCursor);
            if (count == 0) return new List<DecisionZoneItem>();

            var zoneDict = new Dictionary<long, (int count, TimeSpan time, decimal vol)>();

            for (int i = 0; i < count; i++)
            {
                var cur = ticks[i];
                decimal p = cur.Price;
                long k = (long)Math.Floor(p / fixedPriceStep);

                TimeSpan deltaT = TimeSpan.Zero;
                if (i < count - 1)
                {
                    long diffMs = ticks[i + 1].Time - cur.Time;
                    if (diffMs > 0)
                    {
                        // 过滤回放暂停或跨度异常的大时间空隙 (如大于 2 分钟异常跳跃归一为 1 秒)
                        deltaT = diffMs > 120_000 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(diffMs);
                    }
                }

                if (!zoneDict.TryGetValue(k, out var val))
                {
                    val = (0, TimeSpan.Zero, 0m);
                }
                zoneDict[k] = (val.count + 1, val.time + deltaT, val.vol + cur.Qty);
            }

            if (zoneDict.Count == 0) return new List<DecisionZoneItem>();

            double maxMs = zoneDict.Values.Max(v => v.time.TotalMilliseconds);
            int maxTicks = zoneDict.Values.Max(v => v.count);

            var result = new List<DecisionZoneItem>(zoneDict.Count);
            foreach (var kvp in zoneDict.OrderBy(k => k.Key))
            {
                decimal pLow = kvp.Key * fixedPriceStep;
                decimal pHigh = pLow + fixedPriceStep;
                double ratio = maxMs > 0
                    ? (kvp.Value.time.TotalMilliseconds / maxMs)
                    : (maxTicks > 0 ? (double)kvp.Value.count / maxTicks : 0);

                result.Add(new DecisionZoneItem
                {
                    LowerPrice = pLow,
                    UpperPrice = pHigh,
                    TickCount = kvp.Value.count,
                    ResidenceTime = kvp.Value.time,
                    Volume = kvp.Value.vol,
                    DepthRatio = Math.Clamp(ratio, 0.0, 1.0)
                });
            }

            return result;
        }

        /// <summary>
        /// 在指定图表区域绘制决策区矩形框与停留时间深度效果
        /// </summary>
        public static void DrawDecisionZones(
            Plot plot,
            IReadOnlyList<DecisionZoneItem>? zones,
            double xStart,
            double xEnd,
            string chineseFont,
            bool isMacroChart = false)
        {
            if (plot == null || zones == null || zones.Count == 0) return;
            if (xEnd <= xStart) xEnd = xStart + 1.0;

            foreach (var zone in zones)
            {
                if (zone.TickCount <= 0) continue;

                // 核心视觉：根据停留时间深度计算矩形填充色透明度
                // 停留时间最长区域呈现深邃明亮的高饱和琥珀金，较短区域呈现通透柔光
                byte alpha = (byte)Math.Clamp(25 + (int)(zone.DepthRatio * 145), 25, 175);
                byte borderAlpha = (byte)Math.Clamp(alpha + 65, 80, 230);

                var rect = plot.Add.Rectangle(xStart, xEnd, (double)zone.LowerPrice, (double)zone.UpperPrice);
                rect.Axes.YAxis = plot.Axes.Left;
                rect.FillColor = Color.FromHex("#f59e0b").WithAlpha(alpha); // 琥珀金
                rect.LineColor = Color.FromHex("#f59e0b").WithAlpha(borderAlpha);
                rect.LineWidth = 0.85f;
                rect.LinePattern = LinePattern.Dashed;

                // 决策区内容标注：成交 Tick 数量与停留总时间
                string text = $"🎯 {zone.TickCount}笔 | {zone.FormattedTime}";
                double xText = xStart + Math.Max(0.2, (xEnd - xStart) * 0.02);
                double yMid = (double)(zone.LowerPrice + zone.UpperPrice) / 2.0;

                var txt = plot.Add.Text(text, xText, yMid);
                txt.Axes.YAxis = plot.Axes.Left;
                txt.LabelFontName = chineseFont;
                txt.LabelFontSize = 8.5f;
                txt.LabelFontColor = Color.FromHex("#fef08a"); // 亮金米白
                txt.LabelAlignment = Alignment.MiddleLeft;
            }
        }
    }
}
