using Common.Models;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.IO;

namespace Common.Helper
{
    /// <summary>
    /// 基于 ScottPlot 5 的专业量化回测图表绘制帮助类 (纯核心库，折线图架构 + 交易信号标记)
    /// </summary>
    public static class PlotHelper
    {
        /// <summary>
        /// 自动检测并获取当前系统支持的最佳中文字体名称
        /// </summary>
        public static string GetInstalledChineseFont()
        {
            try
            {
                string detected = Fonts.Detect("量化回测趋势线高低点走势价格开多开空");
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    return detected;
                }
            }
            catch
            {
            }

            return "Microsoft YaHei";
        }

        /// <summary>
        /// 核心绘图管线：将 K 线价格折线、高低点标记、阻力/支撑趋势线、交易开仓信号及描述摘要渲染至指定 ScottPlot.Plot 画布
        /// </summary>
        public static void BuildPlot(
            Plot plot,
            IReadOnlyList<RawKline> klines,
            IReadOnlyList<PivotPoint> peaks,
            IReadOnlyList<PivotPoint> valleys,
            IReadOnlyList<TrendLine> trendLines,
            string summaryDescription,
            string title = "量化回测 - 趋势线与高低点结构分析图",
            int startGlobalIndex = 0,
            bool autoScaleAxes = true,
            IReadOnlyList<TradeSignal>? tradeSignals = null,
            float lineWidth = 0.8f)
        {
            if (plot == null || klines == null || klines.Count == 0)
            {
                return;
            }

            plot.Clear();

            // 1. 全局与局部中文字体配置
            string chineseFont = GetInstalledChineseFont();
            Fonts.Default = chineseFont;

            // 2. 专业 TradingView 暗色系主题配色
            plot.FigureBackground.Color = Color.FromHex("#0f172a"); // 外底色: Slate 900
            plot.DataBackground.Color = Color.FromHex("#1e293b");   // 绘图区底色: Slate 800
            plot.Axes.Color(Color.FromHex("#94a3b8"));              // 坐标轴文字/刻度线 Slate 400
            plot.Grid.MajorLineColor = Color.FromHex("#334155");    // 网格线 Slate 700

            int count = klines.Count;
            int endGlobalIndex = startGlobalIndex + count - 1;

            // 3. 绘制 K 线价格走势折线图 (以收盘价 Close 绘制基准折线，线宽 lineWidth)
            double[] xs = new double[count];
            double[] ys = new double[count];

            for (int i = 0; i < count; i++)
            {
                xs[i] = startGlobalIndex + i;
                ys[i] = (double)klines[i].Close;
            }

            var priceLine = plot.Add.Scatter(xs, ys);
            priceLine.LineWidth = Math.Max(0.5f, lineWidth);
            priceLine.MarkerSize = 0;
            priceLine.Color = Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
            priceLine.LegendText = $"价格收盘折线 ({count:N0}根)";

            // 4. 绘制高低点极值标记 (波峰▲最高价 High, 波谷▼最低价 Low, 大小 4)
            int peaksCount = 0;
            if (peaks != null && peaks.Count > 0)
            {
                var peakXs = new List<double>();
                var peakYs = new List<double>();

                foreach (var p in peaks)
                {
                    if (p.Index >= startGlobalIndex && p.Index <= endGlobalIndex)
                    {
                        peakXs.Add(p.Index);
                        peakYs.Add((double)p.Price);
                        peaksCount++;
                    }
                }

                if (peakXs.Count > 0)
                {
                    var peakScatter = plot.Add.Scatter(peakXs.ToArray(), peakYs.ToArray());
                    peakScatter.MarkerShape = MarkerShape.FilledTriangleUp;
                    peakScatter.MarkerSize = 4;
                    peakScatter.Color = Color.FromHex("#ef4444"); // 红色高点
                    peakScatter.LineWidth = 0;
                    peakScatter.LegendText = $"波峰高点 ({peaksCount})";
                }
            }

            int valleysCount = 0;
            if (valleys != null && valleys.Count > 0)
            {
                var valleyXs = new List<double>();
                var valleyYs = new List<double>();

                foreach (var v in valleys)
                {
                    if (v.Index >= startGlobalIndex && v.Index <= endGlobalIndex)
                    {
                        valleyXs.Add(v.Index);
                        valleyYs.Add((double)v.Price);
                        valleysCount++;
                    }
                }

                if (valleyXs.Count > 0)
                {
                    var valleyScatter = plot.Add.Scatter(valleyXs.ToArray(), valleyYs.ToArray());
                    valleyScatter.MarkerShape = MarkerShape.FilledTriangleDown;
                    valleyScatter.MarkerSize = 4;
                    valleyScatter.Color = Color.FromHex("#22c55e"); // 绿色低点
                    valleyScatter.LineWidth = 0;
                    valleyScatter.LegendText = $"波谷低点 ({valleysCount})";
                }
            }

            // 5. 绘制趋势线 (三点确认线: 金黄色加粗凸显；触发线: 绿色加粗；活跃阻力: 橙红；活跃支撑: 青色；已击穿: 灰暗色)
            int resistanceDrawn = 0;
            int supportDrawn = 0;
            int triggeredDrawn = 0;
            int threePointConfirmedDrawn = 0;

            var thirdPointXs = new List<double>();
            var thirdPointYs = new List<double>();

            if (trendLines != null && trendLines.Count > 0)
            {
                int maxLinesToDraw = 200;
                int drawnTotal = 0;

                for (int i = trendLines.Count - 1; i >= 0 && drawnTotal < maxLinesToDraw; i--)
                {
                    var line = trendLines[i];

                    // 计算趋势线延伸终点：
                    // 1. 若为已被击穿/删除的趋势线 (CollidedKlineIndex >= 0)，则严格延长至发生击穿时的 K 线位置终止，绝不往后多画
                    // 2. 若为活跃趋势线 (未发生击穿 CollidedKlineIndex == -1)，则向右延长至当前图表最右端 (endGlobalIndex)
                    int effectiveEndX;
                    if (line.CollidedKlineIndex >= 0)
                    {
                        effectiveEndX = line.CollidedKlineIndex;
                    }
                    else
                    {
                        effectiveEndX = Math.Max(line.X2, endGlobalIndex);
                    }

                    // 确保趋势线延伸段与当前图表可见范围 [startGlobalIndex, endGlobalIndex] 有交集
                    if (effectiveEndX < startGlobalIndex || line.X1 > endGlobalIndex)
                        continue;

                    double xStart = line.X1;
                    double xEnd = effectiveEndX;
                    double yStart = (double)line.Y1;
                    double yEnd = (double)line.GetPriceAt(effectiveEndX);

                    var linePlot = plot.Add.Line(xStart, yStart, xEnd, yEnd);

                    if (line.IsTriggered)
                    {
                        linePlot.Color = Color.FromHex("#22c55e"); // 亮绿色: 触发开仓的趋势线 (加粗突出)
                        linePlot.LineWidth = Math.Max(1.0f, lineWidth * 2.0f);
                        triggeredDrawn++;
                    }
                    else if (line.IsThreePointConfirmed)
                    {
                        // 🌟 三点共线/连接第3点附近的强趋势线 (凸显为耀眼金黄色)
                        if (line.CollidedKlineIndex >= 0)
                        {
                            linePlot.Color = Color.FromHex("#d97706").WithAlpha(0.65); // 已击穿的三点线: 琥珀金暗色
                            linePlot.LineWidth = Math.Max(0.5f, lineWidth * 1.3f);
                        }
                        else
                        {
                            linePlot.Color = Color.FromHex("#fbbf24"); // 活跃三点强趋势线: 金黄色加粗凸显
                            linePlot.LineWidth = Math.Max(1.0f, lineWidth * 2.0f);
                        }
                        threePointConfirmedDrawn++;

                        // 记录第 3 个触碰点用于打标
                        if (line.X3 >= startGlobalIndex && line.X3 <= endGlobalIndex)
                        {
                            thirdPointXs.Add(line.X3);
                            thirdPointYs.Add(line.Y3 > 0m ? (double)line.Y3 : (double)line.GetPriceAt(line.X3));
                        }
                    }
                    else if (line.IsResistance)
                    {
                        if (line.CollidedKlineIndex >= 0)
                        {
                            linePlot.Color = Color.FromHex("#64748b").WithAlpha(0.45); // 已击穿历史阻力线: 灰暗色且在击穿点严格终止
                            linePlot.LineWidth = Math.Max(0.4f, lineWidth * 0.7f);
                        }
                        else
                        {
                            linePlot.Color = Color.FromHex("#f97316"); // 活跃阻力线: 鲜明橙红 (向右无限延伸)
                            linePlot.LineWidth = lineWidth;
                        }
                        resistanceDrawn++;
                    }
                    else
                    {
                        if (line.CollidedKlineIndex >= 0)
                        {
                            linePlot.Color = Color.FromHex("#64748b").WithAlpha(0.45); // 已击穿历史支撑线: 灰暗色且在击穿点严格终止
                            linePlot.LineWidth = Math.Max(0.4f, lineWidth * 0.7f);
                        }
                        else
                        {
                            linePlot.Color = Color.FromHex("#06b6d4"); // 活跃支撑线: 鲜明青色 (向右无限延伸)
                            linePlot.LineWidth = lineWidth;
                        }
                        supportDrawn++;
                    }
                    drawnTotal++;
                }
            }

            // 绘制第 3 点触碰确认高亮标记
            if (thirdPointXs.Count > 0)
            {
                var starScatter = plot.Add.Scatter(thirdPointXs.ToArray(), thirdPointYs.ToArray());
                starScatter.MarkerShape = MarkerShape.FilledCircle;
                starScatter.MarkerSize = 4;
                starScatter.Color = Color.FromHex("#fbbf24"); // 金黄色第3点标记
                starScatter.LineWidth = 0;
                starScatter.LegendText = $"⭐ 三点确认 ({thirdPointXs.Count})";
            }

            // 6. 绘制交易信号标记 (多单: 紫色菱形◆, 空单: 粉红色方形■)
            int longSignalsDrawn = 0;
            int shortSignalsDrawn = 0;
            if (tradeSignals != null && tradeSignals.Count > 0)
            {
                var buyXs = new List<double>();
                var buyYs = new List<double>();
                var sellXs = new List<double>();
                var sellYs = new List<double>();

                foreach (var s in tradeSignals)
                {
                    if (s.GlobalBarIndex >= startGlobalIndex && s.GlobalBarIndex <= endGlobalIndex)
                    {
                        if (s.Side == TradeSide.Buy)
                        {
                            buyXs.Add(s.GlobalBarIndex);
                            buyYs.Add((double)s.Price);
                            longSignalsDrawn++;
                        }
                        else
                        {
                            sellXs.Add(s.GlobalBarIndex);
                            sellYs.Add((double)s.Price);
                            shortSignalsDrawn++;
                        }
                    }
                }

                if (buyXs.Count > 0)
                {
                    var buyScatter = plot.Add.Scatter(buyXs.ToArray(), buyYs.ToArray());
                    buyScatter.MarkerShape = MarkerShape.FilledDiamond;
                    buyScatter.MarkerSize = 7;
                    buyScatter.Color = Color.FromHex("#a855f7"); // 紫色开多标记
                    buyScatter.LineWidth = 0;
                    buyScatter.LegendText = $"🟢 开多信号 ({longSignalsDrawn})";
                }

                if (sellXs.Count > 0)
                {
                    var sellScatter = plot.Add.Scatter(sellXs.ToArray(), sellYs.ToArray());
                    sellScatter.MarkerShape = MarkerShape.FilledSquare;
                    sellScatter.MarkerSize = 6;
                    sellScatter.Color = Color.FromHex("#f43f5e"); // 玫红开空标记
                    sellScatter.LineWidth = 0;
                    sellScatter.LegendText = $"🔴 开空信号 ({shortSignalsDrawn})";
                }
            }

            // 7. 添加左上角结构化描述摘要卡片 (强制中文字体)
            string timeRange = $"{TimeHelper.FromUnixTimeMilliseconds(klines[0].OpenTime):yyyy-MM-dd HH:mm} ~ {TimeHelper.FromUnixTimeMilliseconds(klines[count - 1].CloseTime):yyyy-MM-dd HH:mm}";
            string fullSummary = $"【量化结构指标摘要】\n" +
                                 $"• 时间跨度: {timeRange} (UTC+0)\n" +
                                 $"• K线根数: {count:N0} 根 | 价格区间: {klines[0].Close:F2} -> {klines[count - 1].Close:F2}\n" +
                                 $"• 极值高低点: 高点(Peaks)={peaksCount}, 低点(Valleys)={valleysCount}\n" +
                                 $"• 绘制趋势线: 阻力线={resistanceDrawn}条, 支撑线={supportDrawn}条 (⭐三点共线强线={threePointConfirmedDrawn}条)\n" +
                                 $"• 开仓信号: 多单={longSignalsDrawn}笔, 空单={shortSignalsDrawn}笔 (策略: 触碰3-Tick回弹 LineX1X2>=40, LineAge>=4)\n" +
                                 $"• 策略备注: {summaryDescription}";

            var annotation = plot.Add.Annotation(fullSummary, Alignment.UpperLeft);
            annotation.LabelStyle.FontName = chineseFont;
            annotation.LabelStyle.FontSize = 12;
            annotation.LabelStyle.ForeColor = Color.FromHex("#f8fafc");
            annotation.LabelStyle.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.88);
            annotation.LabelStyle.BorderColor = Color.FromHex("#475569");
            annotation.LabelStyle.BorderWidth = 1.5f;
            annotation.LabelStyle.ShadowColor = Colors.Transparent;

