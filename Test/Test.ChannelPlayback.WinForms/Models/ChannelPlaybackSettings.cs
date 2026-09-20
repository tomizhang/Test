using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 动态包络通道回放系统 UI 界面与参数持久化配置模型
    /// </summary>
    public class ChannelPlaybackSettings
    {
        // 窗口尺寸与分割布局记忆
        public int WindowWidth { get; set; } = 1600;
        public int WindowHeight { get; set; } = 950;
        public int WindowLeft { get; set; } = -1;
        public int WindowTop { get; set; } = -1;
        public bool IsMaximized { get; set; } = false;
        public int SplitterMainDistance { get; set; } = -1;
        public int SplitterLeftDistance { get; set; } = -1;
        public int SplitterBottomDistance { get; set; } = -1;

        // 数据源参数
        public string Coin { get; set; } = "BTCUSDT";
        public string Interval { get; set; } = "1m (1分钟)";
        public DateTime StartDate { get; set; } = new DateTime(2024, 1, 1);
        public DateTime EndDate { get; set; } = new DateTime(2024, 1, 1);

        // 通道核心参数
        public int LeftLength { get; set; } = 100;
        public int RightExtendLength { get; set; } = 100;
        public int CalculationMode { get; set; } = 0; // 0: 三点趋势定向(向上2低1高/向下2高1低), 1: 三点紧凑自适应, 2: 强制2低1高, 3: 强制2高1低, 4: 线性回归, 5: 极小高度
        public int WindowMode { get; set; } = 0;      // 0: 滑动窗口, 1: 全量累计
        public bool AutoScale { get; set; } = true;
        public bool FollowLatest { get; set; } = true;
        public bool ShowTouchMarkers { get; set; } = true;
        public bool ShowLegend { get; set; } = false;
        public int ChartType { get; set; } = 0;       // 0: 蜡烛图 (Candlestick), 1: 折线图 (Line Chart)

        // 相对极值通道保留与突破识别配置
        public bool EnableRetainedChannel { get; set; } = true;
        public int RetainedConfirmBars { get; set; } = 3;
        public int BreakoutRule { get; set; } = 0;    // 0: 收盘价突破 (ClosePrice), 1: 极值价突破 (ExtremePrice)
        public bool EnableSpecialRetainedStyle { get; set; } = true;
        public int SpecialRetainedMinBars { get; set; } = 100;
        public double SpecialRetainedMinAngle { get; set; } = 35.0;
        public float SpecialRetainedLineWidth { get; set; } = 0.8f;
        public string SpecialRetainedColorHex { get; set; } = "#a855f7"; // 紫色

        // V 形态与倒 V 形态识别配置 (价差 ≥ 5%)
        public bool EnableVPattern { get; set; } = true;
        public decimal VPatternMinPriceDiffPct { get; set; } = 5.0m;
        public bool ShowVPatternLines { get; set; } = true;

        // 连续上涨 / 连续下跌动能形态识别配置 (≥5根且≥2.5%)
        public bool EnableConsecutiveTrend { get; set; } = true;
        public int ConsecutiveTrendMinBars { get; set; } = 5;
        public decimal ConsecutiveTrendMinPct { get; set; } = 0.0m;
        public bool ShowConsecutiveChannel { get; set; } = true; // 连续走势绿色 0.8f 平行通道

        // Tick 视图周期配置 (0: 原始逐笔 Tick, 1: 1分钟, 2: 5分钟, 3: 15分钟, 4: 自定义分钟)
        public int TickPeriodMode { get; set; } = 0;
        public int CustomTickMinutes { get; set; } = 3;

        // 连续 5 根 K 线 3 分钟观察期反转做单配置
        public bool EnableReversalOrder { get; set; } = true;
        public int ReversalObservationMinutes { get; set; } = 3;
        public decimal ReversalPLong { get; set; } = 1.0m;
        public decimal ReversalPMedium { get; set; } = 0.35m;
        public decimal ReversalPShort { get; set; } = 0.35m;
        public bool ShowReversalYellowLines { get; set; } = true;

        // 回放控制参数
        public int SpeedIntervalMs { get; set; } = 50;
    }
}
