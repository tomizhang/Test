using Binance.Net.Enums;
using Common;
using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;
using Test.Strategy;

namespace Common.Models
{
    /// <summary>
    /// 交易开仓方向 (开多 / 开空)
    /// </summary>
    public enum TradeSide
    {
        /// <summary>
        /// 开多 (Buy / Long)
        /// </summary>
        Buy = 1,

        /// <summary>
        /// 开空 (Sell / Short)
        /// </summary>
        Sell = 2
    }

    /// <summary>
    /// 仓位平仓原因
    /// </summary>
    public enum PositionExitReason
    {
        None = 0,

        /// <summary>
        /// 触碰止盈价平仓 (+1.5%)
        /// </summary>
        TakeProfit = 1,

        /// <summary>
        /// 触碰止损价平仓 (-0.5%)
        /// </summary>
        StopLoss = 2,

        /// <summary>
        /// 回测周期结束强制平仓
        /// </summary>
        EndOfBacktest = 3
    }

    /// <summary>
    /// 图表主走势渲染模式 (K线蜡烛图 / 收盘折线图)
    /// </summary>
    public enum ChartType
    {
        /// <summary>
        /// 标准红绿蜡烛图 (Candlestick / OHLC 实体与上下影线)
        /// </summary>
        Candlestick = 0,

        /// <summary>
        /// 收盘价折线图 (Line Chart)
        /// </summary>
        Line = 1
    }

    /// <summary>
    /// 回测交易策略类型模式
    /// </summary>
    public enum TradeStrategyType
    {
        /// <summary>
        /// 经典趋势线触碰回弹策略 (3点线 0.001% 触碰 + 持续 3 分钟反向回弹确认开仓)
        /// </summary>
        TouchRebound = 0,

        /// <summary>
        /// 紫色特殊趋势线穿透策略 (从上穿过跌破开空，从下穿过突破开多，1分钟K线收盘确认)
        /// </summary>
        PurpleBreakout = 1,

        /// <summary>
        /// 宏观 Level 3 假突破猎杀策略 (SFP / 2B 假突破反转：刺破前高/前低 L3 后反向回抽持续 3 分钟确认开仓)
        /// </summary>
        Level3FalseBreakout = 2,

        /// <summary>
        /// 多策略组合模式 (同时启用触碰回弹、紫色穿透与宏观L3假突破猎杀策略)
        /// </summary>
        Combined = 3
    }

    /// <summary>
    /// 趋势线触碰回弹触发的交易开仓信号
    /// </summary>
    public struct TradeSignal
    {
        public long SignalId { get; set; }
        public int GlobalBarIndex { get; set; }
        public long TimestampMs { get; set; }
        public DateTime Time => TimeHelper.FromUnixTimeMilliseconds(TimestampMs);
        public TradeSide Side { get; set; }
        public decimal Price { get; set; }
        public TrendLine TriggerLine { get; set; }
        public int TicksSinceTouch { get; set; }
        public string Reason { get; set; }

        public override string ToString()
        {
            string sideStr = Side == TradeSide.Buy ? "🟢 开多 (Long)" : "🔴 开空 (Short)";
            return $"[{Time:yyyy-MM-dd HH:mm:ss.fff}] {sideStr} @ 价格:{Price:F2} | 触碰#{TriggerLine.X1}->#{TriggerLine.X2} (跨度:{TriggerLine.LineX1X2}, 寿命:{TriggerLine.LineAge}) {TicksSinceTouch}ticks回弹";
        }
    }

    /// <summary>
    /// 单笔已完成平仓交易的完整记录
    /// </summary>
    public class TradeRecord
    {
        public int TradeId { get; set; }
        public TradeSide Side { get; set; }                    // 开仓方向 (Buy / Sell)
        public long EntryTimestampMs { get; set; }            // 开仓时间戳
        public DateTime EntryTime => TimeHelper.FromUnixTimeMilliseconds(EntryTimestampMs);
        public decimal EntryPrice { get; set; }               // 开仓价
        public int EntryGlobalBarIndex { get; set; }          // 开仓 K 线序号

        public decimal TakeProfitPrice { get; set; }          // 目标止盈价
        public decimal StopLossPrice { get; set; }            // 目标止损价

        public long ExitTimestampMs { get; set; }             // 平仓时间戳
        public DateTime ExitTime => TimeHelper.FromUnixTimeMilliseconds(ExitTimestampMs);
        public decimal ExitPrice { get; set; }                // 平仓价
        public int ExitGlobalBarIndex { get; set; }           // 平仓 K 线序号
        public PositionExitReason ExitReason { get; set; }    // 平仓类型 (止盈 / 止损 / 结束)

