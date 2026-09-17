using Common;
using System;
using System.Collections.Generic;

namespace Test.PeriodTickPlayback.WinForms.Models
{
    /// <summary>
    /// 大周期时间类型定义 (默认 30 分钟)
    /// </summary>
    public enum MacroPeriodType
    {
        OneMinute = 1,
        ThreeMinutes = 3,
        FiveMinutes = 5,
        FifteenMinutes = 15,
        ThirtyMinutes = 30, // 默认推荐 30 分钟
        OneHour = 60,
        TwoHour = 120,
        FourHour = 240,
        OneDay = 1440,
        CustomMinutes = 9999
    }

    /// <summary>
    /// 大周期图表展现类型 (蜡烛图 / 收盘折线图)
    /// </summary>
    public enum MacroChartDisplayType
    {
        Candlestick = 0, // 蜡烛图 (K线)
        LineChart = 1    // 收盘折线图
    }

    public static class MacroPeriodExtensions
    {
        public static string ToDisplayName(this MacroPeriodType period, int customMinutes = 30)
        {
            return period switch
            {
                MacroPeriodType.OneMinute => "1分钟 (1m)",
                MacroPeriodType.ThreeMinutes => "3分钟 (3m)",
                MacroPeriodType.FiveMinutes => "5分钟 (5m)",
                MacroPeriodType.FifteenMinutes => "15分钟 (15m)",
                MacroPeriodType.ThirtyMinutes => "30分钟 (30m) [默认]",
                MacroPeriodType.OneHour => "1小时 (1h)",
                MacroPeriodType.TwoHour => "2小时 (2h)",
                MacroPeriodType.FourHour => "4小时 (4h)",
                MacroPeriodType.OneDay => "1天 (1d)",
                MacroPeriodType.CustomMinutes => $"自定义 ({customMinutes}m)",
                _ => $"{period}"
            };
        }

        public static TimeSpan ToTimeSpan(this MacroPeriodType period, int customMinutes = 30)
        {
            return period switch
            {
                MacroPeriodType.OneMinute => TimeSpan.FromMinutes(1),
                MacroPeriodType.ThreeMinutes => TimeSpan.FromMinutes(3),
                MacroPeriodType.FiveMinutes => TimeSpan.FromMinutes(5),
                MacroPeriodType.FifteenMinutes => TimeSpan.FromMinutes(15),
                MacroPeriodType.ThirtyMinutes => TimeSpan.FromMinutes(30),
                MacroPeriodType.OneHour => TimeSpan.FromHours(1),
                MacroPeriodType.TwoHour => TimeSpan.FromHours(2),
                MacroPeriodType.FourHour => TimeSpan.FromHours(4),
                MacroPeriodType.OneDay => TimeSpan.FromDays(1),
                MacroPeriodType.CustomMinutes => TimeSpan.FromMinutes(Math.Max(1, customMinutes)),
                _ => TimeSpan.FromMinutes(30)
            };
        }
    }

    /// <summary>
    /// 定型完成的大周期 K 线数据模型 (纯净 OHLCV)
    /// </summary>
    public class MacroKline
    {
        public int BarIndex { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public long OpenTimeMs { get; set; }
        public long CloseTimeMs { get; set; }

        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TradeCount { get; set; }

        public bool IsBullish => Close >= Open;
        public decimal ChangePrice => Close - Open;
        public decimal ChangePct => Open > 0 ? (ChangePrice / Open) * 100m : 0m;
        public decimal AmplitudePct => Low > 0 ? ((High - Low) / Low) * 100m : 0m;

        public override string ToString()
        {
            string dir = IsBullish ? "▲" : "▼";
            return $"[Bar #{BarIndex} {OpenTime:yyyy-MM-dd HH:mm:ss} ~ {CloseTime:HH:mm:ss}] {dir} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume:F2} ({ChangePct:+0.00%;-0.00%;0.00%}) Ticks:{TradeCount:N0}";
        }
    }

