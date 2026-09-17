using Common.Helper;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Drawing;
using Test.PercentageBar.WinForms.Engine;
using Test.PercentageBar.WinForms.Models;
using Color = ScottPlot.Color;

namespace Test.PercentageBar.WinForms.Helper
{
    public enum PercentChartType
    {
        Candlestick = 0,
        Line = 1
    }

    /// <summary>
    /// 百分比变化 K 线专属 ScottPlot 5 渲染帮助类 (纯序号 X 轴, 零时间依赖, 包含时间跨度可视化)
    /// </summary>
    public static class PercentPlotHelper
    {
        private static readonly string[] PreferredChineseFonts = new[]
        {
            "Microsoft YaHei", "微软雅黑", "PingFang SC", "SimHei", "Segoe UI", "Arial"
        };

        public static string GetSafeChineseFont()
        {
            try
            {
                var installedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var installedFontCollection = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (var family in installedFontCollection.Families)
                    {
                        installedFonts.Add(family.Name);
                    }
                }

                foreach (var font in PreferredChineseFonts)
                {
                    if (installedFonts.Contains(font)) return font;
                }
            }
            catch
            {
            }
            return "Microsoft YaHei";
        }

        /// <summary>
        /// 极速构建纯序号 X 轴的百分比 / 固定价格 K 线图表 (包含高低点位、ZigZag 波段与全局极值)
        /// </summary>
        public static void BuildPlot(
            Plot plot,
            IReadOnlyList<PercentageKline> bars,
            string coin,
            decimal thresholdValue,
            SliceUnitType sliceUnit = SliceUnitType.Percentage,
            PercentBarMode mode = PercentBarMode.ChangeFromOpen,
            PercentBarGenerationStats? stats = null,
            string title = "百分比 / 固定价格变化 K 线分析图",
            bool autoScaleAxes = true,
            int? selectedBarStartIndex = null,
            int? selectedBarEndIndex = null,
            PercentChartType chartType = PercentChartType.Candlestick,
            bool showPivots = true,
            bool showGlobalHighLow = true,
            bool showZigZag = true,
            bool showTrendLines = true,
            int pivotWindow = 3,
            TrendLine? selectedTrendLine = null,
            ConsecutiveRecentTrendLine? selectedRecentTrendLine = null,
            decimal touchTolerancePct = 0.0005m,
            bool showVolume = true,
            IReadOnlyList<Test.PercentageBar.WinForms.Engine.FirstTickTrade>? strategyTrades = null,
            bool showStrategyMarkers = false,
            bool showConsecutiveTrend = true,
            int consecutiveMinBars = 5,
            decimal consecutiveMinPct = 2.5m,
            bool showConsecutiveChannel = true,
            bool showRecentTrendLines = true,
            int recentTrendLineCount = 6,
            bool show1000BarInteraction = true,
            ConsecutiveTrendItem? selectedConsecutiveTrend = null,
            IReadOnlyList<ConsecutiveTrendItem>? consecutiveTrends = null)
        {
            if (plot == null || bars == null || bars.Count == 0)
            {
                if (plot != null)
                {
                    plot.Clear();
                    plot.Title("无可用 K 线数据");
                }
                return;
            }

            plot.Clear();

            string chineseFont = GetSafeChineseFont();

            // 1. 全局暗黑主题配置 (Slate 900)
            plot.FigureBackground.Color = Color.FromHex("#0f172a");
            plot.DataBackground.Color = Color.FromHex("#1e293b");

            plot.Grid.MajorLineColor = Color.FromHex("#334155").WithAlpha(0.65);
            plot.Grid.MinorLineColor = Color.FromHex("#1e293b").WithAlpha(0.4);

            plot.Axes.Left.TickLabelStyle.ForeColor = Color.FromHex("#94a3b8");
            plot.Axes.Bottom.TickLabelStyle.ForeColor = Color.FromHex("#94a3b8");
            plot.Axes.Left.FrameLineStyle.Color = Color.FromHex("#475569");
            plot.Axes.Bottom.FrameLineStyle.Color = Color.FromHex("#475569");

            int count = bars.Count;

            // 2. 绘制底部成交量柱状图 (Volume Histogram, 与 X 轴对齐, 绑定右侧 Y 轴，可自由勾选切换)
            if (showVolume)
            {
                var volumeBars = new List<ScottPlot.Bar>(count);
                double maxVolume = 0;
                for (int i = 0; i < count; i++)
                {
                    var b = bars[i];
                    double vol = (double)b.Volume;
                    if (vol > maxVolume) maxVolume = vol;

                    bool isBull = b.Close >= b.Open;
                    var volColor = isBull
                        ? Color.FromHex("#22c55e").WithAlpha(0.45)
                        : Color.FromHex("#ef4444").WithAlpha(0.45);

                    volumeBars.Add(new ScottPlot.Bar
                    {
                        Position = b.BarIndex,
                        Value = vol,
                        ValueBase = 0,
                        Size = 0.75,
                        FillColor = volColor,
                        LineWidth = 0
                    });
                }

                var volPlot = plot.Add.Bars(volumeBars);
                volPlot.Axes.YAxis = plot.Axes.Right;

                // 右侧 Y 轴配置 (成交量专用坐标轴, 颜色偏灰以防视觉喧宾夺主)
                plot.Axes.Right.TickLabelStyle.ForeColor = Color.FromHex("#64748b");
                plot.Axes.Right.FrameLineStyle.Color = Color.FromHex("#334155");
                plot.Axes.Right.Label.Text = "成交量 (Base Volume)";
                plot.Axes.Right.Label.FontName = chineseFont;
                plot.Axes.Right.Label.ForeColor = Color.FromHex("#64748b");
                plot.Axes.Right.Label.FontSize = 11;

                if (maxVolume <= 0) maxVolume = 1;
                plot.Axes.SetLimitsY(0, maxVolume * 3.8, plot.Axes.Right);
            }
            else
            {
                // 不显示成交量时，清空右侧 Y 轴标签
                plot.Axes.Right.Label.Text = string.Empty;
                plot.Axes.Right.TickLabelStyle.ForeColor = Color.FromHex("#1e293b");
                plot.Axes.Right.FrameLineStyle.Color = Color.FromHex("#1e293b");
            }

            // 3. 绘制主图 (蜡烛图 或 收盘折线)
            if (chartType == PercentChartType.Candlestick)
            {
                var ohlcList = new List<OHLC>(count);
                for (int i = 0; i < count; i++)
                {
                    var b = bars[i];
                    double x = b.BarIndex;
                    var ohlc = new OHLC(
                        (double)b.Open,
                        (double)b.High,
                        (double)b.Low,
                        (double)b.Close,
                        DateTime.FromOADate(x),
                        TimeSpan.FromDays(0.8));
                    ohlcList.Add(ohlc);
                }

                var candlePlot = plot.Add.Candlestick(ohlcList);
                candlePlot.RisingColor = Color.FromHex("#22c55e"); // 绿色阳线 (Green 500)
                candlePlot.FallingColor = Color.FromHex("#ef4444"); // 红色阴线 (Red 500)
            }
            else
            {
                double[] xs = new double[count];
                double[] ys = new double[count];

                for (int i = 0; i < count; i++)
                {
                    xs[i] = bars[i].BarIndex;
                    ys[i] = (double)bars[i].Close;
                }

                var linePlot = plot.Add.ScatterLine(xs, ys);
                linePlot.Color = Color.FromHex("#38bdf8"); // 亮天蓝 (Sky 400)
                linePlot.LineWidth = 1.2f;
                linePlot.MarkerSize = count > 300 ? 0 : 3;
            }

            // 4. 高低点位计算引擎与图表特征呈现 (Swing High / Swing Low / Global Extreme / ZigZag / TrendLines)
            var pivotResult = PivotDetector.CalculatePivots(bars, window: pivotWindow, alternateHighLow: true);

            // 4.1 绘制全局最高点与最低点水平参考线
            if (showGlobalHighLow && pivotResult.GlobalHigh.HasValue && pivotResult.GlobalLow.HasValue)
            {
                var gHigh = pivotResult.GlobalHigh.Value;
                var gLow = pivotResult.GlobalLow.Value;

                var lineHigh = plot.Add.HorizontalLine((double)gHigh.Price);
                lineHigh.LinePattern = LinePattern.Dashed;
                lineHigh.Color = Color.FromHex("#ef4444").WithAlpha(0.65);
                lineHigh.LineWidth = 0.8f;

                var lineLow = plot.Add.HorizontalLine((double)gLow.Price);
                lineLow.LinePattern = LinePattern.Dashed;
                lineLow.Color = Color.FromHex("#22c55e").WithAlpha(0.65);
                lineLow.LineWidth = 0.8f;
            }

            // 4.2 绘制 ZigZag 波段高低连线 (金黄色点划线)
            if (showZigZag && pivotResult.Pivots.Count > 1)
            {
                double[] zx = new double[pivotResult.Pivots.Count];
                double[] zy = new double[pivotResult.Pivots.Count];
                for (int k = 0; k < pivotResult.Pivots.Count; k++)
                {
                    zx[k] = pivotResult.Pivots[k].BarIndex;
                    zy[k] = (double)pivotResult.Pivots[k].Price;
                }
                var zigLine = plot.Add.ScatterLine(zx, zy);
                zigLine.Color = Color.FromHex("#f59e0b").WithAlpha(0.85); // Amber 500
                zigLine.LineWidth = 0.8f;
                zigLine.LinePattern = LinePattern.Dotted;
            }

            // 4.3 绘制高低点支撑/阻力趋势线 (Auto Trendlines & Rays，交互生成 & 穿透即删除，支持多达 1000 根)
            int activeTrendLineCount = 0;
            int activeResCount = 0;
            int activeSupCount = 0;
            int threePointCount = 0;

            if (showTrendLines)
            {
                var trendlines = PivotDetector.CalculateTrendLines(bars, pivotResult, maxLines: 1000, maxSpanBars: 1000, extensionBars: 8, strictWickPenetration: true, touchTolerancePct: touchTolerancePct);
                activeTrendLineCount = trendlines.Count;

                int drawnRes = 0;
                int drawnSup = 0;

                foreach (var tl in trendlines)
                {
                    bool isRes = tl.Type == TrendLineType.Resistance;
                    if (isRes) activeResCount++;
                    else activeSupCount++;

                    bool isThreePoint = tl.IsThreePointConfirmed;
                    if (isThreePoint) threePointCount++;

                    bool isLatest = (isRes && drawnRes < 2) || (!isRes && drawnSup < 2);
                    if (isRes) drawnRes++;
                    else drawnSup++;

                    bool isSelected = selectedTrendLine.HasValue && selectedTrendLine.Value.IsMatching(tl.StartBarIndex, tl.EndBarIndex, tl.Type);

                    if (isSelected)
                    {
                        // 🌟 选中趋势线专用高亮霓虹光晕 (Bright Sky Blue 38bdf8)
                        var glowLine = plot.Add.Line(tl.StartBarIndex, (double)tl.StartPrice, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        glowLine.Color = Color.FromHex("#38bdf8").WithAlpha(0.45);
                        glowLine.LineWidth = 2.4f;

                        var selMainLine = plot.Add.Line(tl.StartBarIndex, (double)tl.StartPrice, tl.EndBarIndex, (double)tl.EndPrice);
                        selMainLine.Color = Color.FromHex("#38bdf8");
                        selMainLine.LineWidth = 1.2f;

                        var selRayLine = plot.Add.Line(tl.EndBarIndex, (double)tl.EndPrice, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        selRayLine.Color = Color.FromHex("#38bdf8");
                        selRayLine.LineWidth = 0.8f;
                        selRayLine.LinePattern = LinePattern.Dashed;

                        // 关键高低点圆形锚点徽章 (触碰点全部标注)
                        if (tl.TouchBarIndices != null)
                        {
                            foreach (int bIdx in tl.TouchBarIndices)
                            {
                                decimal pY = tl.StartPrice + tl.Slope * (bIdx - tl.StartBarIndex);
                                var pMarker = plot.Add.Marker(bIdx, (double)pY);
                                pMarker.Shape = MarkerShape.FilledCircle;
                                pMarker.Size = 10;
                                pMarker.Color = isThreePoint ? Color.FromHex("#f472b6") : Color.FromHex("#38bdf8");
                            }
                        }
                        else
                        {
                            var p1Marker = plot.Add.Marker(tl.StartBarIndex, (double)tl.StartPrice);
                            p1Marker.Shape = MarkerShape.FilledCircle;
                            p1Marker.Size = 10;
                            p1Marker.Color = Color.FromHex("#38bdf8");

                            var p2Marker = plot.Add.Marker(tl.EndBarIndex, (double)tl.EndPrice);
                            p2Marker.Shape = MarkerShape.FilledCircle;
                            p2Marker.Size = 10;
                            p2Marker.Color = Color.FromHex("#38bdf8");
                        }

                        // 选中线端点特别徽章
                        string tagPrefix = isThreePoint ? $"🌸【{tl.TouchCount}点共线强" : "★ 选中【";
                        string selTagText = $"{tagPrefix}{(isRes ? "阻力" : "支撑")}】最新映射: {tl.CurrentBarPrice:F2} USDT";
                        var selTag = plot.Add.Text(selTagText, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        selTag.LabelFontName = chineseFont;
                        selTag.LabelFontSize = 9.5f;
                        selTag.LabelFontColor = Color.FromHex("#f8fafc");
                        selTag.LabelAlignment = isRes ? Alignment.LowerLeft : Alignment.UpperLeft;
                        selTag.LabelBackgroundColor = isThreePoint ? Color.FromHex("#831843").WithAlpha(0.95) : Color.FromHex("#0284c7").WithAlpha(0.95);
                        selTag.LabelBorderColor = isThreePoint ? Color.FromHex("#f472b6") : Color.FromHex("#38bdf8");
                        selTag.LabelBorderWidth = 1.5f;
                        continue;
                    }

                    if (isThreePoint)
                    {
                        // 🌸 3点及以上共线强趋势线：粉红色呈现 (Pink 400)，线宽统一 0.9f
                        var pinkColor = Color.FromHex("#f472b6");

                        var mainLine = plot.Add.Line(tl.StartBarIndex, (double)tl.StartPrice, tl.EndBarIndex, (double)tl.EndPrice);
                        mainLine.Color = pinkColor;
                        mainLine.LineWidth = 0.9f;
                        mainLine.LinePattern = LinePattern.Solid;

                        if (tl.ExtendedBarIndex > tl.EndBarIndex)
                        {
                            var rayLine = plot.Add.Line(tl.EndBarIndex, (double)tl.EndPrice, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                            rayLine.Color = pinkColor;
                            rayLine.LineWidth = 0.8f;
                            rayLine.LinePattern = LinePattern.Dashed;
                        }

                        if (tl.TouchBarIndices != null)
                        {
                            foreach (int bIdx in tl.TouchBarIndices)
                            {
                                decimal pY = tl.StartPrice + tl.Slope * (bIdx - tl.StartBarIndex);
                                var touchMarker = plot.Add.Marker(bIdx, (double)pY);
                                touchMarker.Shape = MarkerShape.FilledDiamond;
                                touchMarker.Size = 7;
                                touchMarker.Color = pinkColor;
                            }
                        }

                        string prefix = isRes ? "🌸3点阻力" : "🌸3点支撑";
                        string tagText = tl.IsBroken
                            ? $"{prefix}({tl.TouchCount}点破@Bar#{tl.BreakBarIndex}): {tl.ExtendedPrice:F2}"
                            : $"{prefix}({tl.TouchCount}点): {tl.CurrentBarPrice:F2}";

                        var tag = plot.Add.Text(tagText, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        tag.LabelFontName = chineseFont;
                        tag.LabelFontSize = 8.5f;
                        tag.LabelFontColor = Color.FromHex("#fff1f2");
                        tag.LabelAlignment = isRes ? Alignment.LowerLeft : Alignment.UpperLeft;
                        tag.LabelBackgroundColor = Color.FromHex("#831843").WithAlpha(0.92);
                        tag.LabelBorderColor = pinkColor;
                        tag.LabelBorderWidth = 1f;
                    }
                    else
                    {
                        var lineColor = isRes
                            ? (isLatest ? Color.FromHex("#ef4444") : Color.FromHex("#f87171").WithAlpha(0.35))
                            : (isLatest ? Color.FromHex("#22c55e") : Color.FromHex("#4ade80").WithAlpha(0.35));

                        var mainLine = plot.Add.Line(tl.StartBarIndex, (double)tl.StartPrice, tl.EndBarIndex, (double)tl.EndPrice);
                        mainLine.Color = lineColor;
                        mainLine.LineWidth = 0.8f;
                        mainLine.LinePattern = LinePattern.Solid;

                        if (tl.ExtendedBarIndex > tl.EndBarIndex)
                        {
                            var rayLine = plot.Add.Line(tl.EndBarIndex, (double)tl.EndPrice, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                            rayLine.Color = lineColor;
                            rayLine.LineWidth = 0.8f;
                            rayLine.LinePattern = LinePattern.Dashed;
                        }

                        if (isLatest || trendlines.Count <= 20)
                        {
                            string typeName = isRes ? "阻力" : "支撑";
                            string tagText = $"{typeName}: {tl.CurrentBarPrice:F2}";

                            var tag = plot.Add.Text(tagText, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                            tag.LabelFontName = chineseFont;
                            tag.LabelFontSize = 8.0f;
                            tag.LabelFontColor = isRes ? Color.FromHex("#fca5a5") : Color.FromHex("#86efac");
                            tag.LabelAlignment = isRes ? Alignment.LowerLeft : Alignment.UpperLeft;
                            tag.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                            tag.LabelBorderColor = isRes ? Color.FromHex("#ef4444") : Color.FromHex("#22c55e");
                            tag.LabelBorderWidth = 0.8f;
                        }
                    }
                }
            }

            // 4.4 绘制局部高低点位标记 (红色正三角 ▲ 表示最高，红色倒三角 ▼ 表示最低)
            if (showPivots && pivotResult.Pivots.Count > 0)
            {
                foreach (var p in pivotResult.Pivots)
                {
                    bool isHigh = p.Type == PivotPointType.High;
                    double x = p.BarIndex;
                    double y = (double)p.Price;

                    // 红色正三角 (▲) 与 红色倒三角 (▼)
                    var marker = plot.Add.Marker(x, y);
                    marker.Shape = isHigh ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                    marker.Size = 9;
                    marker.Color = Color.FromHex("#ef4444"); // 统一红色三角表示极值

                    // 价格文字标签 (点数较少或为全局极值时显示文字标签)
                    if (count <= 250 || p.IsGlobalExtreme)
                    {
                        string prefix = p.IsGlobalExtreme
                            ? (isHigh ? "👑最高 " : "👑最低 ")
                            : (isHigh ? "▲ " : "▼ ");
                        string labelText = $"{prefix}{p.Price:F2}";

                        var text = plot.Add.Text(labelText, x, y);
                        text.LabelFontName = chineseFont;
                        text.LabelFontSize = 8.5f;
                        text.LabelFontColor = isHigh ? Color.FromHex("#fca5a5") : Color.FromHex("#86efac");
                        text.LabelAlignment = isHigh ? Alignment.LowerCenter : Alignment.UpperCenter;
                        text.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                        text.LabelBorderColor = isHigh ? Color.FromHex("#ef4444") : Color.FromHex("#22c55e");
                        text.LabelBorderWidth = 1f;
                    }
                }
            }

            // 4.5 绘制首 Tick 动量策略开仓与平仓信号标记 (支持跨 Bar 轨迹连线)
            if (showStrategyMarkers && strategyTrades != null && strategyTrades.Count > 0)
            {
                foreach (var trade in strategyTrades)
                {
                    if (trade.EntryBarIndex < 0 || trade.EntryBarIndex >= count) continue;

                    bool isLong = trade.Side == Common.Models.TradeSide.Buy;
                    var entryMarker = plot.Add.Marker(trade.EntryBarIndex, (double)trade.EntryPrice);
                    entryMarker.Shape = isLong ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                    entryMarker.Size = 7;
                    entryMarker.Color = isLong ? Color.FromHex("#22c55e") : Color.FromHex("#ef4444");

                    if (trade.ExitBarIndex >= 0 && trade.ExitBarIndex < count)
                    {
                        var exitMarker = plot.Add.Marker(trade.ExitBarIndex, (double)trade.ExitPrice);
                        exitMarker.Shape = trade.ExitReason switch
                        {
                            Common.Models.PositionExitReason.TakeProfit => MarkerShape.FilledCircle,
                            Common.Models.PositionExitReason.StopLoss => MarkerShape.Cross,
                            Common.Models.PositionExitReason.SignalReversal => MarkerShape.FilledDiamond,
                            _ => MarkerShape.OpenCircle
                        };
                        exitMarker.Size = 6;
                        exitMarker.Color = trade.ExitReason switch
                        {
                            Common.Models.PositionExitReason.TakeProfit => Color.FromHex("#22c55e"),
                            Common.Models.PositionExitReason.StopLoss => Color.FromHex("#ef4444"),
                            Common.Models.PositionExitReason.SignalReversal => Color.FromHex("#f59e0b"),
                            _ => Color.FromHex("#94a3b8")
                        };

                        // 若跨越了多根 Bar，绘制虚线连线展示持仓生命周期轨迹
                        if (trade.EntryBarIndex != trade.ExitBarIndex)
                        {
                            var tradePath = plot.Add.Line(trade.EntryBarIndex, (double)trade.EntryPrice, trade.ExitBarIndex, (double)trade.ExitPrice);
                            tradePath.LinePattern = LinePattern.Dotted;
                            tradePath.LineWidth = 1.2f;
                            tradePath.Color = trade.IsWin ? Color.FromHex("#22c55e").WithAlpha(0.6) : Color.FromHex("#ef4444").WithAlpha(0.6);
                        }
                    }
                }
            }

            // 4.6 绘制连续上涨 / 连续下跌平行通道 (绿色 0.8f) 与形态标注
            if (showConsecutiveTrend)
            {
                var trends = consecutiveTrends ?? ConsecutiveTrendDetector.ScanTrends(bars, count - 1, consecutiveMinBars, consecutiveMinPct, recentTrendLineCount, touchTolerancePct: touchTolerancePct);
                var greenColor = Color.FromHex("#10b981"); // Emerald 500

                for (int tIdx = 0; tIdx < trends.Count; tIdx++)
                {
                    var tr = trends[tIdx];
                    int sIdx = tr.StartIndex;
                    int eIdx = tr.EndIndex;
                    if (sIdx < 0 || eIdx >= count || sIdx > eIdx) continue;

                    bool isBullish = tr.Type == ConsecutiveTrendType.Bullish;
                    bool isSelected = selectedConsecutiveTrend != null && selectedConsecutiveTrend.Id == tr.Id;

                    if (showConsecutiveChannel && tr.HasChannel)
                    {
                        decimal slopeK = tr.SlopeK;
                        decimal upperB = tr.UpperIntercept;
                        decimal lowerB = tr.LowerIntercept;

                        double yUpStart = (double)(slopeK * sIdx + upperB);
                        double yUpEnd = (double)(slopeK * eIdx + upperB);
                        double yLowStart = (double)(slopeK * sIdx + lowerB);
                        double yLowEnd = (double)(slopeK * eIdx + lowerB);
                        double yMidStart = (double)(slopeK * sIdx + (upperB + lowerB) / 2m);
                        double yMidEnd = (double)(slopeK * eIdx + (upperB + lowerB) / 2m);

                        // ① 平行通道内部半透明绿色微光填充
                        var channelCoords = new Coordinates[]
                        {
                            new Coordinates(sIdx, yUpStart),
                            new Coordinates(eIdx, yUpEnd),
                            new Coordinates(eIdx, yLowEnd),
                            new Coordinates(sIdx, yLowStart)
                        };
                        var channelPoly = plot.Add.Polygon(channelCoords);
                        channelPoly.FillColor = greenColor.WithAlpha(isSelected ? (byte)48 : (byte)22);
                        channelPoly.LineWidth = 0;

                        // ② 上通道线 (绿色，线宽 0.8f，选中时 1.5f)
                        var lineUp = plot.Add.Line(sIdx, yUpStart, eIdx, yUpEnd);
                        lineUp.Color = greenColor;
                        lineUp.LineWidth = isSelected ? 1.5f : 0.8f;
                        lineUp.LinePattern = LinePattern.Solid;

                        // ③ 下通道线 (绿色，线宽 0.8f，选中时 1.5f)
                        var lineLow = plot.Add.Line(sIdx, yLowStart, eIdx, yLowEnd);
                        lineLow.Color = greenColor;
                        lineLow.LineWidth = isSelected ? 1.5f : 0.8f;
                        lineLow.LinePattern = LinePattern.Solid;

                        // ④ 中通道线 (绿色半透明虚线，线宽 0.8f)
                        var lineMid = plot.Add.Line(sIdx, yMidStart, eIdx, yMidEnd);
                        lineMid.Color = greenColor.WithAlpha(isSelected ? (byte)210 : (byte)150);
                        lineMid.LineWidth = 0.8f;
                        lineMid.LinePattern = LinePattern.Dashed;

                        // ⑤ 确立点三角标记 (在首次达标的第 5 根 K 线打上显式三角形标记)
                        int cIdx = tr.ConfirmedBarIndex;
                        if (cIdx >= sIdx && cIdx < count)
                        {
                            double yConf = (double)bars[cIdx].Close;
                            var confMarker = plot.Add.Marker(cIdx, yConf);
                            confMarker.Shape = isBullish ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                            confMarker.Size = isSelected ? 10 : 8;
                            confMarker.Color = isSelected ? Color.FromHex("#fbbf24") : greenColor;
                        }

                        // ⑥ 向右延伸适量虚线方便观察后续突破
                        int extEnd = Math.Min(count - 1, eIdx + 6);
                        if (extEnd > eIdx)
                        {
                            double yUpExt = (double)(slopeK * extEnd + upperB);
                            double yLowExt = (double)(slopeK * extEnd + lowerB);

                            var extUp = plot.Add.Line(eIdx, yUpEnd, extEnd, yUpExt);
                            extUp.Color = greenColor.WithAlpha(130);
                            extUp.LineWidth = 0.8f;
                            extUp.LinePattern = LinePattern.Dashed;

                            var extLow = plot.Add.Line(eIdx, yLowEnd, extEnd, yLowExt);
                            extLow.Color = greenColor.WithAlpha(130);
                            extLow.LineWidth = 0.8f;
                            extLow.LinePattern = LinePattern.Dashed;
                        }

                        // ⑦ 四个角点微小圆点标记
                        var m1 = plot.Add.Marker(sIdx, yUpStart); m1.Shape = MarkerShape.FilledCircle; m1.Size = isSelected ? 6 : 4; m1.Color = greenColor;
                        var m2 = plot.Add.Marker(sIdx, yLowStart); m2.Shape = MarkerShape.FilledCircle; m2.Size = isSelected ? 6 : 4; m2.Color = greenColor;
                        var m3 = plot.Add.Marker(eIdx, yUpEnd); m3.Shape = MarkerShape.FilledCircle; m3.Size = isSelected ? 6 : 4; m3.Color = greenColor;
                        var m4 = plot.Add.Marker(eIdx, yLowEnd); m4.Shape = MarkerShape.FilledCircle; m4.Size = isSelected ? 6 : 4; m4.Color = greenColor;

                        // ⑧ 文字气泡标签标注在通道外沿
                        int curCount = eIdx - sIdx + 1;
                        decimal curPct = tr.StartPrice > 0 ? (bars[eIdx].Close - tr.StartPrice) / tr.StartPrice * 100m : 0m;
                        string baseTag = isBullish ? $"🔥连涨 {curCount}根 (+{curPct:F1}%)" : $"❄️连跌 {curCount}根 ({curPct:F1}%)";
                        string tag = isSelected ? $"⭐ [已选中] {baseTag}" : baseTag;

                        double midX = (sIdx + eIdx) / 2.0;
                        double textY = isBullish
                            ? (double)(slopeK * (decimal)midX + upperB)
                            : (double)(slopeK * (decimal)midX + lowerB);

                        var txt = plot.Add.Text(tag, midX, textY);
                        txt.LabelFontName = chineseFont;
                        txt.LabelFontColor = isSelected ? Color.FromHex("#34d399") : greenColor;
                        txt.LabelFontSize = isSelected ? 10f : 9.5f;
                        txt.LabelBold = true;
                        txt.LabelAlignment = isBullish ? Alignment.LowerCenter : Alignment.UpperCenter;
                        txt.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                        txt.LabelBorderColor = isSelected ? Color.FromHex("#34d399") : greenColor.WithAlpha(180);
                        txt.LabelBorderWidth = 1f;
                    }
                    else
                    {
                        // 简易形态标记模式 (未开启通道时)
                        var themeColor = isBullish ? Color.FromHex("#10b981") : Color.FromHex("#f43f5e");
                        double pStart = (double)bars[sIdx].Close;
                        double pEnd = (double)bars[eIdx].Close;
                        var trendLine = plot.Add.Line(sIdx, pStart, eIdx, pEnd);
                        trendLine.Color = themeColor.WithAlpha(200);
                        trendLine.LineWidth = 1.5f;
                        trendLine.LinePattern = LinePattern.Dashed;

                        int cIdx = tr.ConfirmedBarIndex;
                        if (cIdx >= sIdx && cIdx < count)
                        {
                            var confMarker = plot.Add.Marker(cIdx, (double)bars[cIdx].Close);
                            confMarker.Shape = isBullish ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                            confMarker.Size = 8;
                            confMarker.Color = themeColor;
                        }

                        int curCount = eIdx - sIdx + 1;
                        decimal curPct = tr.StartPrice > 0 ? (bars[eIdx].Close - tr.StartPrice) / tr.StartPrice * 100m : 0m;
                        string tag = isBullish ? $"🔥连涨 {curCount}根 (+{curPct:F1}%)" : $"❄️连跌 {curCount}根 ({curPct:F1}%)";
                        var txt = plot.Add.Text(tag, (sIdx + eIdx) / 2.0, Math.Max(pStart, pEnd));
                        txt.LabelFontName = chineseFont;
                        txt.LabelFontColor = themeColor;
                        txt.LabelFontSize = 9.0f;
                        txt.LabelBold = true;
                        txt.LabelAlignment = Alignment.LowerCenter;
                        txt.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                    }

                    // ⑨ 绘制本次连涨/连跌附近立体多维近期趋势线网
                    if (showRecentTrendLines)
                    {
                        var linesToDraw = tr.RecentTrendLines != null && tr.RecentTrendLines.Count > 0
                            ? tr.RecentTrendLines
                            : null;

                        if (linesToDraw != null && linesToDraw.Count > 0)
                        {
                            int drawLimit = Math.Min(linesToDraw.Count, recentTrendLineCount);
                            for (int lineIdx = 0; lineIdx < drawLimit; lineIdx++)
                            {
                                var rtl = linesToDraw[lineIdx];
                                bool isLineSelected = selectedRecentTrendLine != null &&
                                                      selectedRecentTrendLine.StartX == rtl.StartX &&
                                                      selectedRecentTrendLine.EndX == rtl.EndX &&
                                                      selectedRecentTrendLine.LineType == rtl.LineType;

                                var lineColor = isLineSelected
                                    ? (rtl.HasThirdPointTouch ? Color.FromHex("#f472b6") : Color.FromHex("#fef08a"))
                                    : Color.FromHex(rtl.ColorHex);

                                float lWidth = isLineSelected ? 2.4f : (isSelected ? (rtl.LineWidth * 1.3f) : rtl.LineWidth);

                                // 1. 实体锚点线段 (波段内部或前序结构段)
                                var lineMain = plot.Add.Line(rtl.StartX, (double)rtl.StartY, rtl.EndX, (double)rtl.EndY);
                                lineMain.Color = lineColor.WithAlpha(isLineSelected ? (byte)255 : (isSelected ? (byte)255 : (byte)210));
                                lineMain.LineWidth = lWidth;
                                lineMain.LinePattern = rtl.LineType == ConsecutiveRecentTrendLineType.StreakCenter ? LinePattern.Dotted : LinePattern.Solid;

                                // 2. 关键锚点小标记
                                var mkStart = plot.Add.Marker(rtl.StartX, (double)rtl.StartY);
                                mkStart.Shape = MarkerShape.FilledCircle;
                                mkStart.Size = isLineSelected ? 10 : (isSelected ? 7 : (rtl.IsPrimary ? 6 : 4));
                                mkStart.Color = lineColor;

                                var mkEnd = plot.Add.Marker(rtl.EndX, (double)rtl.EndY);
                                mkEnd.Shape = MarkerShape.FilledCircle;
                                mkEnd.Size = isLineSelected ? 10 : (isSelected ? 7 : (rtl.IsPrimary ? 6 : 4));
                                mkEnd.Color = lineColor;

                                // 🌸 3. 若满足第 3 点共线条件，在第 3 点处绘制粉红高亮菱形标记
                                if (rtl.HasThirdPointTouch && rtl.ThirdPointBarIndex >= 0)
                                {
                                    var mk3 = plot.Add.Marker(rtl.ThirdPointBarIndex, (double)rtl.ThirdPointPrice);
                                    mk3.Shape = MarkerShape.FilledDiamond;
                                    mk3.Size = isLineSelected ? 10 : 8;
                                    mk3.Color = Color.FromHex("#f472b6");
                                }

                                // 4. 向右延伸虚线射线 (零未来函数几何推演，不改变斜率)
                                int rEnd = Math.Min(count - 1, rtl.RayEndX);
                                if (rEnd > rtl.EndX)
                                {
                                    double rY = (double)(rtl.StartY + rtl.Slope * (rEnd - rtl.StartX));
                                    var lineRay = plot.Add.Line(rtl.EndX, (double)rtl.EndY, rEnd, rY);
                                    lineRay.Color = lineColor.WithAlpha(isLineSelected ? (byte)240 : (isSelected ? (byte)200 : (byte)130));
                                    lineRay.LineWidth = isLineSelected ? 1.2f : 0.8f;
                                    lineRay.LinePattern = LinePattern.Dashed;

                                    // 主干核心线、选中形态或线条少时绘制端点气泡标签，次要线省略标签避免遮挡画面
                                    if (!string.IsNullOrEmpty(rtl.TagText) && (rtl.IsPrimary || isSelected || isLineSelected || drawLimit <= 4))
                                    {
                                        var tagTxt = plot.Add.Text(rtl.TagText, rEnd, rY);
                                        tagTxt.LabelFontName = chineseFont;
                                        tagTxt.LabelFontColor = isLineSelected ? Color.FromHex("#ffffff") : lineColor;
                                        tagTxt.LabelFontSize = isLineSelected ? 9.0f : (isSelected ? 8.5f : 8.0f);
                                        tagTxt.LabelAlignment = isBullish ? Alignment.UpperLeft : Alignment.LowerLeft;
                                        tagTxt.LabelBackgroundColor = isLineSelected
                                            ? (rtl.HasThirdPointTouch ? Color.FromHex("#831843").WithAlpha(0.95) : Color.FromHex("#854d0e").WithAlpha(0.95))
                                            : Color.FromHex("#0f172a").WithAlpha(0.85);
                                        tagTxt.LabelBorderColor = lineColor;
                                        tagTxt.LabelBorderWidth = isLineSelected ? 1.5f : 0.8f;
                                    }
                                }

                                // 5. 若单线被选中，在中央显示选中标识
                                if (isLineSelected)
                                {
                                    double midX = (rtl.StartX + rtl.EndX) / 2.0;
                                    double midY = (double)(rtl.StartY + rtl.Slope * ((decimal)midX - rtl.StartX));
                                    var selBadge = plot.Add.Text($"⭐ [选中线: {rtl.Name}]", midX, midY);
                                    selBadge.LabelFontName = chineseFont;
                                    selBadge.LabelFontSize = 9.0f;
                                    selBadge.LabelFontColor = Color.FromHex("#ffffff");
                                    selBadge.LabelBackgroundColor = rtl.HasThirdPointTouch ? Color.FromHex("#831843").WithAlpha(0.95) : Color.FromHex("#854d0e").WithAlpha(0.95);
                                    selBadge.LabelBorderColor = lineColor;
                                    selBadge.LabelBorderWidth = 1.2f;
                                }
                            }
                        }
                        else
                        {
                            // 兜底兼容单线模式
                            var trendLineColor = isBullish ? Color.FromHex("#38bdf8") : Color.FromHex("#f43f5e");

                            if (tr.HasTrendLine)
                            {
                                var tlMain = plot.Add.Line(tr.TrendLineStartX, (double)tr.TrendLineStartY, tr.TrendLineEndX, (double)tr.TrendLineEndY);
                                tlMain.Color = trendLineColor;
                                tlMain.LineWidth = isSelected ? 1.8f : 1.2f;
                                tlMain.LinePattern = LinePattern.Solid;

                                var mk1 = plot.Add.Marker(tr.TrendLineStartX, (double)tr.TrendLineStartY);
                                mk1.Shape = MarkerShape.FilledCircle;
                                mk1.Size = isSelected ? 8 : 6;
                                mk1.Color = trendLineColor;

                                var mk2 = plot.Add.Marker(tr.TrendLineEndX, (double)tr.TrendLineEndY);
                                mk2.Shape = MarkerShape.FilledCircle;
                                mk2.Size = isSelected ? 8 : 6;
                                mk2.Color = trendLineColor;

                                int rayEndIdx = Math.Min(count - 1, tr.EndIndex + 8);
                                if (rayEndIdx > tr.TrendLineEndX)
                                {
                                    double rayEndY = (double)(tr.TrendLineEndY + tr.TrendLineSlope * (rayEndIdx - tr.TrendLineEndX));
                                    var tlRay = plot.Add.Line(tr.TrendLineEndX, (double)tr.TrendLineEndY, rayEndIdx, rayEndY);
                                    tlRay.Color = trendLineColor.WithAlpha(isSelected ? (byte)220 : (byte)150);
                                    tlRay.LineWidth = isSelected ? 1.2f : 0.8f;
                                    tlRay.LinePattern = LinePattern.Dashed;

                                    string tlTag = isBullish ? $"📈 连涨支撑: {rayEndY:F2}" : $"📉 连跌阻力: {rayEndY:F2}";
                                    var tlTxt = plot.Add.Text(tlTag, rayEndIdx, rayEndY);
                                    tlTxt.LabelFontName = chineseFont;
                                    tlTxt.LabelFontColor = trendLineColor;
                                    tlTxt.LabelFontSize = 8.5f;
                                    tlTxt.LabelAlignment = isBullish ? Alignment.UpperLeft : Alignment.LowerLeft;
                                    tlTxt.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                                    tlTxt.LabelBorderColor = trendLineColor;
                                    tlTxt.LabelBorderWidth = 0.8f;
                                }
                            }

                            if (tr.HasPrecedingTrendLine)
                            {
                                var amberColor = Color.FromHex("#fbbf24");
                                var precMain = plot.Add.Line(tr.PrecedingTrendStartX, (double)tr.PrecedingTrendStartY, tr.PrecedingTrendEndX, (double)tr.PrecedingTrendEndY);
                                precMain.Color = amberColor.WithAlpha(180);
                                precMain.LineWidth = 1.0f;
                                precMain.LinePattern = LinePattern.Solid;

                                var pm1 = plot.Add.Marker(tr.PrecedingTrendStartX, (double)tr.PrecedingTrendStartY);
                                pm1.Shape = MarkerShape.FilledCircle;
                                pm1.Size = 5;
                                pm1.Color = amberColor;

                                var pm2 = plot.Add.Marker(tr.PrecedingTrendEndX, (double)tr.PrecedingTrendEndY);
                                pm2.Shape = MarkerShape.FilledCircle;
                                pm2.Size = 5;
                                pm2.Color = amberColor;

                                if (tr.StartIndex > tr.PrecedingTrendEndX)
                                {
                                    double yBreak = (double)(tr.PrecedingTrendEndY + tr.PrecedingTrendSlope * (tr.StartIndex - tr.PrecedingTrendEndX));
                                    var precRay = plot.Add.Line(tr.PrecedingTrendEndX, (double)tr.PrecedingTrendEndY, tr.StartIndex, yBreak);
                                    precRay.Color = amberColor.WithAlpha(140);
                                    precRay.LineWidth = 0.8f;
                                    precRay.LinePattern = LinePattern.Dotted;

                                    string precTag = isBullish ? "⚡ 突破前序阻力" : "⚡ 跌破前序支撑";
                                    var precTxt = plot.Add.Text(precTag, tr.StartIndex, yBreak);
                                    precTxt.LabelFontName = chineseFont;
                                    precTxt.LabelFontColor = amberColor;
                                    precTxt.LabelFontSize = 8.0f;
                                    precTxt.LabelAlignment = isBullish ? Alignment.LowerLeft : Alignment.UpperLeft;
                                    precTxt.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                                    precTxt.LabelBorderColor = amberColor.WithAlpha(180);
                                    precTxt.LabelBorderWidth = 0.8f;
                                }
                            }
                        }
                    }

                    // ⑩ 绘制连涨/连跌关联前1000根高低点位交互趋势线 (连涨交互高点，连跌交互低点)
                    if (show1000BarInteraction && tr.Macro1000BarLines != null && tr.Macro1000BarLines.Count > 0)
                    {
                        for (int mIdx = 0; mIdx < tr.Macro1000BarLines.Count; mIdx++)
                        {
                            var mtl = tr.Macro1000BarLines[mIdx];
                            bool isLineSelected = selectedRecentTrendLine != null &&
                                                  selectedRecentTrendLine.StartX == mtl.StartX &&
                                                  selectedRecentTrendLine.EndX == mtl.EndX &&
                                                  selectedRecentTrendLine.LineType == mtl.LineType;

                            var mColor = isLineSelected
                                ? (mtl.HasThirdPointTouch ? Color.FromHex("#f472b6") : Color.FromHex("#fef08a"))
                                : Color.FromHex(mtl.ColorHex);

                            float lWidth = isLineSelected ? 2.4f : (isSelected ? (mtl.LineWidth * 1.35f) : mtl.LineWidth);

                            // 1. 实体交互连线 (从历史千根锚点连接至当前波段最新 K 线)
                            var lineM = plot.Add.Line(mtl.StartX, (double)mtl.StartY, mtl.EndX, (double)mtl.EndY);
                            lineM.Color = mColor.WithAlpha(isLineSelected ? (byte)255 : (isSelected ? (byte)255 : (byte)215));
                            lineM.LineWidth = lWidth;
                            lineM.LinePattern = LinePattern.Solid;

                            // 2. 历史锚点标记 (圆形)
                            var mkStart = plot.Add.Marker(mtl.StartX, (double)mtl.StartY);
                            mkStart.Shape = MarkerShape.FilledCircle;
                            mkStart.Size = isLineSelected ? 10 : (isSelected ? 7 : (mtl.IsPrimary ? 6 : 4));
                            mkStart.Color = mColor;

                            // 3. 最新 K 线交互端点标记 (菱形 Diamond，突出交互焦点)
                            var mkEnd = plot.Add.Marker(mtl.EndX, (double)mtl.EndY);
                            mkEnd.Shape = MarkerShape.FilledDiamond;
                            mkEnd.Size = isLineSelected ? 11 : (isSelected ? 9 : (mtl.IsPrimary ? 7 : 5));
                            mkEnd.Color = mColor;

                            // 🌸 4. 若满足第 3 点共线条件，在第 3 点处绘制粉红高亮菱形标记
                            if (mtl.HasThirdPointTouch && mtl.ThirdPointBarIndex >= 0)
                            {
                                var mk3 = plot.Add.Marker(mtl.ThirdPointBarIndex, (double)mtl.ThirdPointPrice);
                                mk3.Shape = MarkerShape.FilledDiamond;
                                mk3.Size = isLineSelected ? 10 : 8;
                                mk3.Color = Color.FromHex("#f472b6");
                            }

                            // 5. 向右射线轻微延伸 (预测参考)
                            int rEnd = Math.Min(count - 1, mtl.RayEndX);
                            if (rEnd > mtl.EndX)
                            {
                                double rY = (double)(mtl.StartY + mtl.Slope * (rEnd - mtl.StartX));
                                var lineRay = plot.Add.Line(mtl.EndX, (double)mtl.EndY, rEnd, rY);
                                lineRay.Color = mColor.WithAlpha(isLineSelected ? (byte)240 : (isSelected ? (byte)190 : (byte)120));
                                lineRay.LineWidth = isLineSelected ? 1.2f : 0.8f;
                                lineRay.LinePattern = LinePattern.Dashed;
                            }

                            // 6. 气泡标签显示 (高点交互置于上方，低点交互置于下方)
                            if (!string.IsNullOrEmpty(mtl.TagText))
                            {
                                var tagTxt = plot.Add.Text(mtl.TagText, mtl.EndX, (double)mtl.EndY);
                                tagTxt.LabelFontName = chineseFont;
                                tagTxt.LabelFontColor = isLineSelected ? Color.FromHex("#ffffff") : mColor;
                                tagTxt.LabelFontSize = isLineSelected ? 9.0f : (isSelected ? 8.5f : 8.0f);
                                tagTxt.LabelAlignment = isBullish ? Alignment.UpperRight : Alignment.LowerRight;
                                tagTxt.LabelBackgroundColor = isLineSelected
                                    ? (mtl.HasThirdPointTouch ? Color.FromHex("#831843").WithAlpha(0.95) : Color.FromHex("#854d0e").WithAlpha(0.95))
                                    : Color.FromHex("#090d16").WithAlpha(0.88);
                                tagTxt.LabelBorderColor = mColor;
                                tagTxt.LabelBorderWidth = isLineSelected ? 1.5f : 0.8f;
                            }

                            // 7. 若单线被选中，在中央显示选中标识
                            if (isLineSelected)
                            {
                                double midX = (mtl.StartX + mtl.EndX) / 2.0;
                                double midY = (double)(mtl.StartY + mtl.Slope * ((decimal)midX - mtl.StartX));
                                var selBadge = plot.Add.Text($"⭐ [选中线: {mtl.Name}]", midX, midY);
                                selBadge.LabelFontName = chineseFont;
                                selBadge.LabelFontSize = 9.0f;
                                selBadge.LabelFontColor = Color.FromHex("#ffffff");
                                selBadge.LabelBackgroundColor = mtl.HasThirdPointTouch ? Color.FromHex("#831843").WithAlpha(0.95) : Color.FromHex("#854d0e").WithAlpha(0.95);
                                selBadge.LabelBorderColor = mColor;
                                selBadge.LabelBorderWidth = 1.2f;
                            }
                        }
                    }
                }
            }

            // 5. 绘制用户点击选中的单根或 Shift 连续多根 K 线高亮选区
            if (selectedBarStartIndex.HasValue)
            {
                int sIdx = Math.Clamp(selectedBarStartIndex.Value, 0, count - 1);
                int eIdx = Math.Clamp(selectedBarEndIndex ?? sIdx, 0, count - 1);
                int minB = Math.Min(sIdx, eIdx);
                int maxB = Math.Max(sIdx, eIdx);

                if (minB == maxB)
                {
                    var selBar = bars[minB];
                    double yLow = (double)selBar.Low;
                    double yHigh = (double)selBar.High;
                    double yClose = (double)selBar.Close;

                    if (yLow == yHigh)
                    {
                        yLow = yClose * 0.999;
                        yHigh = yClose * 1.001;
                    }

                    // 垂直光标
                    var cursorLine = plot.Add.Line(minB, yLow, minB, yHigh);
                    cursorLine.Color = Color.FromHex("#38bdf8");
                    cursorLine.LineWidth = 3.0f;

                    // 高亮点
                    var cursorPoint = plot.Add.Scatter(new double[] { minB }, new double[] { yClose });
                    cursorPoint.MarkerShape = MarkerShape.FilledCircle;
                    cursorPoint.MarkerSize = 10;
                    cursorPoint.Color = Color.FromHex("#38bdf8");
                    cursorPoint.LineWidth = 0;
                }
                else
                {
                    // Shift 连续多选：绘制霓虹半透明选区矩形与文字徽章
                    decimal rangeMinLow = decimal.MaxValue;
                    decimal rangeMaxHigh = decimal.MinValue;
                    for (int k = minB; k <= maxB; k++)
                    {
                        if (bars[k].Low < rangeMinLow) rangeMinLow = bars[k].Low;
                        if (bars[k].High > rangeMaxHigh) rangeMaxHigh = bars[k].High;
                    }

                    var selBox = plot.Add.Rectangle(minB - 0.45, maxB + 0.45, (double)rangeMinLow, (double)rangeMaxHigh);
                    selBox.FillColor = Color.FromHex("#38bdf8").WithAlpha(0.18);
                    selBox.LineColor = Color.FromHex("#38bdf8").WithAlpha(0.85);
                    selBox.LineWidth = 1.5f;
                    selBox.LinePattern = LinePattern.Dashed;

                    var selTag = plot.Add.Text($"★ 选中连续 {maxB - minB + 1} 根 Bar (#{minB} ~ #{maxB})", (minB + maxB) / 2.0, (double)rangeMaxHigh);
                    selTag.LabelFontName = chineseFont;
                    selTag.LabelFontSize = 9.0f;
                    selTag.LabelFontColor = Color.FromHex("#38bdf8");
                    selTag.LabelAlignment = Alignment.LowerCenter;
                    selTag.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.9);
                    selTag.LabelBorderColor = Color.FromHex("#38bdf8");
                    selTag.LabelBorderWidth = 1f;
                }
            }

            // 6. 左上角结构化摘要指标卡片
            DateTime firstTime = bars[0].OpenDateTime;
            DateTime lastTime = bars[count - 1].CloseDateTime;
            string timeRange = $"{firstTime:yyyy-MM-dd HH:mm:ss} ~ {lastTime:yyyy-MM-dd HH:mm:ss} (UTC+0)";

            string avgDurationStr = stats != null ? FormatTimeSpan(stats.AverageBarDuration) : "--";
            string minDurationStr = stats != null ? FormatTimeSpan(stats.MinBarDuration) : "--";
            string maxDurationStr = stats != null ? FormatTimeSpan(stats.MaxBarDuration) : "--";
            string avgTicksStr = stats != null ? $"{stats.AverageTicksPerBar:F0} 笔/Bar" : "--";
            string throughputStr = stats != null ? $"{stats.TicksPerSecond:N0} ticks/s (耗时 {stats.ElapsedMilliseconds} ms)" : "";

            string modeStr = mode switch
            {
                PercentBarMode.ChangeFromOpen => "开盘基准涨跌 (From Open)",
                PercentBarMode.HighLowRange => "极值全振幅 (High-Low Range)",
                PercentBarMode.Renko => "Renko 趋势砖块",
                _ => "切分模式"
            };

            string sliceUnitStr = sliceUnit switch
            {
                SliceUnitType.Percentage => $"涨跌幅 ±{thresholdValue:F2}%",
                SliceUnitType.FixedPrice => $"固定价差 ±{thresholdValue:F2} USDT",
                SliceUnitType.MinuteTime => $"分钟周期 {thresholdValue:F0} 分钟 ({(thresholdValue >= 60 ? (thresholdValue / 60) + "h" : thresholdValue + "m")})",
                _ => $"周期 {thresholdValue}"
            };

            string headerTitle = sliceUnit switch
            {
                SliceUnitType.Percentage => "【百分比 K 线特征指标】",
                SliceUnitType.FixedPrice => "【固定价格 K 线特征指标】",
                SliceUnitType.MinuteTime => "【分钟时间周期 K 线特征指标】",
                _ => "【K 线特征指标】"
            };

            string pivotSummary = pivotResult.GlobalHigh.HasValue
                ? $"• 高低极值: 👑最高={pivotResult.GlobalHigh.Value.Price:F2} (Bar #{pivotResult.GlobalHigh.Value.BarIndex}) | 👑最低={pivotResult.GlobalLow?.Price:F2} (Bar #{pivotResult.GlobalLow?.BarIndex}) | 识别波峰高点 {pivotResult.TotalHighPivots} 个, 波谷低点 {pivotResult.TotalLowPivots} 个\n"
                : "";

            string trendLineSummary = showTrendLines
                ? $"• 活跃有效趋势线: 共 {activeTrendLineCount} 根 (🟣3点+强共线: {threePointCount} 根 | 阻力: {activeResCount} 根, 支撑: {activeSupCount} 根 | 穿透K线已自动剔除)\n"
                : "";

            string summaryText = $"{headerTitle}\n" +
                                 $"• 交易对: {coin} | 切分基准: {sliceUnitStr} | 模式: {modeStr}\n" +
                                 $"• 真实时间跨度: {timeRange}\n" +
                                 $"• K线生成根数: {count:N0} 根 (纯序号 X 轴: Bar #0 -> #{count - 1})\n" +
                                 pivotSummary +
                                 trendLineSummary +
                                 $"• 单Bar时间跨度: 平均={avgDurationStr} | 最快突破={minDurationStr} | 最长盘整={maxDurationStr}\n" +
                                 $"• 单Bar涵盖量能: 平均 {avgTicksStr} | 价格范围: {bars[0].Close:F2} -> {bars[count - 1].Close:F2}\n" +
                                 (string.IsNullOrEmpty(throughputStr) ? "" : $"• 引擎生成性能: {throughputStr}");

            var annotation = plot.Add.Annotation(summaryText, Alignment.UpperLeft);
            annotation.LabelStyle.FontName = chineseFont;
            annotation.LabelStyle.FontSize = 11.5f;
            annotation.LabelStyle.ForeColor = Color.FromHex("#f8fafc");
            annotation.LabelStyle.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.9);
            annotation.LabelStyle.BorderColor = Color.FromHex("#475569");
            annotation.LabelStyle.BorderWidth = 1.5f;
            annotation.LabelStyle.ShadowColor = Colors.Transparent;

            // 7. 设置标题与坐标轴标签 (X 轴纯序号说明)
            plot.Title(title, size: 16);
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#f8fafc");

            string axisBottomLabel = sliceUnit == SliceUnitType.Percentage
                ? $"百分比 K 线序列号 (Bar Index, 价格每涨跌 ±{thresholdValue:F2}% 递增)"
                : $"固定价格 K 线序列号 (Bar Index, 价格每变化 ±{thresholdValue:F2} USDT 递增)";

            plot.Axes.Bottom.Label.Text = axisBottomLabel;
            plot.Axes.Bottom.Label.FontName = chineseFont;
            plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#cbd5e1");

            plot.Axes.Left.Label.Text = "价格 (Price USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.ForeColor = Color.FromHex("#cbd5e1");

            // 8. 隐藏外部图例栏，杜绝挤占图表顶部区域
            plot.HideLegend();

            if (autoScaleAxes)
            {
                plot.Axes.Margins(0.02, 0.08);
                plot.Axes.AutoScale();
            }
        }

        public static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}天{ts.Hours}时{ts.Minutes}分";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}时{ts.Minutes}分{ts.Seconds}秒";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}分{ts.Seconds}秒";
            if (ts.TotalSeconds >= 1) return $"{ts.Seconds}秒{ts.Milliseconds:D3}ms";
            return $"{ts.Milliseconds}ms";
        }
    }
}