        public decimal PnLPct { get; set; }                   // 净盈亏百分比 (%)
        public bool IsWin => PnLPct > 0;                      // 是否盈利
        public TimeSpan Duration => ExitTime - EntryTime;     // 持仓时间跨度
        public int HoldingBars => Math.Max(0, ExitGlobalBarIndex - EntryGlobalBarIndex); // 持仓K线根数

        public decimal MaxRunupPct { get; set; }              // 最大潜在浮盈百分比 (MFE)
        public decimal MaxDrawdownPct { get; set; }           // 最大潜在浮亏百分比 (MAE)

        public TrendLine TriggerLine { get; set; }            // 触发开仓的趋势线
        public string StrategyReason { get; set; } = string.Empty; // 策略原因描述

        public override string ToString()
        {
            string icon = ExitReason == PositionExitReason.TakeProfit ? "💰 [止盈]" : (ExitReason == PositionExitReason.StopLoss ? "🛑 [止损]" : "🏁 [完结]");
            string sideStr = Side == TradeSide.Buy ? "多单" : "空单";
            string sign = PnLPct >= 0 ? "+" : "";
            return $"{icon} #{TradeId} {sideStr} @ 开:{EntryPrice:F2} -> 平:{ExitPrice:F2} ({sign}{PnLPct:F2}%, 持仓:{Duration.TotalMinutes:F1}分)";
        }
    }