    /// <summary>
    /// 当前正在播放中、随 Tick 动态演变的大周期 K 线形成态模型
    /// </summary>
    public class FormingMacroKline
    {
        public int BucketIndex { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TicksProcessed { get; set; }
        public int TotalTicksInBucket { get; set; }

        public bool HasTicks => TicksProcessed > 0;
        public bool IsBullish => CurrentPrice >= Open;
        public decimal ChangePrice => HasTicks ? CurrentPrice - Open : 0m;
        public decimal ChangePct => (HasTicks && Open > 0) ? (ChangePrice / Open) * 100m : 0m;
        public double ProgressPct => TotalTicksInBucket > 0 ? ((double)TicksProcessed / TotalTicksInBucket) * 100.0 : 0;
    }

    /// <summary>
    /// 宏观周期桶切片 (包含指定时钟对齐周期区间内的全部原始 Tick 数据与预计算完成 K 线)
    /// </summary>
    public class PeriodBucket
    {
        public int BucketIndex { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<RawTick> Ticks { get; set; } = new();
        public MacroKline? FinalKline { get; set; }

        public int TickCount => Ticks.Count;
        public bool IsEmpty => Ticks.Count == 0;
    }

    /// <summary>
    /// 回放引擎运行状态
    /// </summary>
    public enum PlaybackState
    {
        Idle,
        Loading,
        Ready,
        Playing,
        Paused,
        Completed
    }

    /// <summary>
    /// Tick 表格展示数据项
    /// </summary>
    public class TickRowView
    {
        public int Index { get; set; }
        public string TimeStr { get; set; } = "";
        public decimal Price { get; set; }
        public decimal Qty { get; set; }
        public decimal QuoteQty { get; set; }
        public string SideStr { get; set; } = "";
        public bool IsBuyer { get; set; }
    }

