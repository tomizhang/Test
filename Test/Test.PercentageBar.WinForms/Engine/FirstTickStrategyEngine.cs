using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Test.PercentageBar.WinForms.Helper;
using Test.PercentageBar.WinForms.Models;

namespace Test.PercentageBar.WinForms.Engine
{
    /// <summary>
    /// 紫色趋势线触碰反弹交易明细记录
    /// </summary>
    public class FirstTickTrade
    {
        public int TradeId { get; set; }
        public int EntryBarIndex { get; set; }
        public int ExitBarIndex { get; set; }
        public int HoldingBarsCount => Math.Max(1, ExitBarIndex - EntryBarIndex + 1);
        public string BarRangeDesc => EntryBarIndex == ExitBarIndex ? $"Bar #{EntryBarIndex}" : $"Bar #{EntryBarIndex}~#{ExitBarIndex} ({HoldingBarsCount}根)";
        public TradeSide Side { get; set; } // Buy (Long) / Sell (Short)
        public decimal EntryPrice { get; set; }
        public long EntryTime { get; set; }
        public decimal ExitPrice { get; set; }
        public long ExitTime { get; set; }
        public PositionExitReason ExitReason { get; set; }
        public decimal TargetStopLossPrice { get; set; }
        public decimal TargetTakeProfitPrice { get; set; }
        public decimal TrendLinePrice { get; set; }
        public TrendLineType TriggerLineType { get; set; }
        public int ReboundTicks { get; set; } = 15;
        public string TouchInfo { get; set; } = string.Empty;
        public decimal PositionValue { get; set; }
        public decimal GrossPnL { get; set; }
        public decimal Fee { get; set; }
        public decimal NetPnL { get; set; }
        public decimal ReturnPct { get; set; }
        public decimal AccountEquityAfter { get; set; }

        public DateTime EntryDateTime => TimeHelper.FromUnixTimeMilliseconds(EntryTime).ToLocalTime();
        public DateTime ExitDateTime => TimeHelper.FromUnixTimeMilliseconds(ExitTime).ToLocalTime();
        public TimeSpan HoldingDuration => TimeSpan.FromMilliseconds(Math.Max(0, ExitTime - EntryTime));
        public bool IsWin => NetPnL > 0;
    }

    /// <summary>
    /// 紫色趋势线触碰反弹策略回测综合报告指标
    /// </summary>
    public class FirstTickBacktestReport
    {
        public string Coin { get; set; } = "BTCUSDT";
        public decimal ThresholdValue { get; set; }
        public SliceUnitType SliceUnit { get; set; }
        public decimal InitialCapital { get; set; }
        public decimal FinalCapital { get; set; }
        public decimal TotalNetProfit { get; set; }
        public decimal TotalReturnPct { get; set; }

        // 策略专属参数
        public int ReboundTicks { get; set; } = 15;
        public decimal StopLossPct { get; set; } = 0.01m; // 0.01% (紫色趋势线下方)
        public decimal TakeProfitPct { get; set; } = 1.0m; // 1.0%
        public int TouchEntryMode { get; set; } = 0; // 0: 从第3点起 (排除x1/x2), 1: 仅第3点后 (排除x1/x2/x3)
        public int PurpleTrendLinesCount { get; set; }

        public int TotalTrades { get; set; }
        public int WinTrades { get; set; }
        public int LossTrades { get; set; }
        public decimal WinRatePct => TotalTrades > 0 ? (decimal)WinTrades / TotalTrades * 100m : 0m;

        public int LongTrades { get; set; }
        public int LongWins { get; set; }
        public decimal LongWinRatePct => LongTrades > 0 ? (decimal)LongWins / LongTrades * 100m : 0m;

        public int ShortTrades { get; set; }
        public int ShortWins { get; set; }
        public decimal ShortWinRatePct => ShortTrades > 0 ? (decimal)ShortWins / ShortTrades * 100m : 0m;

        public int StopLossCount => Trades.Count(t => t.ExitReason == PositionExitReason.StopLoss);
        public int TakeProfitCount => Trades.Count(t => t.ExitReason == PositionExitReason.TakeProfit);
        public int EndOfBacktestCount => Trades.Count(t => t.ExitReason == PositionExitReason.EndOfBacktest);
        public int MaxHoldingBars => Trades.Count > 0 ? Trades.Max(t => t.HoldingBarsCount) : 0;

        public decimal TotalProfitGross { get; set; }
        public decimal TotalLossGross { get; set; }
        public decimal TotalFees { get; set; }
        public decimal ProfitFactor => TotalLossGross > 0 ? TotalProfitGross / TotalLossGross : (TotalProfitGross > 0 ? 999m : 0m);

        public decimal MaxDrawdownUsdt { get; set; }
        public decimal MaxDrawdownPct { get; set; }
        public int MaxConsecutiveWins { get; set; }
        public int MaxConsecutiveLosses { get; set; }
        public TimeSpan AverageHoldingDuration { get; set; }
        public decimal ExpectancyUsdt => TotalTrades > 0 ? TotalNetProfit / TotalTrades : 0m;
        public decimal ExpectancyPct => TotalTrades > 0 ? TotalReturnPct / TotalTrades : 0m;

        public List<FirstTickTrade> Trades { get; set; } = new();

