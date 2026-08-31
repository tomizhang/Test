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

        private static string GetSafeChineseFont()
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
            int? selectedBarIndex = null,
            PercentChartType chartType = PercentChartType.Candlestick,
            bool showPivots = true,
            bool showGlobalHighLow = true,
            bool showZigZag = true,
            bool showTrendLines = true,
            int pivotWindow = 3,
            TrendLine? selectedTrendLine = null,
            decimal touchTolerancePct = 0.0003m)
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

            // 2. 绘制底部成交量柱状图 (Volume Histogram, 与 X 轴对齐, 绑定右侧 Y 轴)
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

                var linePlot = plot.Add.Scatter(xs, ys);
                linePlot.Color = Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
                linePlot.LineWidth = 0.8f;
                linePlot.MarkerShape = MarkerShape.FilledCircle;
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
                                pMarker.Color = isThreePoint ? Color.FromHex("#c084fc") : Color.FromHex("#38bdf8");
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
                        string tagPrefix = isThreePoint ? $"🟣【{tl.TouchCount}点共线强" : "★ 选中【";
                        string brokenTag = tl.IsBroken ? " (已穿透/历史参考)" : "";
                        string selTypeName = isRes ? $"{tagPrefix}阻力趋势线{brokenTag}】" : $"{tagPrefix}支撑趋势线{brokenTag}】";
                        string selTagText = $"{selTypeName}\n起: Bar #{tl.StartBarIndex} (${tl.StartPrice:F2}) -> 锚: Bar #{tl.EndBarIndex} (${tl.EndPrice:F2})\n当前投影: ${tl.CurrentBarPrice:F2} | 斜率: {tl.Slope:+0.00;-0.00;0.00}/Bar";
                        var selTag = plot.Add.Text(selTagText, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        selTag.LabelFontName = chineseFont;
                        selTag.LabelFontSize = 9.5f;
                        selTag.LabelFontColor = Color.FromHex("#f8fafc");
                        selTag.LabelAlignment = isRes ? Alignment.LowerLeft : Alignment.UpperLeft;
                        selTag.LabelBackgroundColor = isThreePoint ? Color.FromHex("#581c87").WithAlpha(0.92) : Color.FromHex("#0369a1").WithAlpha(0.92);
                        selTag.LabelBorderColor = isThreePoint ? Color.FromHex("#c084fc") : Color.FromHex("#38bdf8");
                        selTag.LabelBorderWidth = 1.2f;
                    }
                    else if (isThreePoint)
                    {
                        // 🟣 3点及以上共线强趋势线专用紫色渲染 (Purple 400 / Violet c084fc，穿透不删除)
                        var purpleColor = tl.IsBroken ? Color.FromHex("#c084fc").WithAlpha(0.70) : Color.FromHex("#c084fc");

                        // 1. 主趋势线段 (全图统一线宽 0.8f)
                        var mainLine = plot.Add.Line(tl.StartBarIndex, (double)tl.StartPrice, tl.EndBarIndex, (double)tl.EndPrice);
                        mainLine.Color = purpleColor;
                        mainLine.LineWidth = 0.8f;
                        mainLine.LinePattern = LinePattern.Solid;

                        // 2. 向右延伸虚线射线 (全图统一线宽 0.8f，穿透时精准截断在穿透 Bar，不向未来无序延伸)
                        if (tl.ExtendedBarIndex > tl.EndBarIndex)
                        {
                            var rayLine = plot.Add.Line(tl.EndBarIndex, (double)tl.EndPrice, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                            rayLine.Color = purpleColor.WithAlpha(0.85);
                            rayLine.LineWidth = 0.8f;
                            rayLine.LinePattern = LinePattern.Dashed;
                        }

                        // 3. 在所有触碰极值点上绘制紫色菱形标记
                        if (tl.TouchBarIndices != null)
                        {
                            foreach (int bIdx in tl.TouchBarIndices)
                            {
                                decimal pY = tl.StartPrice + tl.Slope * (bIdx - tl.StartBarIndex);
                                var touchMarker = plot.Add.Marker(bIdx, (double)pY);
                                touchMarker.Shape = MarkerShape.FilledDiamond;
                                touchMarker.Size = 7;
                                touchMarker.Color = purpleColor;
                            }
                        }

                        // 4. 趋势线末端投影价格标签 (显示点数与穿透截断点)
                        string typeName = isRes ? "阻力" : "支撑";
                        string brokenMark = tl.IsBroken ? $"(破@Bar#{tl.BreakBarIndex})" : "";
                        string tagText = $"🟣{typeName}({tl.TouchCount}点{brokenMark}): {tl.ExtendedPrice:F2}";

                        var tag = plot.Add.Text(tagText, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        tag.LabelFontName = chineseFont;
                        tag.LabelFontSize = 8.5f;
                        tag.LabelFontColor = Color.FromHex("#f3e8ff");
                        tag.LabelAlignment = isRes ? Alignment.LowerLeft : Alignment.UpperLeft;
                        tag.LabelBackgroundColor = Color.FromHex("#3b0764").WithAlpha(0.88);
                        tag.LabelBorderColor = purpleColor;
                        tag.LabelBorderWidth = 0.8f;
                    }
                    else
                    {
                        // 正常未选中 2点趋势线渲染 (Rose Red 阻力 / Emerald Green 支撑，全图统一线宽 0.8f)
                        var baseColor = isRes
                            ? (isLatest ? Color.FromHex("#f43f5e").WithAlpha(0.9) : Color.FromHex("#f43f5e").WithAlpha(0.42)) // Rose 500
                            : (isLatest ? Color.FromHex("#10b981").WithAlpha(0.9) : Color.FromHex("#10b981").WithAlpha(0.42)); // Emerald 500

                        // 1. 主趋势线段 (全图统一线宽 0.8f)
                        var mainLine = plot.Add.Line(tl.StartBarIndex, (double)tl.StartPrice, tl.EndBarIndex, (double)tl.EndPrice);
                        mainLine.Color = baseColor;
                        mainLine.LineWidth = 0.8f;
                        mainLine.LinePattern = LinePattern.Solid;

                        // 2. 向右延伸虚线射线 (全图统一线宽 0.8f)
                        var rayLine = plot.Add.Line(tl.EndBarIndex, (double)tl.EndPrice, tl.ExtendedBarIndex, (double)tl.ExtendedPrice);
                        rayLine.Color = baseColor.WithAlpha(isLatest ? 0.75 : 0.35);
                        rayLine.LineWidth = 0.8f;
                        rayLine.LinePattern = LinePattern.Dashed;

                        // 3. 趋势线末端投影价格标签
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

            // 4.4 绘制局部高低点位标记与价格文字徽章
            if (showPivots && pivotResult.Pivots.Count > 0)
            {
                foreach (var p in pivotResult.Pivots)
                {
                    bool isHigh = p.Type == PivotPointType.High;
                    double x = p.BarIndex;
                    double y = (double)p.Price;

                    // 三角标记
                    var marker = plot.Add.Marker(x, y);
                    marker.Shape = isHigh ? MarkerShape.FilledTriangleDown : MarkerShape.FilledTriangleUp;
                    marker.Size = 7;
                    marker.Color = isHigh ? Color.FromHex("#f87171") : Color.FromHex("#4ade80");

                    // 价格文字标签 (点数较少或为全局极值时显示文字标签)
                    if (count <= 250 || p.IsGlobalExtreme)
                    {
                        string prefix = p.IsGlobalExtreme
                            ? (isHigh ? "👑高 " : "👑低 ")
                            : (isHigh ? "H: " : "L: ");
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

            // 5. 绘制用户点击选中的 K 线高亮光标 (垂直青色光标 + 收盘圆晕)
            if (selectedBarIndex.HasValue)
            {
                int selIdx = selectedBarIndex.Value;
                if (selIdx >= 0 && selIdx < count)
                {
                    var selBar = bars[selIdx];
                    double yLow = (double)selBar.Low;
                    double yHigh = (double)selBar.High;
                    double yClose = (double)selBar.Close;

                    if (yLow == yHigh)
                    {
                        yLow = yClose * 0.999;
                        yHigh = yClose * 1.001;
                    }

                    // 垂直光标
                    var cursorLine = plot.Add.Line(selIdx, yLow, selIdx, yHigh);
                    cursorLine.Color = Color.FromHex("#38bdf8");
                    cursorLine.LineWidth = 3.0f;

                    // 高亮点
                    var cursorPoint = plot.Add.Scatter(new double[] { selIdx }, new double[] { yClose });
                    cursorPoint.MarkerShape = MarkerShape.FilledCircle;
                    cursorPoint.MarkerSize = 10;
                    cursorPoint.Color = Color.FromHex("#38bdf8");
                    cursorPoint.LineWidth = 0;
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

            string sliceUnitStr = sliceUnit == SliceUnitType.Percentage
                ? $"涨跌幅 ±{thresholdValue:F2}%"
                : $"固定价差 ±{thresholdValue:F2} USDT";

            string headerTitle = sliceUnit == SliceUnitType.Percentage
                ? "【百分比 K 线特征指标】"
                : "【固定价格 K 线特征指标】";

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