            // 8. 设置标题、坐标轴标签与图例
            plot.Title(title, size: 16);
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#f8fafc");

            plot.Axes.Bottom.Label.Text = "全局 K 线序列号 (Global Bar Index)";
            plot.Axes.Bottom.Label.FontName = chineseFont;
            plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#cbd5e1");
            plot.Axes.Bottom.TickLabelStyle.FontName = chineseFont;

            plot.Axes.Left.Label.Text = "价格 (USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.ForeColor = Color.FromHex("#cbd5e1");
            plot.Axes.Left.TickLabelStyle.FontName = chineseFont;

            plot.ShowLegend(Alignment.UpperRight);
            plot.Legend.FontName = chineseFont;
            plot.Legend.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
            plot.Legend.OutlineColor = Color.FromHex("#475569");

            // 9. 自动调节 X/Y 轴坐标与留白呼吸边距 (Auto-Scale)
            if (autoScaleAxes)
            {
                plot.Axes.Margins(horizontal: 0.02, vertical: 0.08);
                plot.Axes.AutoScale();
            }
        }

        public static void BuildPlot(
            Plot plot,
            IReadOnlyList<RawKline> klines,
            IReadOnlyList<PivotPoint> peaks,
            IReadOnlyList<PivotPoint> valleys,
            IReadOnlyList<TrendLine> resistanceLines,
            IReadOnlyList<TrendLine> supportLines,
            string summaryDescription,
            string title = "量化回测 - 趋势线与高低点结构分析图",
            int startGlobalIndex = 0,
            bool autoScaleAxes = true,
            IReadOnlyList<TradeSignal>? tradeSignals = null,
            float lineWidth = 0.8f)
        {
            var allLines = new List<TrendLine>();
            if (resistanceLines != null) allLines.AddRange(resistanceLines);
            if (supportLines != null) allLines.AddRange(supportLines);

            BuildPlot(
                plot,
                klines,
                peaks,
                valleys,
                allLines,
                summaryDescription,
                title,
                startGlobalIndex,
                autoScaleAxes,
                tradeSignals,
                lineWidth);
        }

        public static string PlotTrendLineChart(
            IReadOnlyList<RawKline> klines,
            IReadOnlyList<PivotPoint> peaks,
            IReadOnlyList<PivotPoint> valleys,
            IReadOnlyList<TrendLine> resistanceLines,
            IReadOnlyList<TrendLine> supportLines,
            string summaryDescription,
            string title = "量化回测 - 趋势线与高低点结构分析图",
            int startGlobalIndex = 0,
            string outputFilePath = null,
            int width = 1920,
            int height = 1080,
            IReadOnlyList<TradeSignal>? tradeSignals = null,
            float lineWidth = 0.8f)
        {
            if (klines == null || klines.Count == 0)
            {
                Logger.Log("[PlotHelper] K线数据为空，无法生成图表。");
                return string.Empty;
            }

            var plot = new Plot();
            BuildPlot(
                plot,
                klines,
                peaks,
                valleys,
                resistanceLines,
                supportLines,
                summaryDescription,
                title,
                startGlobalIndex,
                autoScaleAxes: true,
                tradeSignals: tradeSignals,
                lineWidth: lineWidth);

            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                string chartsDir = Config.GetChartsPath();
                string fileName = $"trendline_analysis_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";
                outputFilePath = Path.Combine(chartsDir, fileName);
            }
            else
            {
                string dir = Path.GetDirectoryName(outputFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }

            plot.SavePng(outputFilePath, width, height);
            Logger.Log($"[PlotHelper] 图表已成功生成并落地保存至: {outputFilePath}");

            return outputFilePath;
        }
    }
}
