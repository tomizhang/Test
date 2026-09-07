using Binance.Net.Enums;
using System;
using System.Collections.Generic;

namespace Test.MultiChart.WinForms.Models
{
    /// <summary>
    /// 图表画线类型
    /// </summary>
    public enum DrawingType
    {
        /// <summary>
        /// 趋势线 (两点确定一条延伸线段)
        /// </summary>
        TrendLine = 0,

        /// <summary>
        /// 水平线 (单个价格水平射线/全屏水平线)
        /// </summary>
        HorizontalLine = 1
    }

    /// <summary>
    /// 画线交互工具模式
    /// </summary>
    public enum DrawingToolMode
    {
        /// <summary>
        /// 🖱️ 普通光标模式 (十字光标、查价、拾取与平移)
        /// </summary>
        Pointer = 0,

        /// <summary>
        /// 📐 绘制趋势线模式
        /// </summary>
        TrendLine = 1,

        /// <summary>
        /// ➖ 绘制水平线模式
        /// </summary>
        HorizontalLine = 2,

        /// <summary>
        /// 🗑️ 橡皮擦模式 (点击删除线条)
        /// </summary>
        Eraser = 3
    }

    /// <summary>
    /// 多窗口分屏布局模式
    /// </summary>
    public enum ChartLayoutMode
    {
        /// <summary>
        /// 1×1 单图模式
        /// </summary>
        Single_1x1 = 0,

        /// <summary>
        /// 1×2 上下双联分屏
        /// </summary>
        DualVertical_1x2 = 1,

        /// <summary>
        /// 2×1 左右双联分屏
        /// </summary>
        DualHorizontal_2x1 = 2,

        /// <summary>
        /// 2×2 四分屏四象限
        /// </summary>
        Quad_2x2 = 3,

        /// <summary>
        /// 3×2 六分屏网格
        /// </summary>
        Six_3x2 = 4
    }

    /// <summary>
    /// K 线价格坐标展示模式
    /// </summary>
    public enum PriceScaleMode
    {
        /// <summary>
        /// 💰 真实绝对价格模式 (USDT)
        /// </summary>
        AbsolutePrice = 0,

        /// <summary>
        /// 📊 基准百分比涨跌幅模式 (%)
        /// </summary>
        PercentageChange = 1,

        /// <summary>
        /// 💵 固定 100U 价格归一化模式 (Normalized 100u Base)
        /// </summary>
        Normalized100U = 2
    }

    /// <summary>
    /// K 线独立图表展示形态
    /// </summary>
    public enum ChartDisplayType
    {
        /// <summary>
        /// 🕯️ 标准蜡烛图 (Candlestick)
        /// </summary>
        Candlestick = 0,

        /// <summary>
        /// 📈 收盘价折线 (Line Chart)
        /// </summary>
        Line = 1,

        /// <summary>
        /// 🏔️ 山峰面积图 (Mountain / Area Chart)
        /// </summary>
        Mountain = 2,

        /// <summary>
        /// 📊 美国线 (OHLC Bars)
        /// </summary>
        OhlcBars = 3,

        /// <summary>
        /// ☯️ 平均K线 (Heikin-Ashi)
        /// </summary>
        HeikinAshi = 4
    }

    /// <summary>
    /// 跨窗口同步画线图元数据模型 (基于绝对时空坐标：Symbol + Timestamp + Price)
    /// </summary>
    public class DrawingItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Symbol { get; set; } = "BTCUSDT";
        public DrawingType Type { get; set; } = DrawingType.TrendLine;

        public long Time1 { get; set; }
        public double Price1 { get; set; }

        public long Time2 { get; set; }
        public double Price2 { get; set; }

        public string ColorHex { get; set; } = "#38bdf8"; // 默认天蓝色
        public float LineWidth { get; set; } = 1.5f;
        public bool IsSelected { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DrawingItem Clone()
        {
            return new DrawingItem
            {
                Id = this.Id,
                Symbol = this.Symbol,
                Type = this.Type,
                Time1 = this.Time1,
                Price1 = this.Price1,
                Time2 = this.Time2,
                Price2 = this.Price2,
                ColorHex = this.ColorHex,
                LineWidth = this.LineWidth,
                IsSelected = this.IsSelected,
                CreatedAt = this.CreatedAt
            };
        }
    }

    /// <summary>
    /// 图表时间周期 (包含 Tick 逐笔级别与标准 K 线周期)
    /// </summary>
    public enum ChartInterval
    {
        /// <summary>
        /// ⚡ 逐笔 Tick (每笔成交一个独立点位)
        /// </summary>
        Tick = 0,

        /// <summary>
        /// 1秒 K线
        /// </summary>
        OneSecond = 1,

        /// <summary>
        /// 1分钟 K线
        /// </summary>
        OneMinute = 2,

        /// <summary>
        /// 3分钟 K线
        /// </summary>
        ThreeMinutes = 3,

        /// <summary>
        /// 5分钟 K线
        /// </summary>
        FiveMinutes = 4,

        /// <summary>
        /// 15分钟 K线
        /// </summary>
        FifteenMinutes = 5,

        /// <summary>
        /// 30分钟 K线
        /// </summary>
        ThirtyMinutes = 6,

        /// <summary>
        /// 1小时 K线
        /// </summary>
        OneHour = 7,

