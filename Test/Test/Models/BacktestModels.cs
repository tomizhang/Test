using Binance.Net.Enums;
using Common;
using Common.Models;
using System;
using Test.Strategy;

namespace Common.Models
{
    /// <summary>
    /// 回测引擎输入请求参数配置
    /// </summary>
    public class BacktestRequest
    {
        /// <summary>
        /// 目标交易对 (如 BTCUSDT, ETHUSDT)
        /// </summary>
        public string Coin { get; set; } = "BTCUSDT";

        /// <summary>
        /// 回测起始日期 (包含)
        /// </summary>
        public DateTime StartDate { get; set; } = new DateTime(2025, 1, 1);

        /// <summary>
        /// 回测结束日期 (包含)
        /// </summary>
        public DateTime EndDate { get; set; } = new DateTime(2025, 7, 31);

        /// <summary>
        /// K 线周期 (默认 1m)
        /// </summary>
        public KlineInterval Interval { get; set; } = KlineInterval.OneMinute;

        /// <summary>
        /// 策略滑动窗口保留 K 线数量 (默认 2000 根)
        /// </summary>
        public int MaxKlinesCapacity { get; set; } = 2000;

        /// <summary>
        /// 历史趋势线库最低保存数量 (默认 1000 条)
        /// </summary>
        public int MinTrendLinesCapacity { get; set; } = 1000;

        /// <summary>
        /// 已删除（被穿透）趋势线列表保存长度 (默认 1000 条)
        /// </summary>
        public int MaxDeletedTrendLinesCapacity { get; set; } = 1000;

        /// <summary>
        /// 波峰波谷左侧对比根数 (默认 5)
        /// </summary>
        public int LeftLen { get; set; } = 5;

        /// <summary>
        /// 波峰波谷右侧对比根数 (默认 5)
        /// </summary>
        public int RightLen { get; set; } = 5;

        /// <summary>
        /// 趋势线两极值点间最大跨度 (默认 100 根)
        /// </summary>
        public int MaxSpan { get; set; } = 100;

        /// <summary>
        /// 是否允许趋势线内部 K 线穿透 (默认 false, 严格外包络)
        /// </summary>
        public bool AllowInternalPenetration { get; set; } = false;

        /// <summary>
        /// Tick 并行读取滑动窗口天数 (默认 3 线程并发)
        /// </summary>
        public int ParallelDays { get; set; } = 3;

        /// <summary>
        /// 回测完成后是否自动生成 ScottPlot 分析图表并落盘 (默认 true)
        /// </summary>
        public bool GenerateChart { get; set; } = true;

        /// <summary>
        /// 自定义图表输出文件路径 (为 null 时自动生成在 Config.GetChartsPath())
        /// </summary>
        public string? ChartOutputPath { get; set; } = null;

        /// <summary>
        /// 图表宽度像素 (默认 1920)
        /// </summary>
        public int ChartWidth { get; set; } = 1920;

        /// <summary>
        /// 图表高度像素 (默认 1080)
        /// </summary>
        public int ChartHeight { get; set; } = 1080;

        /// <summary>
        /// 自定义策略备注/描述
        /// </summary>
        public string? Description { get; set; } = null;
    }

    /// <summary>
    /// 回测进度报告模型 (用于 WinForms / UI 进度条与实时状态显示)
    /// </summary>
    public class BacktestProgress
    {
        public string Message { get; set; } = string.Empty;
        public double Percentage { get; set; }
        public DateTime? CurrentDate { get; set; }
        public int ProcessedKlines { get; set; }
        public int TotalKlines { get; set; }
        public long ProcessedTicks { get; set; }
        public long TotalTicks { get; set; }
        public int ActiveResistanceCount { get; set; }
        public int ActiveSupportCount { get; set; }
        public int DeletedLinesCount { get; set; }

        public override string ToString()
        {
            return $"[{Percentage:F1}%] {Message} (Klines: {ProcessedKlines:N0}, Ticks: {ProcessedTicks:N0})";
        }
    }

    /// <summary>
    /// 回测执行最终产出结果模型
    /// </summary>
    public class BacktestResult
    {
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }

        public string Coin { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public KlineInterval Interval { get; set; }

        public int TotalKlines { get; set; }
        public long TotalTicks { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public double TicksPerSecond => ElapsedMilliseconds > 0 ? (TotalTicks * 1000.0 / ElapsedMilliseconds) : 0;

        public int PeaksCount { get; set; }
        public int ValleysCount { get; set; }
        public int ActiveResistanceLinesCount { get; set; }
        public int ActiveSupportLinesCount { get; set; }
        public int DeletedTrendLinesCount { get; set; }
        public int HistoricalTrendLinesCount { get; set; }

        /// <summary>
        /// 生成并落盘的分析图表绝对路径
        /// </summary>
        public string? ChartPath { get; set; }

        /// <summary>
        /// 策略运行最终统计摘要
        /// </summary>
        public string StrategySummary { get; set; } = string.Empty;

        /// <summary>
        /// 策略实例引用 (供后续 WinForms 交互式查看与二次分析)
        /// </summary>
        public TrendLineStrategy? Strategy { get; set; }

        public override string ToString()
        {
            return $"[BacktestResult - {Coin} {Interval.ToIntervalString()}] " +
                   $"Range: {StartDate:yyyy-MM-dd} ~ {EndDate:yyyy-MM-dd} | " +
                   $"Klines: {TotalKlines:N0}, Ticks: {TotalTicks:N0} | " +
                   $"Time: {ElapsedMilliseconds} ms ({TicksPerSecond:N0} ticks/s) | " +
                   $"Peaks: {PeaksCount}, Valleys: {ValleysCount} | " +
                   $"Active Lines: (R:{ActiveResistanceLinesCount}, S:{ActiveSupportLinesCount}), Deleted: {DeletedTrendLinesCount} | " +
                   $"Chart: {ChartPath ?? "None"}";
        }
    }
}