    /// <summary>
    /// 当前持仓中的仓位模型
    /// </summary>
    public class Position
    {
        public int PositionId { get; set; }
        public TradeSide Side { get; set; }
        public long EntryTimestampMs { get; set; }
        public decimal EntryPrice { get; set; }
        public int EntryGlobalBarIndex { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public decimal StopLossPrice { get; set; }
        public decimal HighestPriceSinceEntry { get; set; }
        public decimal LowestPriceSinceEntry { get; set; }
        public TrendLine TriggerLine { get; set; }
        public string StrategyReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// 回测引擎输入请求参数配置
    /// </summary>
    public class BacktestRequest
    {
        public string Coin { get; set; } = "BTCUSDT";
        public DateTime StartDate { get; set; } = new DateTime(2025, 1, 1);
        public DateTime EndDate { get; set; } = new DateTime(2025, 7, 31);
        public KlineInterval Interval { get; set; } = KlineInterval.OneMinute;

        public int MaxKlinesCapacity { get; set; } = 2000;
        public int MinTrendLinesCapacity { get; set; } = 1000;
        public int MaxDeletedTrendLinesCapacity { get; set; } = 1000;

        /// <summary>
        /// 极值高低点计算算法类型 (默认分形法 Fractal)
        /// </summary>
        public PivotAlgorithmType PivotAlgorithm { get; set; } = PivotAlgorithmType.Fractal;

        /// <summary>
        /// ZigZag 最小反转幅度百分比 (%) (默认 1.0%)
        /// </summary>
        public decimal ZigZagDeviationPct { get; set; } = 1.0m;

        /// <summary>
        /// ZigZag 最小 K 线间隔深度 (默认 5)
        /// </summary>
        public int ZigZagDepth { get; set; } = 5;

        public int LeftLen { get; set; } = 5;
        public int RightLen { get; set; } = 5;
        public int MaxSpan { get; set; } = 100;
        public bool AllowInternalPenetration { get; set; } = false;

        public int MinSignalLineX1X2 { get; set; } = 40;
        public int MinSignalLineAge { get; set; } = 4;
        /// <summary>
        /// 趋势线开仓最小整体百分比斜率 (%)，要求整条趋势线具备一定整体倾斜幅度 (默认 0.50%)
        /// </summary>
        public decimal MinSignalOverallSlopePct { get; set; } = 0.50m;
        public decimal MinSignalSlopePct { get => MinSignalOverallSlopePct; set => MinSignalOverallSlopePct = value; }
        public int SignalCooldownSeconds { get; set; } = 60;

        /// <summary>
        /// 策略止盈比例 (%) (默认 1.5%)
        /// </summary>
        public decimal TakeProfitPct { get; set; } = 1.5m;

        /// <summary>
        /// <summary>
        /// 策略止损比例 (%) (默认 0.5%)
        /// </summary>
        public decimal StopLossPct { get; set; } = 0.5m;

        /// <summary>
        /// 是否开启策略交易 (触碰回弹开仓与止盈止损) (默认 true)
        /// </summary>
        public bool EnableTrading { get; set; } = true;

        /// <summary>
        /// 回测交易策略类型模式 (默认触碰回弹策略)
        /// </summary>
        public TradeStrategyType TradeStrategy { get; set; } = TradeStrategyType.TouchRebound;

        /// <summary>
        /// 是否开启 Tick 级别微止损 (5-Tick 价格点位 / 若为 false 则使用固定 StopLossPct 比例止损) (默认 true)
        /// </summary>
        public bool EnableTickStopLoss { get; set; } = true;

        /// <summary>
        /// 趋势线最大允许斜率 (%/bar)，过滤超高斜率与异常噪音趋势线 (默认 2.0%/bar)
        /// </summary>
        public decimal MaxSlopePctPerBar { get; set; } = 2.0m;

        /// <summary>
        /// 趋势线绘制基础线宽 (px) (默认 0.8)
        /// </summary>
        public decimal LineWidth { get; set; } = 0.8m;

        public int ParallelDays { get; set; } = 3;
        public bool GenerateChart { get; set; } = true;
        public string? ChartOutputPath { get; set; } = null;
        public int ChartWidth { get; set; } = 1920;
        public int ChartHeight { get; set; } = 1080;

        /// <summary>
        /// 是否自动生成专业 HTML 统计报告并落盘 (默认 true)
        /// </summary>
        public bool GenerateHtmlReport { get; set; } = true;

        public string? ReportOutputPath { get; set; } = null;
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
        public int ActiveChannelsCount { get; set; }
        public int DeletedLinesCount { get; set; }
        public int LongSignalsCount { get; set; }
        public int ShortSignalsCount { get; set; }
        public int TotalSignalsCount => LongSignalsCount + ShortSignalsCount;

        public int CompletedTradesCount { get; set; }
        public int WinningTradesCount { get; set; }
        public int LosingTradesCount { get; set; }
        public decimal CurrentTotalPnLPct { get; set; }
        private double _winRate = -1;
        public double WinRate
        {
            get => _winRate >= 0 ? _winRate : (CompletedTradesCount > 0 ? (double)WinningTradesCount / CompletedTradesCount * 100.0 : 0.0);
            set => _winRate = value;
        }

        public override string ToString()
        {
            return $"[{Percentage:F1}%] {Message} (Klines: {ProcessedKlines:N0}, Ticks: {ProcessedTicks:N0}, 交易: {CompletedTradesCount}笔 胜率:{WinRate:F1}% 盈亏:{CurrentTotalPnLPct:F2}%)";
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
        public int ActiveChannelsCount { get; set; }
        public int DeletedTrendLinesCount { get; set; }
        public int HistoricalTrendLinesCount { get; set; }

        public int LongSignalsCount { get; set; }
        public int ShortSignalsCount { get; set; }
        public int TotalSignalsCount => LongSignalsCount + ShortSignalsCount;

        // 交易平仓与收益指标
        public List<TradeRecord> CompletedTrades { get; set; } = new List<TradeRecord>();
        public int TotalTrades => CompletedTrades.Count;
        public int WinningTradesCount { get; set; }
        public int LosingTradesCount { get; set; }
        private double _resWinRate = -1;
        public double WinRate
        {
            get => _resWinRate >= 0 ? _resWinRate : (TotalTrades > 0 ? (double)WinningTradesCount / TotalTrades * 100.0 : 0.0);
            set => _resWinRate = value;
        }
        public decimal TotalPnLPct { get; set; }
        public decimal ProfitFactor { get; set; }
        public decimal MaxDrawdownPct { get; set; }
        public decimal AvgWinPct { get; set; }
        public decimal AvgLossPct { get; set; }
        public int MaxConsecutiveWins { get; set; }
        public int MaxConsecutiveLosses { get; set; }

        public string? ChartPath { get; set; }
        public string? ReportHtmlPath { get; set; }
        public string StrategySummary { get; set; } = string.Empty;
        public TrendLineStrategy? Strategy { get; set; }

        public override string ToString()
        {
            return $"[BacktestResult - {Coin} {Interval.ToIntervalString()}] " +
                   $"Range: {StartDate:yyyy-MM-dd} ~ {EndDate:yyyy-MM-dd} | " +
                   $"交易: {TotalTrades}笔, 胜率: {WinRate:F1}%, 累计收益: {TotalPnLPct:F2}%, 盈亏比: {ProfitFactor:F2}, 最大回撤: {MaxDrawdownPct:F2}% | " +
                   $"Time: {ElapsedMilliseconds} ms ({TicksPerSecond:N0} ticks/s) | " +
                   $"HTML报告: {ReportHtmlPath ?? "None"}";
        }
    }
}
