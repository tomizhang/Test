using Common.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Common.Helper
{
    /// <summary>
    /// 专业量化回测 HTML 报告生成器
    /// 生成包含 KPI 仪表盘、累计收益曲线交互图表、逐笔交易明细表与策略参数总览的独立自包含 HTML 报告
    /// </summary>
    public static class HtmlReportHelper
    {
        public static string GenerateReport(BacktestResult result, BacktestRequest request, string? outputFilePath = null)
        {
            if (result == null || request == null)
                throw new ArgumentNullException(nameof(result));

            string reportsDir = Config.GetReportsPath();
            if (string.IsNullOrEmpty(outputFilePath))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"{request.Coin}_{request.Interval.ToIntervalString()}_{request.StartDate:yyyyMMdd}_{request.EndDate:yyyyMMdd}_{timestamp}_report.html";
                outputFilePath = Path.Combine(reportsDir, fileName);
            }

            var trades = result.CompletedTrades ?? new List<TradeRecord>();

            // 1. 计算核心指标统计
            int totalTrades = trades.Count;
            int winTrades = trades.Count(t => t.IsWin);
            int lossTrades = trades.Count(t => !t.IsWin && t.ExitReason != PositionExitReason.None);
            double winRate = totalTrades > 0 ? (double)winTrades / totalTrades * 100.0 : 0.0;

            decimal totalPnL = trades.Sum(t => t.PnLPct);
            decimal grossProfit = trades.Where(t => t.PnLPct > 0).Sum(t => t.PnLPct);
            decimal grossLoss = Math.Abs(trades.Where(t => t.PnLPct < 0).Sum(t => t.PnLPct));
            decimal profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? 99.99m : 0m);

            decimal avgWin = winTrades > 0 ? grossProfit / winTrades : 0m;
            decimal avgLoss = lossTrades > 0 ? grossLoss / lossTrades : 0m;
            decimal winLossRatio = avgLoss > 0 ? avgWin / avgLoss : 0m;

            int longCount = trades.Count(t => t.Side == TradeSide.Buy);
            int shortCount = trades.Count(t => t.Side == TradeSide.Sell);
            int longWins = trades.Count(t => t.Side == TradeSide.Buy && t.IsWin);
            int shortWins = trades.Count(t => t.Side == TradeSide.Sell && t.IsWin);

            // 计算最大回撤与资金曲线点
            decimal peak = 0m;
            decimal maxDrawdown = 0m;
            decimal currentEquity = 0m;

            var equityPoints = new List<(int Index, string Time, decimal Equity, decimal TradePnL)>();
            equityPoints.Add((0, request.StartDate.ToString("yyyy-MM-dd"), 0m, 0m));

            int maxConsecWins = 0, currentConsecWins = 0;
            int maxConsecLosses = 0, currentConsecLosses = 0;

            for (int i = 0; i < trades.Count; i++)
            {
                var t = trades[i];
                currentEquity += t.PnLPct;
                if (currentEquity > peak) peak = currentEquity;
                decimal dd = peak - currentEquity;
                if (dd > maxDrawdown) maxDrawdown = dd;

                equityPoints.Add((i + 1, t.ExitTime.ToString("yyyy-MM-dd HH:mm"), currentEquity, t.PnLPct));

                if (t.IsWin)
                {
                    currentConsecWins++;
                    currentConsecLosses = 0;
                    if (currentConsecWins > maxConsecWins) maxConsecWins = currentConsecWins;
                }
                else
                {
                    currentConsecLosses++;
                    currentConsecWins = 0;
                    if (currentConsecLosses > maxConsecLosses) maxConsecLosses = currentConsecLosses;
                }
            }

            // 更新结果字段
            result.WinningTradesCount = winTrades;
            result.LosingTradesCount = lossTrades;
            result.TotalPnLPct = totalPnL;
            result.ProfitFactor = profitFactor;
            result.MaxDrawdownPct = maxDrawdown;
            result.AvgWinPct = avgWin;
            result.AvgLossPct = avgLoss;
            result.MaxConsecutiveWins = maxConsecWins;
            result.MaxConsecutiveLosses = maxConsecLosses;

            // 2. 构造 HTML 内容
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("    <meta charset=\"UTF-8\">");
            sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine($"    <title>量化回测分析报告 - {request.Coin} {request.Interval.ToIntervalString()}</title>");
            sb.AppendLine("    <script src=\"https://cdn.jsdelivr.net/npm/chart.js\"></script>");
            sb.AppendLine("    <style>");
            sb.AppendLine(@"
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; background-color: #0f172a; color: #f8fafc; line-height: 1.5; padding: 24px; }
        .container { max-width: 1400px; margin: 0 auto; }
        
        /* Header */
        .header { display: flex; justify-content: space-between; align-items: center; background: #1e293b; padding: 24px 32px; border-radius: 16px; margin-bottom: 24px; border: 1px solid #334155; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.2); }
        .header-title h1 { font-size: 26px; font-weight: 700; color: #f8fafc; margin-bottom: 6px; display: flex; align-items: center; gap: 10px; }
        .header-title p { color: #94a3b8; font-size: 14px; }
        .badges { display: flex; gap: 8px; margin-top: 8px; }
        .badge { display: inline-block; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600; text-transform: uppercase; }
        .badge-coin { background: #3b82f6; color: #ffffff; }
        .badge-interval { background: #8b5cf6; color: #ffffff; }
        .badge-dates { background: #0284c7; color: #ffffff; }

        /* KPI Cards Grid */
        .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 16px; margin-bottom: 24px; }
        .kpi-card { background: #1e293b; padding: 20px; border-radius: 14px; border: 1px solid #334155; border-left: 4px solid #38bdf8; transition: transform 0.2s; }
        .kpi-card:hover { transform: translateY(-2px); }
        .kpi-label { font-size: 13px; color: #94a3b8; margin-bottom: 6px; font-weight: 500; }
        .kpi-value { font-size: 24px; font-weight: 700; }
        .kpi-sub { font-size: 12px; color: #64748b; margin-top: 4px; }

        .text-green { color: #10b981 !important; }
        .text-red { color: #f43f5e !important; }
        .text-cyan { color: #06b6d4 !important; }
        .text-amber { color: #f59e0b !important; }
        .text-purple { color: #c084fc !important; }

        /* Chart Section */
        .chart-section { background: #1e293b; padding: 24px; border-radius: 16px; border: 1px solid #334155; margin-bottom: 24px; }
        .section-title { font-size: 18px; font-weight: 600; color: #f1f5f9; margin-bottom: 16px; display: flex; align-items: center; gap: 8px; }
        .chart-container { position: relative; height: 360px; width: 100%; }

        /* Parameters Section */
        .params-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 16px; }
        .param-group { background: #0f172a; padding: 16px; border-radius: 10px; border: 1px solid #334155; }
        .param-item { display: flex; justify-content: space-between; padding: 6px 0; border-bottom: 1px dashed #334155; font-size: 13px; }
        .param-item:last-child { border-bottom: none; }
        .param-name { color: #94a3b8; }
        .param-val { color: #f8fafc; font-weight: 600; }

        /* Trades Table Section */
        .table-section { background: #1e293b; padding: 24px; border-radius: 16px; border: 1px solid #334155; }
        .table-controls { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; flex-wrap: wrap; gap: 12px; }
        .search-box { background: #0f172a; border: 1px solid #334155; color: #f8fafc; padding: 8px 16px; border-radius: 8px; font-size: 13px; width: 280px; }
        .filter-btn-group { display: flex; gap: 6px; }
        .filter-btn { background: #334155; border: none; color: #cbd5e1; padding: 6px 14px; border-radius: 8px; cursor: pointer; font-size: 12px; font-weight: 500; }
        .filter-btn.active { background: #3b82f6; color: #ffffff; }

        .table-wrapper { overflow-x: auto; max-height: 600px; border-radius: 10px; border: 1px solid #334155; }
        table { width: 100%; border-collapse: collapse; text-align: left; font-size: 13px; }
        thead { background: #0f172a; position: sticky; top: 0; z-index: 10; }
        th { padding: 12px 14px; color: #94a3b8; font-weight: 600; border-bottom: 1px solid #334155; white-space: nowrap; }
        td { padding: 12px 14px; border-bottom: 1px solid #1e293b; color: #e2e8f0; }
        tbody tr { background: #1e293b; transition: background 0.15s; }
        tbody tr:hover { background: #334155; }

        .tag { display: inline-block; padding: 3px 8px; border-radius: 6px; font-size: 11px; font-weight: 600; }
        .tag-long { background: rgba(16, 185, 129, 0.15); color: #34d399; border: 1px solid rgba(16, 185, 129, 0.3); }
        .tag-short { background: rgba(244, 63, 94, 0.15); color: #fb7185; border: 1px solid rgba(244, 63, 94, 0.3); }
        .tag-tp { background: rgba(16, 185, 129, 0.2); color: #10b981; font-weight: 700; }
        .tag-sl { background: rgba(244, 63, 94, 0.2); color: #f43f5e; font-weight: 700; }
        .tag-end { background: rgba(148, 163, 184, 0.2); color: #94a3b8; }
        
        .footer { text-align: center; color: #64748b; font-size: 13px; margin-top: 32px; padding-bottom: 24px; }
    ");
            sb.AppendLine("    </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"container\">");

            // Header
            sb.AppendLine("    <div class=\"header\">");
            sb.AppendLine("        <div class=\"header-title\">");
            sb.AppendLine($"            <h1>📊 趋势线量化策略回测分析报告</h1>");
            sb.AppendLine($"            <p>生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 引擎: DuckDB + Incremental Multi-Layer TrendLine</p>");
            sb.AppendLine("            <div class=\"badges\">");
            sb.AppendLine($"                <span class=\"badge badge-coin\">{request.Coin}</span>");
            sb.AppendLine($"                <span class=\"badge badge-interval\">{request.Interval.ToIntervalString()}</span>");
            sb.AppendLine($"                <span class=\"badge badge-dates\">{request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}</span>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // KPI Grid
            string pnlColor = totalPnL >= 0 ? "text-green" : "text-red";
            string pnlSign = totalPnL >= 0 ? "+" : "";
            sb.AppendLine("    <div class=\"kpi-grid\">");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #10b981;\">");
            sb.AppendLine("            <div class=\"kpi-label\">累计净收益率 (Total Return)</div>");
            sb.AppendLine($"            <div class=\"kpi-value {pnlColor}\">{pnlSign}{totalPnL:F2}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">盈利: +{grossProfit:F2}% | 亏损: -{grossLoss:F2}%</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #3b82f6;\">");
            sb.AppendLine("            <div class=\"kpi-label\">交易胜率 (Win Rate)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-cyan\">{winRate:F1}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">胜单: {winTrades} 笔 | 负单: {lossTrades} 笔</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #f59e0b;\">");
            sb.AppendLine("            <div class=\"kpi-label\">获利因子 (Profit Factor)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-amber\">{profitFactor:F2}</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">单笔平均盈亏比: {winLossRatio:F2}</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #f43f5e;\">");
            sb.AppendLine("            <div class=\"kpi-label\">最大回撤 (Max Drawdown)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-red\">-{maxDrawdown:F2}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">最大连续亏损: {maxConsecLosses} 笔</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #8b5cf6;\">");
            sb.AppendLine("            <div class=\"kpi-label\">已平仓总交易 (Total Trades)</div>");
            sb.AppendLine($"            <div class=\"kpi-value text-purple\">{totalTrades} 笔</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">多单: {longCount} (胜率 {(longCount > 0 ? (double)longWins / longCount * 100.0 : 0):F0}%) | 空单: {shortCount} (胜率 {(shortCount > 0 ? (double)shortWins / shortCount * 100.0 : 0):F0}%)</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"kpi-card\" style=\"border-left-color: #06b6d4;\">");
            sb.AppendLine("            <div class=\"kpi-label\">止盈 / 止损设定 (TP / SL)</div>");
            sb.AppendLine($"            <div class=\"kpi-value\">+{request.TakeProfitPct:F1}% / -{request.StopLossPct:F1}%</div>");
            sb.AppendLine($"            <div class=\"kpi-sub\">冷却: {request.SignalCooldownSeconds}s | 跨度≥{request.MinSignalLineX1X2}</div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("    </div>");

            // Chart Section (Cumulative Equity Curve)
            sb.AppendLine("    <div class=\"chart-section\">");
            sb.AppendLine("        <div class=\"section-title\">📈 累计资金收益率曲线 (Cumulative Equity Curve)</div>");
            sb.AppendLine("        <div class=\"chart-container\">");
            sb.AppendLine("            <canvas id=\"equityChart\"></canvas>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Parameters & Diagnostics Grid
            sb.AppendLine("    <div class=\"chart-section\">");
            sb.AppendLine("        <div class=\"section-title\">⚙ 策略参数与运行指标</div>");
            sb.AppendLine("        <div class=\"params-grid\">");

            sb.AppendLine("            <div class=\"param-group\">");
            sb.AppendLine("                <div style=\"font-weight: 700; color: #38bdf8; margin-bottom: 8px;\">🎯 开仓与止盈止损设置</div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">止盈目标 (Take Profit)</span><span class=\"param-val text-green\">+{request.TakeProfitPct:F2}%</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">止损限制 (Stop Loss)</span><span class=\"param-val text-red\">-{request.StopLossPct:F2}%</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">信号开仓冷却</span><span class=\"param-val\">{request.SignalCooldownSeconds} 秒</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">最小趋势线跨度 (LineX1X2)</span><span class=\"param-val\">≥ {request.MinSignalLineX1X2} 根</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">最小延伸寿命 (LineAge)</span><span class=\"param-val\">≥ {request.MinSignalLineAge} 根</span></div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class=\"param-group\">");
            sb.AppendLine("                <div style=\"font-weight: 700; color: #c084fc; margin-bottom: 8px;\">📐 趋势线与极值形态参数</div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">极值点判定对比 (左右)</span><span class=\"param-val\">左 {request.LeftLen} 根 / 右 {request.RightLen} 根</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">趋势线最大配对跨度 (MaxSpan)</span><span class=\"param-val\">{request.MaxSpan} 根</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">外包络模式</span><span class=\"param-val\">{(request.AllowInternalPenetration ? "允许内部穿透" : "严格外包络 (禁止穿透)")}</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">滑动窗口容量 (MaxKlines)</span><span class=\"param-val\">{request.MaxKlinesCapacity:N0} 根</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">识别极值总数</span><span class=\"param-val\">高点 {result.PeaksCount:N0} | 低点 {result.ValleysCount:N0}</span></div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class=\"param-group\">");
            sb.AppendLine("                <div style=\"font-weight: 700; color: #34d399; margin-bottom: 8px;\">⚡ 引擎吞吐性能指标</div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">总回测执行耗时</span><span class=\"param-val\">{result.ElapsedMilliseconds:N0} ms</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">处理 K 线总量</span><span class=\"param-val\">{result.TotalKlines:N0} 根</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">处理 Tick 逐笔总量</span><span class=\"param-val\">{result.TotalTicks:N0} 笔</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">吞吐速率 (Throughput)</span><span class=\"param-val text-green\">{result.TicksPerSecond:N0} ticks/s</span></div>");
            sb.AppendLine($"                <div class=\"param-item\"><span class=\"param-name\">已击穿删除趋势线</span><span class=\"param-val\">{result.DeletedTrendLinesCount:N0} 条</span></div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Trades Table Section
            sb.AppendLine("    <div class=\"table-section\">");
            sb.AppendLine("        <div class=\"section-title\">📋 逐笔平仓交易明细清单</div>");
            sb.AppendLine("        <div class=\"table-controls\">");
            sb.AppendLine("            <input type=\"text\" id=\"searchBox\" class=\"search-box\" placeholder=\"🔍 搜索交易序号、方向、时间...\" onkeyup=\"filterTrades()\">");
            sb.AppendLine("            <div class=\"filter-btn-group\">");
            sb.AppendLine("                <button class=\"filter-btn active\" onclick=\"setFilter('all', this)\">全部交易</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('tp', this)\">止盈盈利 💰</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('sl', this)\">止损亏损 🛑</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('long', this)\">多单 🟢</button>");
            sb.AppendLine("                <button class=\"filter-btn\" onclick=\"setFilter('short', this)\">空单 🔴</button>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div class=\"table-wrapper\">");
            sb.AppendLine("            <table id=\"tradesTable\">");
            sb.AppendLine("                <thead>");
            sb.AppendLine("                    <tr>");
            sb.AppendLine("                        <th>#</th>");
            sb.AppendLine("                        <th>开仓方向</th>");
            sb.AppendLine("                        <th>开仓时间</th>");
            sb.AppendLine("                        <th>开仓价格</th>");
            sb.AppendLine("                        <th>目标止盈价</th>");
            sb.AppendLine("                        <th>目标止损价</th>");
            sb.AppendLine("                        <th>平仓时间</th>");
            sb.AppendLine("                        <th>平仓价格</th>");
            sb.AppendLine("                        <th>平仓类型</th>");
            sb.AppendLine("                        <th>净收益率 (%)</th>");
            sb.AppendLine("                        <th>持仓时长</th>");
            sb.AppendLine("                        <th>触发趋势线形态</th>");
            sb.AppendLine("                    </tr>");
            sb.AppendLine("                </thead>");
            sb.AppendLine("                <tbody>");

            for (int i = 0; i < trades.Count; i++)
            {
                var t = trades[i];
                string sideTag = t.Side == TradeSide.Buy ? "<span class=\"tag tag-long\">多单 BUY</span>" : "<span class=\"tag tag-short\">空单 SELL</span>";
                string typeTag = t.ExitReason == PositionExitReason.TakeProfit
                    ? "<span class=\"tag tag-tp\">💰 止盈平仓</span>"
                    : (t.ExitReason == PositionExitReason.StopLoss ? "<span class=\"tag tag-sl\">🛑 止损平仓</span>" : "<span class=\"tag tag-end\">🏁 周期结束</span>");

                string pnlClass = t.PnLPct >= 0 ? "text-green" : "text-red";
                string pnlTxt = $"{(t.PnLPct >= 0 ? "+" : "")}{t.PnLPct:F2}%";
                string durTxt = t.Duration.TotalHours >= 1
                    ? $"{t.Duration.Hours}h {t.Duration.Minutes}m {t.Duration.Seconds}s"
                    : $"{t.Duration.Minutes}m {t.Duration.Seconds}s";

                string filterType = t.ExitReason == PositionExitReason.TakeProfit ? "tp" : (t.ExitReason == PositionExitReason.StopLoss ? "sl" : "end");
                string filterSide = t.Side == TradeSide.Buy ? "long" : "short";

                sb.AppendLine($"                    <tr data-type=\"{filterType}\" data-side=\"{filterSide}\">");
                sb.AppendLine($"                        <td><b>#{t.TradeId}</b></td>");
                sb.AppendLine($"                        <td>{sideTag}</td>");
                sb.AppendLine($"                        <td>{t.EntryTime:yyyy-MM-dd HH:mm:ss}</td>");
                sb.AppendLine($"                        <td><b>{t.EntryPrice:F2}</b></td>");
                sb.AppendLine($"                        <td class=\"text-green\">{t.TakeProfitPrice:F2}</td>");
                sb.AppendLine($"                        <td class=\"text-red\">{t.StopLossPrice:F2}</td>");
                sb.AppendLine($"                        <td>{t.ExitTime:yyyy-MM-dd HH:mm:ss}</td>");
                sb.AppendLine($"                        <td><b>{t.ExitPrice:F2}</b></td>");
                sb.AppendLine($"                        <td>{typeTag}</td>");
                sb.AppendLine($"                        <td class=\"{pnlClass}\" style=\"font-weight:700;\">{pnlTxt}</td>");
                sb.AppendLine($"                        <td>{durTxt} ({t.HoldingBars} bars)</td>");
                sb.AppendLine($"                        <td style=\"font-size:11px; color:#94a3b8;\">#{t.TriggerLine.X1}->#{t.TriggerLine.X2} (跨度:{t.TriggerLine.LineX1X2}, 寿命:{t.TriggerLine.LineAge})</td>");
                sb.AppendLine("                    </tr>");
            }

            sb.AppendLine("                </tbody>");
            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Footer
            sb.AppendLine("    <div class=\"footer\">");
            sb.AppendLine("        <p>Binance Backtest Engine &copy; 2026. High Performance Parquet Streaming & Multi-Layer TrendLine Analytics.</p>");
            sb.AppendLine("    </div>");

            // JavaScript for Interactive Charts and Table Filters
            sb.AppendLine("</div>");
            sb.AppendLine("<script>");

            // Prepare chart data
            var chartLabels = string.Join(",", equityPoints.Select(p => $"\"{p.Time}\""));
            var chartData = string.Join(",", equityPoints.Select(p => p.Equity.ToString("F2", CultureInfo.InvariantCulture)));

            sb.AppendLine($@"
    // 初始化 Chart.js 资金曲线
    const ctx = document.getElementById('equityChart').getContext('2d');
    const gradient = ctx.createLinearGradient(0, 0, 0, 360);
    gradient.addColorStop(0, 'rgba(16, 185, 129, 0.4)');
    gradient.addColorStop(1, 'rgba(16, 185, 129, 0.0)');

    new Chart(ctx, {{
        type: 'line',
        data: {{
            labels: [{chartLabels}],
            datasets: [{{
                label: '累计收益率 (%)',
                data: [{chartData}],
                borderColor: '#10b981',
                backgroundColor: gradient,
                fill: true,
                tension: 0.2,
                pointRadius: 2,
                pointHoverRadius: 6,
                borderWidth: 2
            }}]
        }},
        options: {{
            responsive: true,
            maintainAspectRatio: false,
            plugins: {{
                legend: {{ display: false }},
                tooltip: {{
                    backgroundColor: '#1e293b',
                    titleColor: '#f8fafc',
                    bodyColor: '#10b981',
                    borderColor: '#334155',
                    borderWidth: 1,
                    callbacks: {{
                        label: function(context) {{
                            return '累计收益: ' + context.parsed.y.toFixed(2) + '%';
                        }}
                    }}
                }}
            }},
            scales: {{
                x: {{
                    grid: {{ color: '#334155' }},
                    ticks: {{ color: '#94a3b8', maxTicksLimit: 12 }}
                }},
                y: {{
                    grid: {{ color: '#334155' }},
                    ticks: {{
                        color: '#94a3b8',
                        callback: function(value) {{ return value.toFixed(1) + '%'; }}
                    }}
                }}
            }}
        }}
    }});

    // 表格筛选过滤
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
            const rowType = row.getAttribute('data-type');
            const rowSide = row.getAttribute('data-side');
            const text = row.innerText.toLowerCase();

            let matchFilter = false;
            if (currentFilter === 'all') matchFilter = true;
            else if (currentFilter === 'tp' && rowType === 'tp') matchFilter = true;
            else if (currentFilter === 'sl' && rowType === 'sl') matchFilter = true;
            else if (currentFilter === 'long' && rowSide === 'long') matchFilter = true;
            else if (currentFilter === 'short' && rowSide === 'short') matchFilter = true;

            const matchSearch = text.includes(search);
            row.style.display = (matchFilter && matchSearch) ? '' : 'none';
        }});
    }}
");
            sb.AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(outputFilePath, sb.ToString(), Encoding.UTF8);
            result.ReportHtmlPath = outputFilePath;
            Logger.Log($"[HtmlReportHelper] 专业量化回测分析报告已成功生成落盘: {outputFilePath}");

            return outputFilePath;
        }
    }
}