    /// <summary>
    /// Tick 与成交量多空比值统计模型
    /// </summary>
    public readonly struct TickLongShortStats
    {
        public int TotalTicks { get; init; }
        public int BuyTicks { get; init; }
        public int SellTicks { get; init; }

        public decimal TotalVolume { get; init; }
        public decimal BuyVolume { get; init; }
        public decimal SellVolume { get; init; }

        public decimal TotalQuoteAmount { get; init; }
        public decimal BuyQuoteAmount { get; init; }
        public decimal SellQuoteAmount { get; init; }

        /// <summary>
        /// Tick 笔数多空比 (Buy / Sell)
        /// </summary>
        public double TickRatio => SellTicks > 0 ? (double)BuyTicks / SellTicks : (BuyTicks > 0 ? 999.99 : 1.0);

        /// <summary>
        /// 成交量多空比 (BuyVol / SellVol)
        /// </summary>
        public double VolumeRatio => SellVolume > 0 ? (double)(BuyVolume / SellVolume) : (BuyVolume > 0 ? 999.99 : 1.0);

        /// <summary>
        /// 成交额多空比 (BuyQuote / SellQuote)
        /// </summary>
        public double QuoteRatio => SellQuoteAmount > 0 ? (double)(BuyQuoteAmount / SellQuoteAmount) : (BuyQuoteAmount > 0 ? 999.99 : 1.0);

        public double BuyTickPct => TotalTicks > 0 ? ((double)BuyTicks / TotalTicks) * 100.0 : 0.0;
        public double SellTickPct => TotalTicks > 0 ? ((double)SellTicks / TotalTicks) * 100.0 : 0.0;

        public double BuyVolumePct => TotalVolume > 0 ? (double)(BuyVolume / TotalVolume) * 100.0 : 0.0;
        public double SellVolumePct => TotalVolume > 0 ? (double)(SellVolume / TotalVolume) * 100.0 : 0.0;

        /// <summary>
        /// 净主动买量 (Delta Volume)
        /// </summary>
        public decimal NetVolume => BuyVolume - SellVolume;

        /// <summary>
        /// 净主动买额 (Delta Quote USDT)
        /// </summary>
        public decimal NetQuoteAmount => BuyQuoteAmount - SellQuoteAmount;

        public static string FormatVolume(decimal vol)
        {
            decimal abs = Math.Abs(vol);
            string sign = vol < 0 ? "-" : (vol > 0 ? "+" : "");
            if (abs >= 1_000_000m) return $"{sign}{abs / 1_000_000m:F2}M";
            if (abs >= 1_000m) return $"{sign}{abs / 1_000m:F2}K";
            return $"{vol:F2}";
        }

        public static string FormatRatio(double ratio, double buyPct, double sellPct)
        {
            if (ratio >= 999.0) return "∞ (多 100% : 空 0%)";
            return $"{ratio:F2} (多 {buyPct:F1}% : 空 {sellPct:F1}%)";
        }

        public static TickLongShortStats Calculate(IReadOnlyList<RawTick>? ticks, int count = -1)
        {
            if (ticks == null || ticks.Count == 0) return default;
            int n = count < 0 ? ticks.Count : Math.Min(ticks.Count, count);
            if (n <= 0) return default;

            int buyTicks = 0;
            int sellTicks = 0;
            decimal buyVol = 0;
            decimal sellVol = 0;
            decimal buyQuote = 0;
            decimal sellQuote = 0;

            for (int i = 0; i < n; i++)
            {
                var t = ticks[i];
                if (!t.IsBuyerMaker)
                {
                    buyTicks++;
                    buyVol += t.Qty;
                    buyQuote += t.QuoteQty;
                }
                else
                {
                    sellTicks++;
                    sellVol += t.Qty;
                    sellQuote += t.QuoteQty;
                }
            }

            return new TickLongShortStats
            {
                TotalTicks = n,
                BuyTicks = buyTicks,
                SellTicks = sellTicks,
                TotalVolume = buyVol + sellVol,
                BuyVolume = buyVol,
                SellVolume = sellVol,
                TotalQuoteAmount = buyQuote + sellQuote,
                BuyQuoteAmount = buyQuote,
                SellQuoteAmount = sellQuote
            };
        }

        /// <summary>
        /// 计算供图表渲染的动态多折线数据序列 (X坐标, Tick笔数多空比序列, 成交量多空比序列)
        /// </summary>
        public static (double[] Xs, double[] TickRatios, double[] VolRatios) CalculateRatioSeries(
            IReadOnlyList<RawTick>? ticks,
            int count,
            int targetSamplePoints = 1500)
        {
            if (ticks == null || ticks.Count == 0 || count <= 0)
            {
                return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
            }

            int n = Math.Min(ticks.Count, count);

            int sampleCount = Math.Min(n, targetSamplePoints);
            double step = (double)(n - 1) / Math.Max(1, sampleCount - 1);

            var sampleIndices = new SortedSet<int> { 0, n - 1 };
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = (int)Math.Round(i * step);
                if (idx >= 0 && idx < n) sampleIndices.Add(idx);
            }

            double[] xs = new double[sampleIndices.Count];
            double[] tickRatios = new double[sampleIndices.Count];
            double[] volRatios = new double[sampleIndices.Count];

            decimal totalVol = 0;
            for (int i = 0; i < n; i++) totalVol += ticks[i].Qty;
            double vBase = n > 0 ? (double)(totalVol / n) : 1.0;
            if (vBase <= 0) vBase = 1.0;

            int curBuyTicks = 0;
            int curSellTicks = 0;
            double curBuyVol = 0;
            double curSellVol = 0;
            int outIdx = 0;
            var sampleEnumerator = sampleIndices.GetEnumerator();
            sampleEnumerator.MoveNext();

            for (int i = 0; i < n; i++)
            {
                var t = ticks[i];
                double q = (double)t.Qty;
                if (!t.IsBuyerMaker)
                {
                    curBuyTicks++;
                    curBuyVol += q;
                }
                else
                {
                    curSellTicks++;
                    curSellVol += q;
                }

                if (i == sampleEnumerator.Current)
                {
                    xs[outIdx] = i;
                    tickRatios[outIdx] = (double)(curBuyTicks + 1) / (curSellTicks + 1);
                    volRatios[outIdx] = (curBuyVol + vBase) / (curSellVol + vBase);
                    outIdx++;

                    if (!sampleEnumerator.MoveNext()) break;
                }
            }

            return (xs, tickRatios, volRatios);
        }
    }
}
