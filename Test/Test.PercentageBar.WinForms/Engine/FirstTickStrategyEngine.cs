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
    /// 首 Tick 动量交易明细记录
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
    /// 首 Tick 动量策略回测综合报告指标
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
        public int SignalReversalCount => Trades.Count(t => t.ExitReason == PositionExitReason.SignalReversal);
        public int SignalReversalWins => Trades.Count(t => t.ExitReason == PositionExitReason.SignalReversal && t.IsWin);
        public int SignalReversalLosses => Trades.Count(t => t.ExitReason == PositionExitReason.SignalReversal && !t.IsWin);
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
        /// 生成格式化结构报告卡片文本
        /// </summary>
        public string GenerateTextReport()
        {
            var sb = new StringBuilder();
            string unitDesc = SliceUnit == SliceUnitType.Percentage ? $"±{ThresholdValue:F2}%" : $"±{ThresholdValue:F2} USDT";
            string retSign = TotalNetProfit >= 0 ? "+" : "";

            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ 🏆【K线首Tick动量策略 (无固定止盈 / 跨Bar同向顺延奔跑 / 阈值止损) 全量回测报告】");
            sb.AppendLine("╠──────────────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"║ 🏷️ 交易对: {Coin}  |  切片基准: {unitDesc}  |  止损模式: 切片周期指定价格 (±Delta)");
            sb.AppendLine($"║ 🎯 核心逻辑: 首Tick开仓(不设固定止盈)；跨入下根K线首Tick同向则【不平仓继续持有让利润奔跑】，反向则【立即平仓并反手】");
            sb.AppendLine("╠──────────────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"║ 💰 资金规模: 初始本金 = {InitialCapital:N2} USDT  |  最终净值 = {FinalCapital:N2} USDT  |  累计净收益 = {retSign}{TotalNetProfit:N2} USDT ({retSign}{TotalReturnPct:F2}%)");
            sb.AppendLine($"║ 📊 胜率统计: 总交易 = {TotalTrades:N0} 笔  |  盈利 = {WinTrades:N0} 笔  |  亏损 = {LossTrades:N0} 笔  |  胜率 = {WinRatePct:F2}%");
            sb.AppendLine($"║ 📌 平仓构成: 🛑阈值止损 = {StopLossCount:N0}笔 | 🔄反向平仓 = {SignalReversalCount:N0}笔 (盈利{SignalReversalWins}胜 / 亏损{SignalReversalLosses}负) | 最长跨Bar持仓 = {MaxHoldingBars}根Bar");
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
                string fileName = $"{Coin}_FirstTick_Backtest_{timestamp}_report.html";
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
            sb.AppendLine($"    <title>🏆 K线首Tick动量顺延策略量化回测报告 - {Coin} ({unitDesc})</title>");
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
        .badge-unit { background: #8b5cf6; color: #ffffff; }
        .badge-ratio { background: #10b981; color: #ffffff; }
        .badge-time { background: #0284c7; color: #ffffff; }

        /* KPI Cards Grid */
        .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 16px; margin-bottom: 24px; }
        .kpi-card { background: #1e293b; padding: 20px; border-radius: 14px; border: 1px solid #334155; border-left: 4px solid #38bdf8; transition: transform 0.2s, box-shadow 0.2s; }
        .kpi-card:hover { transform: translateY(-2px); box-shadow: 0 6px 16px rgba(0,0,0,0.3); }
        .kpi-label { font-size: 13px; color: #94a3b8; margin-bottom: 6px; font-weight: 500; }
        .kpi-value { font-size: 23px; font-weight: 700; }
        .kpi-sub { font-size: 12px; color: #64748b; margin-top: 4px; }

        .text-green { color: #10b981 !important; }
        .text-red { color: #f43f5e !important; }
        .text-cyan { color: #06b6d4 !important; }
        .text-amber { color: #f59e0b !important; }
        .text-purple { color: #c084fc !important; }
        .text-blue { color: #38bdf8 !important; }

        /* Chart Section */
        .chart-section { background: #1e293b; padding: 24px; border-radius: 16px; border: 1px solid #334155; margin-bottom: 24px; }
        .section-title { font-size: 17px; font-weight: 600; color: #f1f5f9; margin-bottom: 16px; display: flex; align-items: center; gap: 8px; }
        .chart-container { position: relative; height: 360px; width: 100%; }

        /* Strategy Info Box */
        .strategy-info { background: #0f172a; padding: 18px 20px; border-radius: 12px; border: 1px solid #334155; margin-bottom: 24px; font-size: 13.5px; line-height: 1.7; color: #cbd5e1; }
        .strategy-info strong { color: #38bdf8; }

        /* Trades Table Section */
        .table-section { background: #1e293b; padding: 24px; border-radius: 16px; border: 1px solid #334155; }
        .table-controls { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; flex-wrap: wrap; gap: 12px; }
        .search-box { background: #0f172a; border: 1px solid #334155; color: #f8fafc; padding: 8px 16px; border-radius: 8px; font-size: 13px; width: 280px; outline: none; }
        .search-box:focus { border-color: #38bdf8; }
        .filter-btn-group { display: flex; gap: 6px; flex-wrap: wrap; }
        .filter-btn { background: #334155; border: none; color: #cbd5e1; padding: 6px 14px; border-radius: 8px; cursor: pointer; font-size: 12px; font-weight: 500; transition: background 0.15s; }
        .filter-btn.active, .filter-btn:hover { background: #3b82f6; color: #ffffff; }

        .table-wrapper { overflow-x: auto; max-height: 650px; border-radius: 10px; border: 1px solid #334155; }
        table { width: 100%; border-collapse: collapse; text-align: left; font-size: 13px; }
        thead { background: #0f172a; position: sticky; top: 0; z-index: 10; }
        th { padding: 12px 14px; color: #94a3b8; font-weight: 600; border-bottom: 1px solid #334155; white-space: nowrap; }
        td { padding: 10px 14px; border-bottom: 1px solid #1e293b; color: #e2e8f0; white-space: nowrap; }
        tbody tr { background: #1e293b; transition: background 0.15s; }
        tbody tr:hover { background: #334155; }

        .tag { display: inline-block; padding: 2px 8px; border-radius: 6px; font-size: 11.5px; font-weight: 600; }
        .tag-long { background: rgba(16, 185, 129, 0.15); color: #34d399; border: 1px solid rgba(16, 185, 129, 0.3); }
        .tag-short { background: rgba(244, 63, 94, 0.15); color: #fb7185; border: 1px solid rgba(244, 63, 94, 0.3); }
        .tag-win { background: rgba(16, 185, 129, 0.2); color: #10b981; font-weight: 700; border: 1px solid rgba(16, 185, 129, 0.3); }
        .tag-sl { background: rgba(244, 63, 94, 0.2); color: #f43f5e; font-weight: 700; border: 1px solid rgba(244, 63, 94, 0.3); }
        .tag-rev { background: rgba(245, 158, 11, 0.2); color: #f59e0b; font-weight: 700; border: 1px solid rgba(245, 158, 11, 0.3); }
        .tag-end { background: rgba(148, 163, 184, 0.2); color: #94a3b8; }
        
        .footer { text-align: center; color: #64748b; font-size: 13px; margin-top: 32px; padding-bottom: 24px; }
    ");
            sb.AppendLine("    </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"container\">");

            // 1. Header
            sb.AppendLine("    <div class=\"header\">");
            sb.AppendLine("        <div class=\"header-title\">");
            sb.AppendLine($"            <h1>🏆 K线首 Tick 动量顺延策略量化回测综合报告</h1>");
            sb.AppendLine($"            <p>生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 运行模式: 无固定止盈 / 跨 Bar 同向顺延奔跑 / 切片阈值止损</p>");
            sb.AppendLine("            <div class=\"badges\">");
            sb.AppendLine($"                <span class=\"badge badge-coin\">{Coin}</span>");
            sb.AppendLine($"                <span class=\"badge badge-unit\">切片基准: {unitDesc}</span>");
            sb.AppendLine($"                <span class=\"badge badge-ratio\">止损: ±{ThresholdValue:F2}{(SliceUnit == SliceUnitType.Percentage ? "%" : " U")}</span>");
            sb.AppendLine($"                <span class=\"badge badge-time\">总交易: {TotalTrades:N0} 笔</span>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // 2. 策略原理提示框
            sb.AppendLine("    <div class=\"strategy-info\">");
            sb.AppendLine("        <strong>🎯 策略执行机制 (不再受 1:1 止盈限制，让利润充分奔跑):</strong><br>");
            sb.AppendLine("        • <strong>开仓与止损:</strong> 根据 K 线的第一个 Tick 流向开仓 (Buy 开多 / Sell 开空)，<strong>不设固定 1:1 止盈</strong>，止损设定为 K 线切片周期指定价格 (±Delta)；<br>");
            sb.AppendLine("        • <strong>跨 Bar 同向顺延:</strong> 当开仓后未触及止损进入下一根 K 线时，若<strong>下周期 K 线第一个 Tick 与当前仓位方向相同</strong>，则<strong>【继续持有不平仓】</strong>，持续捕获单边趋势波段；<br>");
            sb.AppendLine("        • <strong>跨 Bar 反向平仓:</strong> 若下周期 K 线第一个 Tick 与当前仓位<strong>方向相反</strong>，则<strong>【立即在首笔 Tick 价格平仓】</strong>锁定波段收益，并即刻反向开新仓。");
            sb.AppendLine("    </div>");

            // 3. KPI 仪表盘
            sb.AppendLine("    <div class=\"kpi-grid\">");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #10b981;\">");
            sb.AppendLine("            <div class=\"kpi-label\">累计净收益 (Net PnL)</div>");
            sb.AppendLine($"            <div class=\"kpi-value {pnlColorClass}\">{pnlSign}{TotalNetProfit:N2} USDT</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">累计收益率: {pnlSign}{TotalReturnPct:F2}% (本金: {InitialCapital:N2} U)</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #3b82f6;\">");
            sb.AppendLine("            <div class=\"kpi-label\">综合胜率 (Win Rate)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-blue\">{WinRatePct:F2}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">🏆盈利: {WinTrades} 笔 | 🛑止损: {StopLossCount} 笔 | 🔄反向平仓: {SignalReversalCount} 笔</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #f59e0b;\">");
            sb.AppendLine("            <div class=\"kpi-label\">获利因子 (Profit Factor)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-amber\">{ProfitFactor:F2}</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">单笔期望: {pnlSign}{ExpectancyUsdt:N2} U ({pnlSign}{ExpectancyPct:F3}%)</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #f43f5e;\">");
            sb.AppendLine("            <div class=\"kpi-label\">最大历史回撤 (Max Drawdown)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-red\">-{MaxDrawdownUsdt:N2} USDT</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">最大回撤率: -{MaxDrawdownPct:F2}% | 连亏: {MaxConsecutiveLosses} 笔</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #8b5cf6;\">");
            sb.AppendLine("            <div class=\"kpi-label\">多单 vs 空单胜率</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-purple\">{LongWinRatePct:F1}% / {ShortWinRatePct:F1}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">多单: {LongTrades:N0}笔 ({LongWins}胜) | 空单: {ShortTrades:N0}笔 ({ShortWins}胜)</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #06b6d4;\">");
            sb.AppendLine("            <div class=\"kpi-label\">持仓效率与最长跨Bar</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-cyan\">{PercentPlotHelper.FormatTimeSpan(AverageHoldingDuration)}</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">单笔最长跨度: {MaxHoldingBars} 根 Bar | 连胜: {MaxConsecutiveWins} 笔</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("    </div>");

            // 4. Chart 1: 累计账户净值曲线
            sb.AppendLine("    <div class=\"chart-section\">");
            sb.AppendLine("        <div class=\"section-title\">📈 累计账户净值资金曲线 (Equity Curve)</div>");
            sb.AppendLine("        <div class=\"chart-container\">");
            sb.AppendLine("            <canvas id=\"equityChart\"></canvas>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // 5. Chart 2: 逐笔交易收益柱状分布图
            sb.AppendLine("    <div class=\"chart-section\">");
            sb.AppendLine("        <div class=\"section-title\">📊 逐笔交易盈亏分布 (Per-Trade PnL Distribution)</div>");
            sb.AppendLine("        <div class=\"chart-container\">");
            sb.AppendLine("            <canvas id=\"pnlChart\"></canvas>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // 6. 逐笔交易明细表
            sb.AppendLine("    <div class=\"table-section\">");
            sb.AppendLine("        <div class=\"section-title\">📑 逐笔交易明细流水清单 (Trade Log History)</div>");
            sb.AppendLine("        <div class=\"table-controls\">");
            sb.AppendLine("            <input type=\"text\" id=\"searchBox\" class=\"search-box\" placeholder=\"🔍 搜索 Bar#、时间、方向、结果...\" onkeyup=\"filterTrades()\">");
            sb.AppendLine("            <div class=\"filter-btn-group\">");
            sb.AppendLine($"                <button class=\"filter-btn active\" onclick=\"setFilter('all', this)\">全部 ({TotalTrades})</button>");
            sb.AppendLine($"                <button class=\"filter-btn\" onclick=\"setFilter('buy', this)\">🟢 多单 ({LongTrades})</button>");
            sb.AppendLine($"                <button class=\"filter-btn\" onclick=\"setFilter('sell', this)\">🔴 空单 ({ShortTrades})</button>");
            sb.AppendLine($"                <button class=\"filter-btn\" onclick=\"setFilter('win', this)\">🏆 盈利 ({WinTrades})</button>");
            sb.AppendLine($"                <button class=\"filter-btn\" onclick=\"setFilter('sl', this)\">🛑 止损 ({StopLossCount})</button>");
            sb.AppendLine($"                <button class=\"filter-btn\" onclick=\"setFilter('rev', this)\">🔄 反向平仓 ({SignalReversalCount})</button>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"table-wrapper\">");
            sb.AppendLine("            <table id=\"tradesTable\">");
            sb.AppendLine("                <thead>");
            sb.AppendLine("                    <tr>");
            sb.AppendLine("                        <th>#</th>");
            sb.AppendLine("                        <th>持仓Bar范围</th>");
            sb.AppendLine("                        <th>方向</th>");
            sb.AppendLine("                        <th>开仓时间</th>");
            sb.AppendLine("                        <th>开仓价 (USDT)</th>");
            sb.AppendLine("                        <th>平仓时间</th>");
            sb.AppendLine("                        <th>平仓价 (USDT)</th>");
            sb.AppendLine("                        <th>平仓结果</th>");
            sb.AppendLine("                        <th>毛收益 (USDT)</th>");
            sb.AppendLine("                        <th>手续费 (USDT)</th>");
            sb.AppendLine("                        <th>净收益 (USDT)</th>");
            sb.AppendLine("                        <th>收益率 (%)</th>");
            sb.AppendLine("                        <th>账户净值 (USDT)</th>");
            sb.AppendLine("                        <th>持仓时长</th>");
            sb.AppendLine("                    </tr>");
            sb.AppendLine("                </thead>");
            sb.AppendLine("                <tbody>");

            foreach (var t in Trades)
            {
                bool isLong = t.Side == TradeSide.Buy;
                string sideTag = isLong ? "<span class=\"tag tag-long\">🟢 多单</span>" : "<span class=\"tag tag-short\">🔴 空单</span>";
                string reasonTag;
                if (t.ExitReason == PositionExitReason.StopLoss)
                {
                    reasonTag = "<span class=\"tag tag-sl\">🛑 阈值止损</span>";
                }
                else if (t.ExitReason == PositionExitReason.SignalReversal)
                {
                    reasonTag = t.IsWin
                        ? $"<span class=\"tag tag-win\">🔄 反向平仓 (+{t.ReturnPct:F2}%)</span>"
                        : $"<span class=\"tag tag-rev\">🔄 反向平仓 ({t.ReturnPct:F2}%)</span>";
                }
                else
                {
                    reasonTag = "<span class=\"tag tag-end\">期末平仓</span>";
                }

                string pnlClass = t.NetPnL >= 0 ? "text-green" : "text-red";
                string sideAttr = isLong ? "buy" : "sell";
                string reasonAttr = t.ExitReason switch
                {
                    PositionExitReason.StopLoss => "sl",
                    PositionExitReason.SignalReversal => "rev",
                    _ => "end"
                };
                string winAttr = t.IsWin ? "win" : "loss";

                sb.AppendLine($"                    <tr data-side=\"{sideAttr}\" data-reason=\"{reasonAttr}\" data-win=\"{winAttr}\">");
                sb.AppendLine($"                        <td>{t.TradeId}</td>");
                sb.AppendLine($"                        <td>{t.BarRangeDesc}</td>");
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
            sb.AppendLine($"        <p>🏆 量化高频百分比 K 线回测系统 • 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
            sb.AppendLine("    </div>");

            // JavaScript for Charts & Search/Filter
            sb.AppendLine("</div>");
            sb.AppendLine("<script>");
            sb.AppendLine($@"
        // 1. 资金净值走势折线图
        const ctxEquity = document.getElementById('equityChart').getContext('2d');
        const equityGradient = ctxEquity.createLinearGradient(0, 0, 0, 360);
        equityGradient.addColorStop(0, 'rgba(56, 189, 248, 0.35)');
        equityGradient.addColorStop(1, 'rgba(56, 189, 248, 0.00)');

        new Chart(ctxEquity, {{
            type: 'line',
            data: {{
                labels: [{labelsJson}],
                datasets: [{{
                    label: '账户净值 (USDT)',
                    data: [{equityJson}],
                    borderColor: '#38bdf8',
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
                        titleColor: '#38bdf8',
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
                        titleColor: '#38bdf8',
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
                else if (currentFilter === 'sl' && reason === 'sl') matchFilter = true;
                else if (currentFilter === 'rev' && reason === 'rev') matchFilter = true;

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
    /// 内部活动持仓跟踪状态 (用于跨 Bar 顺延与方向判定)
    /// </summary>
    internal class ActivePosition
    {
        public int EntryBarIndex { get; set; }
        public TradeSide Side { get; set; }
        public decimal EntryPrice { get; set; }
        public long EntryTime { get; set; }
        public decimal TargetStopLossPrice { get; set; }
        public decimal PositionValue { get; set; }
    }

    /// <summary>
    /// K 线首 Tick 动量交易策略回测计算引擎
    /// </summary>
    public static class FirstTickStrategyEngine
    {
        /// <summary>
        /// 执行首 Tick 动量策略回测 (无固定止盈 / 跨 Bar 同向顺延奔跑 / 切片阈值止损)
        /// </summary>
        public static FirstTickBacktestReport RunBacktest(
            IReadOnlyList<PercentageKline> bars,
            string coin,
            decimal thresholdValue,
            SliceUnitType sliceUnit = SliceUnitType.Percentage,
            decimal initialCapital = 10000m,
            decimal positionSizePct = 100m,
            decimal feeRate = 0.0004m,
            bool compoundInterest = false)
        {
            var report = new FirstTickBacktestReport
            {
                Coin = coin,
                ThresholdValue = thresholdValue,
                SliceUnit = sliceUnit,
                InitialCapital = initialCapital,
                FinalCapital = initialCapital
            };

            if (bars == null || bars.Count == 0 || thresholdValue <= 0) return report;

            decimal currentCapital = initialCapital;
            decimal peakCapital = initialCapital;
            decimal maxDrawdownUsdt = 0m;
            decimal maxDrawdownPct = 0m;

            int consecutiveWins = 0, maxConsecutiveWins = 0;
            int consecutiveLosses = 0, maxConsecutiveLosses = 0;
            long totalHoldingMs = 0;

            int tradeId = 1;
            ActivePosition? activePos = null;

            // 平仓并记录交易辅助闭包
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

            // 开仓辅助闭包 (不设固定止盈，止损由切片周期阈值指定)
            ActivePosition OpenPosition(TradeSide side, decimal entryPrice, long entryTime, int entryBarIndex)
            {
                decimal delta;
                if (sliceUnit == SliceUnitType.Percentage)
                {
                    delta = entryPrice * (thresholdValue / 100.0m);
                }
                else
                {
                    delta = thresholdValue;
                }

                decimal slPrice = side == TradeSide.Buy ? (entryPrice - delta) : (entryPrice + delta);

                decimal posValue = compoundInterest
                    ? currentCapital * (positionSizePct / 100.0m)
                    : initialCapital * (positionSizePct / 100.0m);

                if (posValue <= 0) posValue = 1000m;

                return new ActivePosition
                {
                    EntryBarIndex = entryBarIndex,
                    Side = side,
                    EntryPrice = entryPrice,
                    EntryTime = entryTime,
                    TargetStopLossPrice = slPrice,
                    PositionValue = posValue
                };
            }

            // 逐 Bar 流式推进仿真
            for (int i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                if (bar.Ticks == null || bar.Ticks.Length == 0) continue;

                var ticks = bar.Ticks;
                var firstTick = ticks[0];
                if (firstTick.Price <= 0) continue;

                // 1. 判断本根 K 线首笔 Tick 流向 (主动买入开多 / 主动卖出开空)
                bool isFirstTickBuy = !firstTick.IsBuyerMaker;
                TradeSide firstTickSide = isFirstTickBuy ? TradeSide.Buy : TradeSide.Sell;

                int startTickIdx = 1;

                if (activePos != null)
                {
                    // 仓位已跨越进入到下一个 K 线周期 (Bar i)
                    if (activePos.Side == firstTickSide)
                    {
                        // 满足同方向仓位 -> 不平仓，继续持有让利润奔跑，监控止损
                        startTickIdx = 0; // 从本 Bar 第一个 tick 开始参与止损判定
                    }
                    else
                    {
                        // 反之（方向相反）-> 在本 Bar 的第一个 tick 立即平仓 (反向首Tick平仓)
                        ClosePosition(activePos, firstTick.Price, firstTick.Time, bar.BarIndex, PositionExitReason.SignalReversal);
                        activePos = null;

                        // 并由首笔反向 Tick 立即建立新方向仓位
                        activePos = OpenPosition(firstTickSide, firstTick.Price, firstTick.Time, bar.BarIndex);
                        startTickIdx = 1;
                    }
                }
                else
                {
                    // 当前无持仓 -> 由首笔 Tick 建立新仓位
                    activePos = OpenPosition(firstTickSide, firstTick.Price, firstTick.Time, bar.BarIndex);
                    startTickIdx = 1;
                }

                // 2. 扫描本 Bar 内后续 Ticks 进行止损 (Stop Loss) 判定
                if (activePos != null)
                {
                    for (int t = startTickIdx; t < ticks.Length; t++)
                    {
                        var tick = ticks[t];
                        decimal p = tick.Price;

                        if (activePos.Side == TradeSide.Buy)
                        {
                            if (p <= activePos.TargetStopLossPrice)
                            {
                                ClosePosition(activePos, activePos.TargetStopLossPrice, tick.Time, bar.BarIndex, PositionExitReason.StopLoss);
                                activePos = null;
                                break;
                            }
                        }
                        else // Sell
                        {
                            if (p >= activePos.TargetStopLossPrice)
                            {
                                ClosePosition(activePos, activePos.TargetStopLossPrice, tick.Time, bar.BarIndex, PositionExitReason.StopLoss);
                                activePos = null;
                                break;
                            }
                        }
                    }
                }

                // 若本 Bar 结束时 activePos 仍未触发止损，则保持 activePos 不变，自然进入下根 Bar 继续顺延持有判定！
            }

            // 回测结束，若仍有未平仓持仓，在最后一根 Bar 的收盘价结算平仓
            if (activePos != null && bars.Count > 0)
            {
                var lastBar = bars[^1];
                ClosePosition(activePos, lastBar.Close, lastBar.CloseTime, lastBar.BarIndex, PositionExitReason.EndOfBacktest);
                activePos = null;
            }

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