        /// <summary>
        /// 4小时 K线
        /// </summary>
        FourHour = 8,

        /// <summary>
        /// 1天 K线
        /// </summary>
        OneDay = 9
    }

    public static class ChartIntervalExtensions
    {
        public static string ToDisplayString(this ChartInterval interval)
        {
            return interval switch
            {
                ChartInterval.Tick => "Tick",
                ChartInterval.OneSecond => "1s",
                ChartInterval.OneMinute => "1m",
                ChartInterval.ThreeMinutes => "3m",
                ChartInterval.FiveMinutes => "5m",
                ChartInterval.FifteenMinutes => "15m",
                ChartInterval.ThirtyMinutes => "30m",
                ChartInterval.OneHour => "1h",
                ChartInterval.FourHour => "4h",
                ChartInterval.OneDay => "1d",
                _ => "1m"
            };
        }

        public static ChartInterval ParseChartInterval(string? str)
        {
            if (string.IsNullOrWhiteSpace(str)) return ChartInterval.OneMinute;
            return str.Trim() switch
            {
                "Tick" or "tick" => ChartInterval.Tick,
                "1s" => ChartInterval.OneSecond,
                "1m" => ChartInterval.OneMinute,
                "3m" => ChartInterval.ThreeMinutes,
                "5m" => ChartInterval.FiveMinutes,
                "15m" => ChartInterval.FifteenMinutes,
                "30m" => ChartInterval.ThirtyMinutes,
                "1h" => ChartInterval.OneHour,
                "4h" => ChartInterval.FourHour,
                "1d" => ChartInterval.OneDay,
                _ => ChartInterval.OneMinute
            };
        }

        public static KlineInterval ToKlineInterval(this ChartInterval interval)
        {
            return interval switch
            {
                ChartInterval.OneSecond => KlineInterval.OneSecond,
                ChartInterval.OneMinute => KlineInterval.OneMinute,
                ChartInterval.ThreeMinutes => KlineInterval.ThreeMinutes,
                ChartInterval.FiveMinutes => KlineInterval.FiveMinutes,
                ChartInterval.FifteenMinutes => KlineInterval.FifteenMinutes,
                ChartInterval.ThirtyMinutes => KlineInterval.ThirtyMinutes,
                ChartInterval.OneHour => KlineInterval.OneHour,
                ChartInterval.FourHour => KlineInterval.FourHour,
                ChartInterval.OneDay => KlineInterval.OneDay,
                _ => KlineInterval.OneMinute
            };
        }
    }

    /// <summary>
    /// 单个分屏窗口持久化配置模型
    /// </summary>
    public class ChartPaneSettings
    {
        public int PaneIndex { get; set; }
        public string Symbol { get; set; } = "BTCUSDT";
        public ChartInterval Interval { get; set; } = ChartInterval.OneMinute;
        public ChartDisplayType DisplayType { get; set; } = ChartDisplayType.Candlestick;
        public PriceScaleMode PriceScale { get; set; } = PriceScaleMode.AbsolutePrice;
        public bool ShowVolume { get; set; } = true;
        public bool ShowMa { get; set; } = true;
    }

    /// <summary>
    /// 全局多窗口工作台持久化配置总模型 (存储于 MultiChartSettings.json)
    /// </summary>
    public class MultiChartSettings
    {
        public ChartLayoutMode LayoutMode { get; set; } = ChartLayoutMode.Quad_2x2;
        public string GlobalSymbol { get; set; } = "BTCUSDT";
        public bool AutoSyncDrawings { get; set; } = true;
        public bool SyncCrosshair { get; set; } = true;

        public int FormWidth { get; set; } = 1600;
        public int FormHeight { get; set; } = 950;
        public bool IsMaximized { get; set; } = false;

        public Dictionary<string, int> SplitterPositions { get; set; } = new();

        public List<ChartPaneSettings> Panes { get; set; } = new()
        {
            new ChartPaneSettings { PaneIndex = 0, Symbol = "BTCUSDT", Interval = ChartInterval.Tick, DisplayType = ChartDisplayType.Line, PriceScale = PriceScaleMode.AbsolutePrice },
            new ChartPaneSettings { PaneIndex = 1, Symbol = "BTCUSDT", Interval = ChartInterval.OneMinute, DisplayType = ChartDisplayType.Candlestick, PriceScale = PriceScaleMode.AbsolutePrice },
            new ChartPaneSettings { PaneIndex = 2, Symbol = "BTCUSDT", Interval = ChartInterval.FiveMinutes, DisplayType = ChartDisplayType.Candlestick, PriceScale = PriceScaleMode.AbsolutePrice },
            new ChartPaneSettings { PaneIndex = 3, Symbol = "BTCUSDT", Interval = ChartInterval.FifteenMinutes, DisplayType = ChartDisplayType.Candlestick, PriceScale = PriceScaleMode.AbsolutePrice },
            new ChartPaneSettings { PaneIndex = 4, Symbol = "BTCUSDT", Interval = ChartInterval.OneHour, DisplayType = ChartDisplayType.Candlestick, PriceScale = PriceScaleMode.AbsolutePrice },
            new ChartPaneSettings { PaneIndex = 5, Symbol = "BTCUSDT", Interval = ChartInterval.OneDay, DisplayType = ChartDisplayType.Candlestick, PriceScale = PriceScaleMode.AbsolutePrice },
        };
    }
}
