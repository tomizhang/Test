using Common.Helper;
using System;

namespace Test.PercentageBar.WinForms.Models
{
    /// <summary>
    /// K 线切分度量基准类型
    /// </summary>
    public enum SliceUnitType
    {
        /// <summary>
        /// 百分比幅度切分 (%)
        /// </summary>
        Percentage = 0,

        /// <summary>
        /// 固定价格/价差切分 (USDT)
        /// </summary>
        FixedPrice = 1
    }

    /// <summary>
    /// 百分比 / 固定价格 K 线生成切分算法模式
    /// </summary>
    public enum PercentBarMode
    {
        /// <summary>
        /// 基于开盘价涨跌幅度 (From Open)：
        /// 价格自 Open 涨跌达到设定阈值即完成一根 Bar
        /// </summary>
        ChangeFromOpen = 0,

        /// <summary>
        /// 基于极值全振幅 (High-Low Range)：
        /// 当 (High - Low) 达到设定阈值时完成一根 Bar
        /// </summary>
        HighLowRange = 1,

        /// <summary>
        /// 经典 Renko 趋势砖块模式 (Renko Box)：
        /// 仅当价格朝趋势方向突破或反转两倍箱体时切分
        /// </summary>
        Renko = 2
    }

    /// <summary>
    /// 紧凑内存高性能百分比 K 线结构体 (Value Type, 零 GC 堆分配, 包含 OHLC + 精确时间跨度 + 量能指标)
    /// </summary>
    public readonly struct PercentageKline
    {
        /// <summary>
        /// K 线纯序号索引 (0, 1, 2, 3...)
        /// </summary>
        public int BarIndex { get; init; }

        /// <summary>
        /// 开盘价
        /// </summary>
        public decimal Open { get; init; }

        /// <summary>
        /// 最高价
        /// </summary>
        public decimal High { get; init; }

        /// <summary>
        /// 最低价
        /// </summary>
        public decimal Low { get; init; }

        /// <summary>
        /// 收盘价
        /// </summary>
        public decimal Close { get; init; }

        /// <summary>
        /// 开盘精确时间戳 (毫秒)
        /// </summary>
        public long OpenTime { get; init; }

        /// <summary>
        /// 收盘精确时间戳 (毫秒)
        /// </summary>
        public long CloseTime { get; init; }

        /// <summary>
        /// 开盘时间 (UTC)
        /// </summary>
        public DateTime OpenDateTime => TimeHelper.FromUnixTimeMilliseconds(OpenTime);

        /// <summary>
        /// 收盘时间 (UTC)
        /// </summary>
        public DateTime CloseDateTime => TimeHelper.FromUnixTimeMilliseconds(CloseTime);

        /// <summary>
        /// 🌟 时间跨度 (持续时长: 收盘时间 - 开盘时间)
        /// </summary>
        public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, CloseTime - OpenTime));

        /// <summary>
        /// 累积基础币成交量 (Base Volume)
        /// </summary>
        public decimal Volume { get; init; }

        /// <summary>
        /// 累积计价币成交额 (Quote Volume / USDT)
        /// </summary>
        public decimal QuoteVolume { get; init; }

        /// <summary>
        /// 累积成交笔数
        /// </summary>
        public long TradeCount { get; init; }

        /// <summary>
        /// 累积主动买入量 (Taker Buy Base Volume)
        /// </summary>
        public decimal TakerBuyVolume { get; init; }

        /// <summary>
        /// 累积主动买入额 (Taker Buy Quote Volume)
        /// </summary>
        public decimal TakerBuyQuoteVolume { get; init; }

        /// <summary>
        /// 包含的逐笔 Tick 数量
        /// </summary>
        public int TickCount { get; init; }

        /// <summary>
        /// 🌟 本根 K 线内部包含的全量逐笔 Tick 数据切片
        /// </summary>
        public Common.RawTick[]? Ticks { get; init; }

        /// <summary>
        /// 涨跌额 (Close - Open)
        /// </summary>
        public decimal PriceChange => Close - Open;

        /// <summary>
        /// 涨跌幅度百分比 (%)
        /// </summary>
        public decimal PriceChangePct => Open > 0 ? (Close - Open) / Open * 100m : 0m;

        /// <summary>
        /// 极值全振幅百分比 (%)
        /// </summary>
        public decimal PriceAmplitudePct => Low > 0 ? (High - Low) / Low * 100m : 0m;

        /// <summary>
        /// 格式化输出人类可读的持续时间跨度
        /// </summary>
        public string GetFormattedDuration()
        {
            var ts = Duration;
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}天{ts.Hours}时{ts.Minutes}分";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}时{ts.Minutes}分{ts.Seconds}秒";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}分{ts.Seconds}秒";
            if (ts.TotalSeconds >= 1) return $"{ts.Seconds}秒{ts.Milliseconds:D3}ms";
            return $"{ts.Milliseconds}ms";
        }

        public override string ToString()
        {
            string dir = Close >= Open ? "🟢 阳线" : "🔴 阴线";
            return $"[Bar #{BarIndex}] {dir} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} | 涨跌:{PriceChangePct:+0.00;-0.00;0.00}% | 跨度:{GetFormattedDuration()} | Ticks:{TickCount:N0}";
        }
    }
}