        /// <summary>
        /// 生成格式化控制台结构报告卡片文本
        /// </summary>
        public string GenerateTextReport()
        {
            var sb = new StringBuilder();
            string unitDesc = SliceUnit switch
            {
                SliceUnitType.Percentage => $"±{ThresholdValue:F2}%",
                SliceUnitType.FixedPrice => $"±{ThresholdValue:F2} USDT",
                SliceUnitType.MinuteTime => $"{ThresholdValue:F0}m 分钟周期",
                _ => $"{ThresholdValue}"
            };
            string retSign = TotalNetProfit >= 0 ? "+" : "";
            string touchModeDesc = TouchEntryMode == 1 ? "仅第3点后新触碰 (>x3, 严禁x1/x2/x3)" : "从第3点起 (x3+, 严禁x1/x2基准锚点)";

            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ 🏆【🟣 紫色趋势线触碰 15-Tick 反弹量化策略 全量回测报告】");
            sb.AppendLine("╠──────────────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"║ 🏷️ 交易对: {Coin}  |  K线基准: {unitDesc}  |  有效紫色趋势线: {PurpleTrendLinesCount} 根");
            sb.AppendLine($"║ 🎯 核心逻辑: 触碰紫色趋势线 ({touchModeDesc}) -> 跟踪 {ReboundTicks} Tick 反弹入场 | 止损: 趋势线下 {StopLossPct:F3}% | 止盈: {TakeProfitPct:F2}%");
            sb.AppendLine("╠──────────────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"║ 💰 资金规模: 初始本金 = {InitialCapital:N2} USDT  |  最终净值 = {FinalCapital:N2} USDT  |  累计净收益 = {retSign}{TotalNetProfit:N2} USDT ({retSign}{TotalReturnPct:F2}%)");
            sb.AppendLine($"║ 📊 胜率统计: 总交易 = {TotalTrades:N0} 笔  |  盈利 = {WinTrades:N0} 笔  |  亏损 = {LossTrades:N0} 笔  |  胜率 = {WinRatePct:F2}%");
            sb.AppendLine($"║ 📌 平仓构成: 🎯止盈平仓 = {TakeProfitCount:N0}笔 | 🛑趋势线止损 = {StopLossCount:N0}笔 | ⌛期末平仓 = {EndOfBacktestCount:N0}笔 | 最长跨Bar持仓 = {MaxHoldingBars}根Bar");
            sb.AppendLine($"║ ⚖️ 盈亏特征: 利润因子 (Profit Factor) = {ProfitFactor:F2}  |  单笔平均盈亏 = {retSign}{ExpectancyUsdt:N2} USDT  |  单笔期望收益 = {retSign}{ExpectancyPct:F3}%");
            sb.AppendLine($"║ 📉 回撤风控: 历史最大回撤 = -{MaxDrawdownUsdt:N2} USDT (-{MaxDrawdownPct:F2}%)  |  最大连胜 = {MaxConsecutiveWins} 笔  |  最大连亏 = {MaxConsecutiveLosses} 笔");
            sb.AppendLine($"║ ⏱️ 效率指标: 平均持仓时长 = {PercentPlotHelper.FormatTimeSpan(AverageHoldingDuration)}  |  多单: {LongTrades:N0}笔(胜率{LongWinRatePct:F1}%)  |  空单: {ShortTrades:N0}笔(胜率{ShortWinRatePct:F1}%)");
            sb.AppendLine($"║ 💸 交易成本: 累计手续费支出 = {TotalFees:N2} USDT");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════════════════");

            return sb.ToString();
        }

        /// <summary>
        /// 生成专业交互式 HTML 量化回测报告 (包含 KPI 看板、资金曲线图、逐笔盈亏柱状图与可搜索交易流水明细表)
        /// </summary>
        public string GenerateHtmlReport(string? outputFilePath = null)
        {
            if (string.IsNullOrEmpty(outputFilePath))
            {
                string reportsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports");
                if (!Directory.Exists(reportsDir))
                {
                    Directory.CreateDirectory(reportsDir);
                }
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"{Coin}_PurpleTrendLine_Backtest_{timestamp}_report.html";
                outputFilePath = Path.Combine(reportsDir, fileName);
            }

            string unitDesc = SliceUnit == SliceUnitType.Percentage ? $"±{ThresholdValue:F2}%" : $"±{ThresholdValue:F2} USDT";
            string pnlSign = TotalNetProfit >= 0 ? "+" : "";
            string pnlColorClass = TotalNetProfit >= 0 ? "text-green" : "text-red";

            // 构造 Chart.js 资金曲线与逐笔数据 JSON
            var tradeLabels = new List<string>();
            var equityData = new List<string>();
            var pnlData = new List<string>();
            var pnlColors = new List<string>();

            tradeLabels.Add("\"#0\"");
            equityData.Add(InitialCapital.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            pnlData.Add("0");
            pnlColors.Add("\"#64748b\"");

            foreach (var t in Trades)
            {
                tradeLabels.Add($"\"#{t.TradeId}\"");
                equityData.Add(t.AccountEquityAfter.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                pnlData.Add(t.NetPnL.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                pnlColors.Add(t.IsWin ? "\"#10b981\"" : "\"#f43f5e\"");
            }

            string labelsJson = string.Join(",", tradeLabels);
            string equityJson = string.Join(",", equityData);
            string pnlJson = string.Join(",", pnlData);
            string colorsJson = string.Join(",", pnlColors);

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("    <meta charset=\"UTF-8\">");
            sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine($"    <title>🏆 紫色趋势线触碰 15-Tick 反弹量化回测报告 - {Coin} ({unitDesc})</title>");
            sb.AppendLine("    <script src=\"https://cdn.jsdelivr.net/npm/chart.js\"></script>");
            sb.AppendLine("    <style>");
            sb.AppendLine(@"
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; background-color: #0f172a; color: #f8fafc; line-height: 1.5; padding: 24px; }
        .container { max-width: 1440px; margin: 0 auto; }
        
        /* Header */
        .header { display: flex; justify-content: space-between; align-items: center; background: #1e293b; padding: 24px 32px; border-radius: 16px; margin-bottom: 24px; border: 1px solid #334155; box-shadow: 0 4px 12px rgba(0, 0, 0, 0.25); }
        .header-title h1 { font-size: 24px; font-weight: 700; color: #f8fafc; margin-bottom: 6px; display: flex; align-items: center; gap: 10px; }
        .header-title p { color: #94a3b8; font-size: 13.5px; }
        .badges { display: flex; gap: 8px; margin-top: 8px; flex-wrap: wrap; }
        .badge { display: inline-block; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600; text-transform: uppercase; }
        .badge-coin { background: #3b82f6; color: #ffffff; }
        .badge-purple { background: #9333ea; color: #ffffff; }
        .badge-ratio { background: #10b981; color: #ffffff; }
        .badge-time { background: #0284c7; color: #ffffff; }

        /* KPI Cards Grid */
        .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 16px; margin-bottom: 24px; }
        .kpi-card { background: #1e293b; padding: 20px; border-radius: 14px; border: 1px solid #334155; border-left: 4px solid #c084fc; transition: transform 0.2s, box-shadow 0.2s; }
        .kpi-card:hover { transform: translateY(-2px); box-shadow: 0 6px 16px rgba(0,0,0,0.3); }
        .kpi-label { font-size: 13px; color: #94a3b8; margin-bottom: 6px; font-weight: 500; }
        .kpi-value { font-size: 23px; font-weight: 700; }
        .kpi-sub { font-size: 12px; color: #64748b; margin-top: 4px; }

        /* Strategy Info Box */
        .strategy-info { background: #18182f; padding: 18px 20px; border-radius: 12px; border: 1px solid #6b21a8; margin-bottom: 24px; font-size: 13.5px; line-height: 1.7; color: #e9d5ff; }
        .strategy-info strong { color: #f0abfc; }

        /* Charts Grid */
        .charts-grid { display: grid; grid-template-columns: 2fr 1fr; gap: 20px; margin-bottom: 24px; }
        @media (max-width: 1024px) { .charts-grid { grid-template-columns: 1fr; } }
        .chart-card { background: #1e293b; padding: 20px; border-radius: 14px; border: 1px solid #334155; }
        .chart-card h3 { font-size: 16px; color: #f1f5f9; margin-bottom: 16px; display: flex; align-items: center; gap: 8px; }
        .chart-wrapper { position: relative; height: 320px; width: 100%; }

        /* Trades Table Card */
        .table-card { background: #1e293b; padding: 24px; border-radius: 14px; border: 1px solid #334155; }
        .table-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; flex-wrap: wrap; gap: 12px; }
        .table-header h3 { font-size: 17px; color: #f1f5f9; }
        .table-filters { display: flex; gap: 10px; align-items: center; }
        .search-box { background: #0f172a; border: 1px solid #475569; border-radius: 8px; padding: 7px 14px; color: #f8fafc; font-size: 13px; outline: none; width: 220px; }
        .search-box:focus { border-color: #c084fc; }
        .filter-btn { background: #334155; border: none; color: #cbd5e1; padding: 7px 14px; border-radius: 8px; font-size: 12px; cursor: pointer; transition: background 0.2s; }
        .filter-btn.active, .filter-btn:hover { background: #9333ea; color: #ffffff; }

        /* Table Style */
        .table-responsive { overflow-x: auto; max-height: 520px; border-radius: 8px; border: 1px solid #334155; }
        table { width: 100%; border-collapse: collapse; font-size: 13px; text-align: left; }
        thead { position: sticky; top: 0; background: #0f172a; z-index: 2; }
        th { padding: 12px 14px; color: #94a3b8; font-weight: 600; border-bottom: 1px solid #334155; white-space: nowrap; }
        td { padding: 11px 14px; border-bottom: 1px solid #1e293b; white-space: nowrap; }
        tbody tr { background: #1e293b; transition: background 0.15s; }
        tbody tr:hover { background: #273549; }

        /* Colors & Tags */
        .text-green { color: #34d399 !important; }
        .text-red { color: #f87171 !important; }
        .text-blue { color: #38bdf8 !important; }
        .text-purple { color: #c084fc !important; }
        .tag { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 11.5px; font-weight: 600; }
        .tag-buy { background: rgba(52, 211, 153, 0.15); color: #34d399; border: 1px solid rgba(52, 211, 153, 0.4); }
        .tag-sell { background: rgba(248, 113, 113, 0.15); color: #f87171; border: 1px solid rgba(248, 113, 113, 0.4); }
        .tag-tp { background: rgba(52, 211, 153, 0.2); color: #34d399; border: 1px solid #10b981; }
        .tag-sl { background: rgba(248, 113, 113, 0.2); color: #f87171; border: 1px solid #ef4444; }
        .tag-end { background: rgba(148, 163, 184, 0.2); color: #94a3b8; border: 1px solid #64748b; }

        /* Footer */
        .footer { text-align: center; margin-top: 32px; color: #64748b; font-size: 12.5px; }
    ");
            sb.AppendLine("    </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"container\">");

            // Header
            sb.AppendLine("    <div class=\"header\">");
            sb.AppendLine("        <div class=\"header-title\">");
            sb.AppendLine($"            <h1>🏆 {Coin} 紫色趋势线触碰 15-Tick 反弹策略回测报告</h1>");
            sb.AppendLine($"            <p>基于高频逐笔 Tick 级微观反弹验证与精确趋势线止损止盈执行</p>");
            sb.AppendLine("            <div class=\"badges\">");
            sb.AppendLine($"                <span class=\"badge badge-coin\">{Coin}</span>");
            sb.AppendLine($"                <span class=\"badge badge-purple\">🟣 活跃紫色趋势线: {PurpleTrendLinesCount} 根</span>");
            sb.AppendLine($"                <span class=\"badge badge-unit\">{unitDesc}</span>");
            sb.AppendLine($"                <span class=\"badge badge-ratio\">15-Tick 反弹 | 止损 -{StopLossPct:F3}% | 止盈 +{TakeProfitPct:F2}%</span>");
            sb.AppendLine($"                <span class=\"badge badge-time\">{DateTime.Now:yyyy-MM-dd HH:mm:ss}</span>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Strategy Description Box
            sb.AppendLine("    <div class=\"strategy-info\">");
            sb.AppendLine($"        <strong>🎯 策略执行核心逻辑：</strong><br/>");
            string touchModeHtml = TouchEntryMode == 1 ? "仅在第 3 点确认后的新触碰 (>x3)" : "从第 3 点确认及之后 (x3+)";
            sb.AppendLine($"        • <strong>触发锚定：</strong>当价格触碰识别出的【🟣 3点及以上共线紫色趋势线】时（<strong>已严格排除基准锚点 x1 和 x2</strong>，当前模式：{touchModeHtml}），启动微观 Tick 级触碰反弹探针；<br/>");
            sb.AppendLine($"        • <strong>反弹入场：</strong>连续监控后续 <strong>{ReboundTicks} 个 Tick</strong>，若期间价格未击穿止损线且确认形成回弹走势，在第 15 个 Tick 处精确开仓；<br/>");
            sb.AppendLine($"        • <strong>硬性止损：</strong>止损位锚定在<strong>紫色趋势线下方 {StopLossPct:F3}%</strong>（严格控制单笔微破位亏损）；<br/>");
            sb.AppendLine($"        • <strong>目标止盈：</strong>止盈位暂定为<strong>入场价格上方 {TakeProfitPct:F2}%</strong>，形成高盈亏比防守型反弹交易。");
            sb.AppendLine("    </div>");

            // KPI Grid
            sb.AppendLine("    <div class=\"kpi-grid\">");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">累计净收益</div>");
            sb.AppendLine($"            <div class=\"kpi-value {pnlColorClass}\">{pnlSign}{TotalNetProfit:N2} <span style=\"font-size:14px;\">USDT</span></div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">收益率: {pnlSign}{TotalReturnPct:F2}%</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">综合交易胜率</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-purple\">{WinRatePct:F1}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">{WinTrades} 胜 / {LossTrades} 负 (共 {TotalTrades} 笔)</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">利润因子 (Profit Factor)</div>");
            sb.AppendLine($"            <div class=\"kpi-value {(ProfitFactor >= 1.5m ? "text-green" : "text-blue")}\">{ProfitFactor:F2}</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">毛利: {TotalProfitGross:N1} / 毛亏: {TotalLossGross:N1}</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">历史最大回撤</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-red\">-{MaxDrawdownPct:F2}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">-{MaxDrawdownUsdt:N2} USDT</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">止盈 / 止损结构</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-green\">{TakeProfitCount} / {StopLossCount}</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">期末结算: {EndOfBacktestCount} 笔</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">多空胜率分布</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-blue\">{LongWinRatePct:F1}% / {ShortWinRatePct:F1}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">多: {LongTrades}笔({LongWins}胜) | 空: {ShortTrades}笔({ShortWins}胜)</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">期望每笔盈亏</div>");
            sb.AppendLine($"            <div class=\"kpi-value {pnlColorClass}\">{pnlSign}{ExpectancyUsdt:N2} U</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">期望收益: {pnlSign}{ExpectancyPct:F3}%</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"kpi-card\">");
            sb.AppendLine("            <div class=\"kpi-label\">持仓效率与成本</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-blue\">{PercentPlotHelper.FormatTimeSpan(AverageHoldingDuration)}</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">手续费: {TotalFees:N2} U | 最长: {MaxHoldingBars}根</div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Charts Grid
            sb.AppendLine("    <div class=\"charts-grid\">");
            sb.AppendLine("        <div class=\"chart-card\">");
            sb.AppendLine("            <h3>📈 账户资金净值曲线 (Equity Curve)</h3>");
            sb.AppendLine("            <div class=\"chart-wrapper\"><canvas id=\"equityChart\"></canvas></div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"chart-card\">");
            sb.AppendLine("            <h3>📊 逐笔交易净盈亏 (Net PnL Distribution)</h3>");
            sb.AppendLine("            <div class=\"chart-wrapper\"><canvas id=\"pnlChart\"></canvas></div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Trades Table Card
            sb.AppendLine("    <div class=\"table-card\">");
            sb.AppendLine("        <div class=\"table-header\">");
            sb.AppendLine($"            <h3>📋 逐笔交易明细流水表 (共 {Trades.Count} 笔)</h3>");
            sb.AppendLine("            <div class=\"table-filters\">");
            sb.AppendLine("                <input type=\"text\" id=\"searchBox\" class=\"search-box\" placeholder=\"🔍 搜索 Bar# / 时间 / 原因...\" onkeyup=\"filterTrades()\">");
            sb.AppendLine("                <button class=\"filter-btn active\" onclick=\"setFilter('all', this)\">全部</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('buy', this)\">多单</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('sell', this)\">空单</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('tp', this)\">🎯止盈</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('sl', this)\">🛑止损</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('win', this)\">仅盈利</button>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"table-responsive\">");
            sb.AppendLine("            <table id=\"tradesTable\">");
            sb.AppendLine("                <thead>");
            sb.AppendLine("                    <tr>");
            sb.AppendLine("                        <th>#</th>");
            sb.AppendLine("                        <th>持仓K线</th>");
            sb.AppendLine("                        <th>触碰来源</th>");
            sb.AppendLine("                        <th>方向</th>");
            sb.AppendLine("                        <th>开仓时间</th>");
            sb.AppendLine("                        <th>开仓价</th>");
            sb.AppendLine("                        <th>平仓时间</th>");
            sb.AppendLine("                        <th>平仓价</th>");
            sb.AppendLine("                        <th>平仓原因</th>");
            sb.AppendLine("                        <th>毛盈亏(U)</th>");
            sb.AppendLine("                        <th>手续费(U)</th>");
            sb.AppendLine("                        <th>净盈亏(U)</th>");
            sb.AppendLine("                        <th>收益率</th>");
            sb.AppendLine("                        <th>账户净值(U)</th>");
            sb.AppendLine("                        <th>持仓时长</th>");
            sb.AppendLine("                    </tr>");
            sb.AppendLine("                </thead>");
            sb.AppendLine("                <tbody>");

            foreach (var t in Trades)
            {
                bool isLong = t.Side == TradeSide.Buy;
                string sideTag = isLong ? "<span class=\"tag tag-buy\">🟢做多</span>" : "<span class=\"tag tag-sell\">🔴做空</span>";
                string reasonTag;
                if (t.ExitReason == PositionExitReason.TakeProfit)
                {
                    reasonTag = "<span class=\"tag tag-tp\">🎯 1%止盈</span>";
                }
                else if (t.ExitReason == PositionExitReason.StopLoss)
                {
                    reasonTag = $"<span class=\"tag tag-sl\">🛑 趋势线下0.01%止损</span>";
                }
                else
                {
                    reasonTag = "<span class=\"tag tag-end\">⌛ 期末平仓</span>";
                }

                string pnlClass = t.NetPnL >= 0 ? "text-green" : "text-red";
                string sideAttr = isLong ? "buy" : "sell";
                string reasonAttr = t.ExitReason switch
                {
                    PositionExitReason.TakeProfit => "tp",
                    PositionExitReason.StopLoss => "sl",
                    _ => "end"
                };
                string winAttr = t.IsWin ? "win" : "loss";

                sb.AppendLine($"                    <tr data-side=\"{sideAttr}\" data-reason=\"{reasonAttr}\" data-win=\"{winAttr}\">");
                sb.AppendLine($"                        <td>{t.TradeId}</td>");
                sb.AppendLine($"                        <td>{t.BarRangeDesc}</td>");
                sb.AppendLine($"                        <td><span class=\"tag\" style=\"background:rgba(192,132,252,0.15);color:#c084fc;border:1px solid rgba(192,132,252,0.3);\">{t.TouchInfo}</span></td>");
                sb.AppendLine($"                        <td>{sideTag}</td>");
                sb.AppendLine($"                        <td>{t.EntryDateTime:yyyy-MM-dd HH:mm:ss.fff}</td>");
                sb.AppendLine($"                        <td>{t.EntryPrice:F2}</td>");
                sb.AppendLine($"                        <td>{t.ExitDateTime:yyyy-MM-dd HH:mm:ss.fff}</td>");
                sb.AppendLine($"                        <td>{t.ExitPrice:F2}</td>");
                sb.AppendLine($"                        <td>{reasonTag}</td>");
                sb.AppendLine($"                        <td class=\"{pnlClass}\">{t.GrossPnL:+0.00;-0.00;0.00}</td>");
                sb.AppendLine($"                        <td>{t.Fee:F3}</td>");
                sb.AppendLine($"                        <td class=\"{pnlClass}\" style=\"font-weight:700;\">{t.NetPnL:+0.00;-0.00;0.00}</td>");
                sb.AppendLine($"                        <td class=\"{pnlClass}\">{t.ReturnPct:+0.00;-0.00;0.00}%</td>");
                sb.AppendLine($"                        <td style=\"font-weight:600;\">{t.AccountEquityAfter:N2}</td>");
                sb.AppendLine($"                        <td>{PercentPlotHelper.FormatTimeSpan(t.HoldingDuration)}</td>");
                sb.AppendLine("                    </tr>");
            }

            sb.AppendLine("                </tbody>");
            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Footer
            sb.AppendLine("    <div class=\"footer\">");
            sb.AppendLine($"        <p>🏆 量化高频百分比 K 线回测系统 • 紫色趋势线触碰 15-Tick 反弹策略 • 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
            sb.AppendLine("    </div>");

            // JavaScript for Charts & Search/Filter
            sb.AppendLine("</div>");
            sb.AppendLine("<script>");
            sb.AppendLine($@"
        // 1. 资金净值走势折线图
        const ctxEquity = document.getElementById('equityChart').getContext('2d');
        const equityGradient = ctxEquity.createLinearGradient(0, 0, 0, 360);
        equityGradient.addColorStop(0, 'rgba(192, 132, 252, 0.35)');
        equityGradient.addColorStop(1, 'rgba(192, 132, 252, 0.00)');

        new Chart(ctxEquity, {{
            type: 'line',
            data: {{
                labels: [{labelsJson}],
                datasets: [{{
                    label: '账户净值 (USDT)',
                    data: [{equityJson}],
                    borderColor: '#c084fc',
                    borderWidth: 2,
                    backgroundColor: equityGradient,
                    fill: true,
                    tension: 0.15,
                    pointRadius: {(Trades.Count > 150 ? 0 : 2)},
                    pointHoverRadius: 5
                }}]
            }},
            options: {{
                responsive: true,
                maintainAspectRatio: false,
                interaction: {{ intersect: false, mode: 'index' }},
                scales: {{
                    x: {{ grid: {{ color: '#334155' }}, ticks: {{ color: '#94a3b8', maxTicksLimit: 20 }} }},
                    y: {{ grid: {{ color: '#334155' }}, ticks: {{ color: '#94a3b8' }} }}
                }},
                plugins: {{
                    legend: {{ labels: {{ color: '#f8fafc', font: {{ size: 13 }} }} }},
                    tooltip: {{
                        backgroundColor: '#1e293b',
                        titleColor: '#c084fc',
                        bodyColor: '#f8fafc',
                        borderColor: '#475569',
                        borderWidth: 1
                    }}
                }}
            }}
        }});

        // 2. 逐笔盈亏柱状图
        const ctxPnl = document.getElementById('pnlChart').getContext('2d');
        new Chart(ctxPnl, {{
            type: 'bar',
            data: {{
                labels: [{labelsJson}],
                datasets: [{{
                    label: '单笔净盈亏 (USDT)',
                    data: [{pnlJson}],
                    backgroundColor: [{colorsJson}],
                    borderRadius: 2
                }}]
            }},
            options: {{
                responsive: true,
                maintainAspectRatio: false,
                scales: {{
                    x: {{ grid: {{ display: false }}, ticks: {{ color: '#94a3b8', maxTicksLimit: 20 }} }},
                    y: {{ grid: {{ color: '#334155' }}, ticks: {{ color: '#94a3b8' }} }}
                }},
                plugins: {{
                    legend: {{ labels: {{ color: '#f8fafc' }} }},
                    tooltip: {{
                        backgroundColor: '#1e293b',
                        titleColor: '#c084fc',
                        bodyColor: '#f8fafc',
                        borderColor: '#475569',
                        borderWidth: 1
                    }}
                }}
            }}
        }});

        // 3. 交易表格筛选与实时搜索
        let currentFilter = 'all';

        function setFilter(type, btn) {{
            currentFilter = type;
            document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            filterTrades();
        }}

        function filterTrades() {{
            const search = document.getElementById('searchBox').value.toLowerCase();
            const rows = document.querySelectorAll('#tradesTable tbody tr');

            rows.forEach(row => {{
                const side = row.getAttribute('data-side');
                const reason = row.getAttribute('data-reason');
                const win = row.getAttribute('data-win');
                const text = row.innerText.toLowerCase();

                let matchFilter = false;
                if (currentFilter === 'all') matchFilter = true;
                else if (currentFilter === 'buy' && side === 'buy') matchFilter = true;
                else if (currentFilter === 'sell' && side === 'sell') matchFilter = true;
                else if (currentFilter === 'win' && win === 'win') matchFilter = true;
                else if (currentFilter === 'tp' && reason === 'tp') matchFilter = true;
                else if (currentFilter === 'sl' && reason === 'sl') matchFilter = true;

                const matchSearch = text.includes(search);

                if (matchFilter && matchSearch) {{
                    row.style.display = '';
                }} else {{
                    row.style.display = 'none';
                }}
            }});
        }}
    ");
            sb.AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(outputFilePath, sb.ToString(), Encoding.UTF8);
            return outputFilePath;
        }
    }

    /// <summary>
    /// 内部活动持仓跟踪状态
    /// </summary>
    internal class ActivePosition
    {
        public int EntryBarIndex { get; set; }
        public TradeSide Side { get; set; }
        public decimal EntryPrice { get; set; }
        public long EntryTime { get; set; }
        public decimal TargetStopLossPrice { get; set; }
        public decimal TargetTakeProfitPrice { get; set; }
        public decimal TrendLinePrice { get; set; }
        public TrendLineType TriggerLineType { get; set; }
        public decimal PositionValue { get; set; }
        public string TouchDesc { get; set; } = string.Empty;
    }

    /// <summary>
    /// 内部展平单笔 Tick 视图结构体
    /// </summary>
    internal readonly struct StrategyTick
    {
        public int GlobalIndex { get; init; }
        public int BarIndex { get; init; }
        public long Time { get; init; }
        public decimal Price { get; init; }
        public decimal Qty { get; init; }
        public decimal QuoteQty { get; init; }
        public bool IsBuyerMaker { get; init; }
    }

    /// <summary>
    /// 紫色趋势线触碰 15-Tick 反弹量化策略回测引擎
    /// </summary>
    public static class FirstTickStrategyEngine
    {
        /// <summary>
        /// 执行紫色趋势线触碰 15-Tick 反弹量化策略回测
        /// </summary>
        public static FirstTickBacktestReport RunBacktest(
            IReadOnlyList<PercentageKline> bars,
            string coin,
            decimal thresholdValue,
            SliceUnitType sliceUnit = SliceUnitType.Percentage,
            decimal initialCapital = 10000m,
            decimal positionSizePct = 100m,
            decimal feeRate = 0.0004m,
            bool compoundInterest = false,
            int reboundTicks = 15,
            decimal stopLossPct = 0.01m,
            decimal takeProfitPct = 1.0m,
            int pivotWindow = 3,
            decimal touchTolerancePct = 0.0003m,
            int tradeSideMode = 0, // 0: 仅做多(支撑反弹), 1: 仅做空(阻力回落), 2: 多空双向
            int touchEntryMode = 0) // 0: 从第3点起 (x3+, 排除x1/x2), 1: 仅第3点后 (>x3, 排除x1/x2/x3)
        {
            var report = new FirstTickBacktestReport
            {
                Coin = coin,
                ThresholdValue = thresholdValue,
                SliceUnit = sliceUnit,
                InitialCapital = initialCapital,
                FinalCapital = initialCapital,
                ReboundTicks = reboundTicks,
                StopLossPct = stopLossPct,
                TakeProfitPct = takeProfitPct,
                TouchEntryMode = touchEntryMode
            };

            if (bars == null || bars.Count < 3) return report;

            // 1. 自动计算高低极值点与趋势线
            var pivotResult = PivotDetector.CalculatePivots(bars, window: Math.Max(2, pivotWindow), alternateHighLow: true);
            var trendlines = PivotDetector.CalculateTrendLines(
                bars,
                pivotResult,
                maxLines: 1000,
                maxSpanBars: 1000,
                extensionBars: 8,
                strictWickPenetration: true,
                touchTolerancePct: Math.Max(0.00005m, touchTolerancePct));

            // 2. 筛选 3 点及以上共线的【🟣 紫色强趋势线】
            var purpleLines = trendlines.Where(tl => tl.IsThreePointConfirmed).ToList();
            report.PurpleTrendLinesCount = purpleLines.Count;
            if (purpleLines.Count == 0) return report;

            // 3. 将所有 Bar 内部逐笔 Ticks 展平为时间单调递增的连续流
            var allTicks = new List<StrategyTick>(bars.Count * 200);
            int globalTickCounter = 0;
            // 记录每个 Bar 在 allTicks 中的起始和结束 Tick 索引
            var barTickRanges = new Dictionary<int, (int Start, int End)>();

            for (int b = 0; b < bars.Count; b++)
            {
                var bar = bars[b];
                if (bar.Ticks == null || bar.Ticks.Length == 0) continue;

                int startTick = globalTickCounter;
                for (int t = 0; t < bar.Ticks.Length; t++)
                {
                    var raw = bar.Ticks[t];
                    if (raw.Price <= 0) continue;

                    allTicks.Add(new StrategyTick
                    {
                        GlobalIndex = globalTickCounter++,
                        BarIndex = bar.BarIndex,
                        Time = raw.Time,
                        Price = raw.Price,
                        Qty = raw.Qty,
                        QuoteQty = raw.QuoteQty,
                        IsBuyerMaker = raw.IsBuyerMaker
                    });
                }
                int endTick = globalTickCounter;
                if (endTick > startTick)
                {
                    barTickRanges[bar.BarIndex] = (startTick, endTick);
                }
            }

            if (allTicks.Count == 0) return report;

            // 4. 精确识别各紫色趋势线在逐笔 Tick 流中的触碰/锚定事件
            // 为每根紫色趋势线预先查找其各 TouchBar 内部距离趋势线最近的极值 Tick (Anchor Tick)
            // 触碰点集合: (TickIndex, Line, LinePriceAtTouch, TouchDesc)
            var touchEvents = new List<(int TickIndex, TrendLine Line, decimal LinePrice, string TouchDesc)>();

            decimal tol = Math.Max(0.00005m, touchTolerancePct);
            decimal slRatio = stopLossPct / 100.0m;

            foreach (var tl in purpleLines)
            {
                bool isSupport = tl.Type == TrendLineType.Support;
                if (tradeSideMode == 0 && !isSupport) continue; // 仅做多，过滤阻力线
                if (tradeSideMode == 1 && isSupport) continue;  // 仅做空，过滤支撑线

                // 4.1 提取已知锚点 Bar 列表 (TouchBarIndices)
                // ⚠️ 关键规则修正：紫色趋势线的起始基准锚点 x1 (touchList[0]) 和 x2 (touchList[1]) 绝对不能作为开仓触碰条件！
                var touchList = tl.TouchBarIndices ?? new[] { tl.StartBarIndex, tl.EndBarIndex };
                var processedBars = new HashSet<int>();

                // 将 x1 和 x2 标记为已处理（防后续射线扫描重复扫到），但绝不生成触碰开仓事件！
                if (touchList.Length >= 1) processedBars.Add(touchList[0]);
                if (touchList.Length >= 2) processedBars.Add(touchList[1]);

                // touchEntryMode == 0: 从第3点 (x3, 索引 2) 开始计入开仓触碰 (推荐，排除 x1, x2)
                // touchEntryMode == 1: 仅在第3点确认之后 (>x3, 索引 3) 开始计入开仓触碰 (排除 x1, x2, x3)
                int startTouchIdx = touchEntryMode == 1 ? 3 : 2;

                for (int i = startTouchIdx; i < touchList.Length; i++)
                {
                    int bIdx = touchList[i];
                    if (bIdx < 0 || bIdx >= bars.Count) continue;
                    processedBars.Add(bIdx);

                    if (!barTickRanges.TryGetValue(bIdx, out var range)) continue;

                    decimal linePrice = tl.StartPrice + tl.Slope * (bIdx - tl.StartBarIndex);
                    if (linePrice <= 0) continue;

                    // 找出该 Bar 中最贴近趋势线价格的 Tick
                    int bestTickIdx = -1;
                    decimal minDiff = decimal.MaxValue;

                    for (int ti = range.Start; ti < range.End; ti++)
                    {
                        decimal diff = Math.Abs(allTicks[ti].Price - linePrice);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            bestTickIdx = ti;
                        }
                    }

                    if (bestTickIdx >= 0)
                    {
                        string desc = i == 2 ? $"第3点确认触碰 (Bar #{bIdx})" : $"第{i + 1}点共线触碰 (Bar #{bIdx})";
                        touchEvents.Add((bestTickIdx, tl, linePrice, desc));
                    }
                }

                // 4.2 扫描趋势线有效延伸期内 (从第3点之后至穿透前) 是否有其他 Bar 价格触碰达到趋势线
                int startScanBar = touchList.Length >= 3 ? touchList[2] + 1 : tl.StartBarIndex + 1;
                int endScanBar = tl.IsBroken ? tl.BreakBarIndex : Math.Min(tl.ExtendedBarIndex, bars.Count - 1);

                for (int bIdx = startScanBar; bIdx <= endScanBar; bIdx++)
                {
                    if (processedBars.Contains(bIdx)) continue;
                    if (!barTickRanges.TryGetValue(bIdx, out var range)) continue;

                    decimal linePrice = tl.StartPrice + tl.Slope * (bIdx - tl.StartBarIndex);
                    if (linePrice <= 0) continue;

                    // 检查该 Bar 的 High/Low 是否进入触碰容差范围
                    var bar = bars[bIdx];
                    bool reachesLine;
                    if (isSupport)
                    {
                        // 支撑线：价格回落触碰 linePrice
                        reachesLine = bar.Low <= linePrice * (1m + tol) && bar.High >= linePrice * (1m - slRatio);
                    }
                    else
                    {
                        // 阻力线：价格冲高触碰 linePrice
                        reachesLine = bar.High >= linePrice * (1m - tol) && bar.Low <= linePrice * (1m + slRatio);
                    }

                    if (reachesLine)
                    {
                        // 寻找首次进入触碰区间的 Tick
                        for (int ti = range.Start; ti < range.End; ti++)
                        {
                            var tick = allTicks[ti];
                            bool hit = isSupport
                                ? (tick.Price <= linePrice * (1m + tol) && tick.Price >= linePrice * (1m - slRatio))
                                : (tick.Price >= linePrice * (1m - tol) && tick.Price <= linePrice * (1m + slRatio));

                            if (hit)
                            {
                                string desc = $"延伸线触碰 (Bar #{bIdx})";
                                touchEvents.Add((ti, tl, linePrice, desc));
                                break; // 该 Bar 内只记录首次触碰
                            }
                        }
                    }
                }
            }

            // 按时间先后对触碰事件排序
            touchEvents.Sort((a, b) => a.TickIndex.CompareTo(b.TickIndex));

            // 去除过密触碰（防单次震荡重复频繁触发，同一趋势线触碰间隔至少 30 个 Tick）
            var filteredTouchEvents = new List<(int TickIndex, TrendLine Line, decimal LinePrice, string TouchDesc)>();
            int lastTouchTickIdx = -1;
            foreach (var evt in touchEvents)
            {
                if (lastTouchTickIdx < 0 || evt.TickIndex - lastTouchTickIdx >= 30)
                {
                    filteredTouchEvents.Add(evt);
                    lastTouchTickIdx = evt.TickIndex;
                }
            }

            // 5. 逐笔 Tick 流式回测模拟
            decimal currentCapital = initialCapital;
            decimal peakCapital = initialCapital;
            decimal maxDrawdownUsdt = 0m;
            decimal maxDrawdownPct = 0m;

            int consecutiveWins = 0, maxConsecutiveWins = 0;
            int consecutiveLosses = 0, maxConsecutiveLosses = 0;
            long totalHoldingMs = 0;
            int tradeId = 1;

            ActivePosition? activePos = null;

            void ClosePosition(ActivePosition pos, decimal exitPrice, long exitTime, int exitBarIndex, PositionExitReason exitReason)
            {
                decimal returnPct = pos.Side == TradeSide.Buy
                    ? (exitPrice - pos.EntryPrice) / pos.EntryPrice
                    : (pos.EntryPrice - exitPrice) / pos.EntryPrice;

                decimal grossPnL = pos.PositionValue * returnPct;
                decimal entryFee = pos.PositionValue * feeRate;
                decimal exitFee = (pos.PositionValue * (1m + returnPct)) * feeRate;
                decimal totalFee = entryFee + exitFee;
                decimal netPnL = grossPnL - totalFee;

                currentCapital += netPnL;

                if (currentCapital > peakCapital)
                {
                    peakCapital = currentCapital;
                }

                decimal ddUsdt = peakCapital - currentCapital;
                decimal ddPct = peakCapital > 0 ? (ddUsdt / peakCapital) * 100.0m : 0m;
                if (ddUsdt > maxDrawdownUsdt) maxDrawdownUsdt = ddUsdt;
                if (ddPct > maxDrawdownPct) maxDrawdownPct = ddPct;

                bool isWin = netPnL > 0;
                if (isWin)
                {
                    consecutiveWins++;
                    consecutiveLosses = 0;
                    if (consecutiveWins > maxConsecutiveWins) maxConsecutiveWins = consecutiveWins;
                }
                else
                {
                    consecutiveLosses++;
                    consecutiveWins = 0;
                    if (consecutiveLosses > maxConsecutiveLosses) maxConsecutiveLosses = consecutiveLosses;
                }

                long durationMs = Math.Max(0, exitTime - pos.EntryTime);
                totalHoldingMs += durationMs;

                var tradeRecord = new FirstTickTrade
                {
                    TradeId = tradeId++,
                    EntryBarIndex = pos.EntryBarIndex,
                    ExitBarIndex = exitBarIndex,
                    Side = pos.Side,
                    EntryPrice = pos.EntryPrice,
                    EntryTime = pos.EntryTime,
                    ExitPrice = exitPrice,
                    ExitTime = exitTime,
                    ExitReason = exitReason,
                    TargetStopLossPrice = pos.TargetStopLossPrice,
                    TargetTakeProfitPrice = pos.TargetTakeProfitPrice,
                    TrendLinePrice = pos.TrendLinePrice,
                    TriggerLineType = pos.TriggerLineType,
                    ReboundTicks = reboundTicks,
                    TouchInfo = pos.TouchDesc,
                    PositionValue = pos.PositionValue,
                    GrossPnL = grossPnL,
                    Fee = totalFee,
                    NetPnL = netPnL,
                    ReturnPct = returnPct * 100.0m,
                    AccountEquityAfter = currentCapital
                };

                report.Trades.Add(tradeRecord);
                report.TotalTrades++;
                if (isWin)
                {
                    report.WinTrades++;
                    report.TotalProfitGross += grossPnL;
                }
                else
                {
                    report.LossTrades++;
                    report.TotalLossGross += Math.Abs(grossPnL);
                }

                report.TotalFees += totalFee;

                if (pos.Side == TradeSide.Buy)
                {
                    report.LongTrades++;
                    if (isWin) report.LongWins++;
                }
                else
                {
                    report.ShortTrades++;
                    if (isWin) report.ShortWins++;
                }
            }

            // 维护候选开仓观察探针集合
            var activeProbes = new List<(int StartTickIdx, TrendLine Line, decimal LinePrice, decimal MinPrice, decimal MaxPrice, bool IsSupport, decimal StopLossPrice, string TouchDesc)>();
            int nextEventIdx = 0;

            int totalTicks = allTicks.Count;
            for (int t = 0; t < totalTicks; t++)
            {
                var curTick = allTicks[t];
                decimal curPrice = curTick.Price;

                // 1. 若有持仓，首先检查当前 Tick 是否触发止盈或止损
                if (activePos != null)
                {
                    if (activePos.Side == TradeSide.Buy)
                    {
                        // 多单止损：价格低于等于止损价
                        if (curPrice <= activePos.TargetStopLossPrice)
                        {
                            ClosePosition(activePos, activePos.TargetStopLossPrice, curTick.Time, curTick.BarIndex, PositionExitReason.StopLoss);
                            activePos = null;
                        }
                        // 多单止盈：价格高于等于止盈价
                        else if (curPrice >= activePos.TargetTakeProfitPrice)
                        {
                            ClosePosition(activePos, activePos.TargetTakeProfitPrice, curTick.Time, curTick.BarIndex, PositionExitReason.TakeProfit);
                            activePos = null;
                        }
                    }
                    else // Short
                    {
                        // 空单止损：价格高于等于止损价
                        if (curPrice >= activePos.TargetStopLossPrice)
                        {
                            ClosePosition(activePos, activePos.TargetStopLossPrice, curTick.Time, curTick.BarIndex, PositionExitReason.StopLoss);
                            activePos = null;
                        }
                        // 空单止盈：价格低于等于止盈价
                        else if (curPrice <= activePos.TargetTakeProfitPrice)
                        {
                            ClosePosition(activePos, activePos.TargetTakeProfitPrice, curTick.Time, curTick.BarIndex, PositionExitReason.TakeProfit);
                            activePos = null;
                        }
                    }
                }

                // 2. 检查是否有新的触碰事件发生
                while (nextEventIdx < filteredTouchEvents.Count && filteredTouchEvents[nextEventIdx].TickIndex == t)
                {
                    var evt = filteredTouchEvents[nextEventIdx];
                    nextEventIdx++;

                    bool isSup = evt.Line.Type == TrendLineType.Support;
                    // 止损价计算：支撑线为下方 stopLossPct (0.01%)，阻力线为上方 stopLossPct (0.01%)
                    decimal slPrice = isSup
                        ? evt.LinePrice * (1m - slRatio)
                        : evt.LinePrice * (1m + slRatio);

                    activeProbes.Add((evt.TickIndex, evt.Line, evt.LinePrice, curPrice, curPrice, isSup, slPrice, evt.TouchDesc));
                }

                // 3. 跟踪并推进已激活的 15-Tick 反弹探针
                int maxObservationTicks = Math.Max(reboundTicks * 3, 60); // 允许在满 15 个 Tick 后的合理微观时间窗内捕捉反弹确认

                for (int p = activeProbes.Count - 1; p >= 0; p--)
                {
                    var probe = activeProbes[p];
                    int ticksPassed = t - probe.StartTickIdx;

                    // 3.1 检查观察期间是否被击穿止损线 (若击穿则反弹失败，立即取消探针)
                    if (probe.IsSupport)
                    {
                        if (curPrice <= probe.StopLossPrice)
                        {
                            activeProbes.RemoveAt(p);
                            continue;
                        }
                    }
                    else
                    {
                        if (curPrice >= probe.StopLossPrice)
                        {
                            activeProbes.RemoveAt(p);
                            continue;
                        }
                    }

                    // 3.2 更新期间极值
                    decimal newMin = Math.Min(probe.MinPrice, curPrice);
                    decimal newMax = Math.Max(probe.MaxPrice, curPrice);
                    activeProbes[p] = (probe.StartTickIdx, probe.Line, probe.LinePrice, newMin, newMax, probe.IsSupport, probe.StopLossPrice, probe.TouchDesc);

                    // 3.3 检查是否已达到设定的观察 Tick 数 (默认 15) 并等待反弹确认入场
                    if (ticksPassed >= reboundTicks)
                    {
                        // 仅当当前无持仓时才开立新仓位
                        if (activePos == null)
                        {
                            bool shouldEnter = false;
                            TradeSide entrySide = probe.IsSupport ? TradeSide.Buy : TradeSide.Sell;

                            if (probe.IsSupport)
                            {
                                // 支撑反弹条件：当前价格高于观察期最低价，且价格保持在止损线上方 (反弹确认)
                                bool isRebound = curPrice > probe.MinPrice && curPrice >= probe.StopLossPrice;
                                if (isRebound) shouldEnter = true;
                            }
                            else
                            {
                                // 阻力回落条件：当前价格低于观察期最高价，且价格保持在止损线下方 (回落确认)
                                bool isPullback = curPrice < probe.MaxPrice && curPrice <= probe.StopLossPrice;
                                if (isPullback) shouldEnter = true;
                            }

                            if (shouldEnter)
                            {
                                // 计算仓位价值
                                decimal posValue = compoundInterest
                                    ? currentCapital * (positionSizePct / 100.0m)
                                    : initialCapital * (positionSizePct / 100.0m);
                                if (posValue <= 0) posValue = 1000m;

                                // 目标止盈价：入场价 ± takeProfitPct (默认 1%)
                                decimal tpRatio = takeProfitPct / 100.0m;
                                decimal tpPrice = entrySide == TradeSide.Buy
                                    ? curPrice * (1m + tpRatio)
                                    : curPrice * (1m - tpRatio);

                                activePos = new ActivePosition
                                {
                                    EntryBarIndex = curTick.BarIndex,
                                    Side = entrySide,
                                    EntryPrice = curPrice,
                                    EntryTime = curTick.Time,
                                    TargetStopLossPrice = probe.StopLossPrice, // 紫色趋势线下方 0.01%
                                    TargetTakeProfitPrice = tpPrice,          // 入场价上方 1%
                                    TrendLinePrice = probe.LinePrice,
                                    TriggerLineType = probe.Line.Type,
                                    PositionValue = posValue,
                                    TouchDesc = probe.TouchDesc
                                };

                                // 成功入场，移除该探针
                                activeProbes.RemoveAt(p);
                                continue;
                            }
                        }

                        // 若已超过最大允许观察窗口仍未反弹，或已有持仓，则移除探针
                        if (ticksPassed >= maxObservationTicks || activePos != null)
                        {
                            activeProbes.RemoveAt(p);
                        }
                    }
                }
            }

            // 6. 回测结束：若仍有持仓，以全量回测最后一笔 Tick 收盘结算
            if (activePos != null && allTicks.Count > 0)
            {
                var lastTick = allTicks[^1];
                ClosePosition(activePos, lastTick.Price, lastTick.Time, lastTick.BarIndex, PositionExitReason.EndOfBacktest);
                activePos = null;
            }

            // 7. 汇总回测报表指标
            report.FinalCapital = currentCapital;
            report.TotalNetProfit = currentCapital - initialCapital;
            report.TotalReturnPct = initialCapital > 0 ? (report.TotalNetProfit / initialCapital) * 100.0m : 0m;
            report.MaxDrawdownUsdt = maxDrawdownUsdt;
            report.MaxDrawdownPct = maxDrawdownPct;
            report.MaxConsecutiveWins = maxConsecutiveWins;
            report.MaxConsecutiveLosses = maxConsecutiveLosses;
            report.AverageHoldingDuration = report.TotalTrades > 0 ? TimeSpan.FromMilliseconds((double)totalHoldingMs / report.TotalTrades) : TimeSpan.Zero;

            return report;
        }
    }
}
