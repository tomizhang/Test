using Common.Models;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.IO;

namespace Common.Helper
{
    /// <summary>
    /// 基于 ScottPlot 5 的专业量化回测图表绘制帮助类 (纯核心库，折线图架构)
    /// 核心要素：
    /// 1. 价格走势折线图 (Close Price Line, 0.8f 蓝线)
    /// 2. 高低点极值精准标记 (波峰▲最高价 High, 波谷▼最低价 Low, 标记大小 4)
    /// 3. 阻力趋势线 (橙色, 0.8f) 与 支撑趋势线 (青色, 0.8f) 完整延伸
    /// 4. 自动调节 X/Y 轴与呼吸边距 (Auto-Scale Margins)
    /// 5. 全面系统字体检测，彻底杜绝所有标题、图例、坐标轴与卡片中文乱码
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
                // ScottPlot 5: Fonts.Detect(string text) 自动探测并返回包含该文本字形的本地字体名称
                string detected = Fonts.Detect("量化回测趋势线高低点走势价格");
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
        /// 核心绘图管线：将 K 线价格折线、高低点标记、阻力/支撑趋势线及描述摘要渲染至指定 ScottPlot.Plot 画布
        /// </summary>
        /// <param name="plot">ScottPlot 画布实例</param>
        /// <param name="klines">K线历史数据序列</param>
        /// <param name="peaks">波峰/高点集合</param>
        /// <param name="valleys">波谷/低点集合</param>
        /// <param name="trendLines">趋势线集合 (自动按阻力/支撑分类并绘制)</param>
        /// <param name="summaryDescription">图表内嵌描述摘要信息</param>
        /// <param name="title">图表主标题</param>
        /// <param name="startGlobalIndex">首根 K 线的全局索引起点 (默认 0)</param>
        /// <param name="autoScaleAxes">是否自动动态适配调节 X/Y 轴范围 (默认 true)</param>
        public static void BuildPlot(
            Plot plot,
            IReadOnlyList<RawKline> klines,
            IReadOnlyList<PivotPoint> peaks,
            IReadOnlyList<PivotPoint> valleys,
            IReadOnlyList<TrendLine> trendLines,
            string summaryDescription,
            string title = "量化回测 - 趋势线与高低点结构分析图",
            int startGlobalIndex = 0,
            bool autoScaleAxes = true)
        {
            if (plot == null || klines == null || klines.Count == 0)
            {
                return;
            }

            plot.Clear();

            // 1. 全局与局部中文字体配置 (检测系统原生中文字体，全组件覆盖彻底根治方块乱码)
            string chineseFont = GetInstalledChineseFont();
            Fonts.Default = chineseFont;

            // 2. 专业 TradingView 暗色系主题配色
            plot.FigureBackground.Color = Color.FromHex("#0f172a"); // 外底色: Slate 900
            plot.DataBackground.Color = Color.FromHex("#1e293b");   // 绘图区底色: Slate 800
            plot.Axes.Color(Color.FromHex("#94a3b8"));              // 坐标轴文字/刻度线 Slate 400
            plot.Grid.MajorLineColor = Color.FromHex("#334155");    // 网格线 Slate 700

            int count = klines.Count;
            int endGlobalIndex = startGlobalIndex + count - 1;

            // 3. 绘制 K 线价格走势折线图 (以收盘价 Close 绘制基准折线，线宽 0.8f)
            double[] xs = new double[count];
            double[] ys = new double[count];

            for (int i = 0; i < count; i++)
            {
                xs[i] = startGlobalIndex + i;
                ys[i] = (double)klines[i].Close;
            }

            var priceLine = plot.Add.Scatter(xs, ys);
            priceLine.LineWidth = 0.8f; // 线宽 0.8f
            priceLine.MarkerSize = 0;   // 纯平滑折线
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
                    peakScatter.MarkerSize = 4; // 标记大小 4
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
                    valleyScatter.MarkerSize = 4; // 标记大小 4
                    valleyScatter.Color = Color.FromHex("#22c55e"); // 绿色低点
                    valleyScatter.LineWidth = 0;
                    valleyScatter.LegendText = $"波谷低点 ({valleysCount})";
                }
            }

            // 5. 绘制趋势线 (阻力趋势线: 橙红色, 支撑趋势线: 青色, 线宽 0.8f)
            int resistanceDrawn = 0;
            int supportDrawn = 0;

            if (trendLines != null && trendLines.Count > 0)
            {
                foreach (var line in trendLines)
                {
                    // 确保趋势线在当前图表可见范围有交集
                    if (line.X2 < startGlobalIndex || line.X1 > endGlobalIndex)
                        continue;

                    int endX = line.CollidedKlineIndex >= 0 ? line.CollidedKlineIndex : (line.X2 + line.LineExtensionRange);
                    if (endX > endGlobalIndex) endX = endGlobalIndex;

                    double xStart = line.X1;
                    double xEnd = endX;
                    double yStart = (double)line.Y1;
                    double yEnd = (double)line.GetPriceAt(endX);

                    var linePlot = plot.Add.Line(xStart, yStart, xEnd, yEnd);
                    linePlot.LineWidth = 0.8f; // 线宽 0.8f

                    if (line.IsResistance)
                    {
                        linePlot.Color = Color.FromHex("#f97316"); // 橙红色阻力线
                        resistanceDrawn++;
                    }
                    else
                    {
                        linePlot.Color = Color.FromHex("#06b6d4"); // 青色支撑线
                        supportDrawn++;
                    }
                }
            }

            // 6. 添加左上角结构化描述摘要卡片 (强制中文字体)
            string timeRange = $"{TimeHelper.FromUnixTimeMilliseconds(klines[0].OpenTime):yyyy-MM-dd HH:mm} ~ {TimeHelper.FromUnixTimeMilliseconds(klines[count - 1].CloseTime):yyyy-MM-dd HH:mm}";
            string fullSummary = $"【量化结构指标摘要】\n" +
                                 $"• 时间跨度: {timeRange} (UTC+0)\n" +
                                 $"• K线根数: {count:N0} 根 | 价格区间: {klines[0].Close:F2} -> {klines[count - 1].Close:F2}\n" +
                                 $"• 极值高低点: 高点(Peaks)={peaksCount}, 低点(Valleys)={valleysCount}\n" +
                                 $"• 绘制趋势线: 阻力线={resistanceDrawn}条, 支撑线={supportDrawn}条 (总库: {trendLines?.Count ?? 0})\n" +
                                 $"• 策略备注: {summaryDescription}";

            var annotation = plot.Add.Annotation(fullSummary, Alignment.UpperLeft);
            annotation.LabelStyle.FontName = chineseFont;
            annotation.LabelStyle.FontSize = 12;
            annotation.LabelStyle.ForeColor = Color.FromHex("#f8fafc");
            annotation.LabelStyle.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.88);
            annotation.LabelStyle.BorderColor = Color.FromHex("#475569");
            annotation.LabelStyle.BorderWidth = 1.5f;
            annotation.LabelStyle.ShadowColor = Colors.Transparent;

            // 7. 设置标题、坐标轴标签与图例 (全面绑定中文字体，解决图例与坐标轴乱码)
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

            // 8. 自动调节 X/Y 轴坐标与留白呼吸边距 (Auto-Scale)
            if (autoScaleAxes)
            {
                plot.Axes.Margins(horizontal: 0.02, vertical: 0.08); // X轴留白2%，Y轴上下留白8%
                plot.Axes.AutoScale();
            }
        }

        /// <summary>
        /// 兼容重载：按独立阻力线和支撑线列表绘制
        /// </summary>
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
            bool autoScaleAxes = true)
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
                autoScaleAxes);
        }

        /// <summary>
        /// 绘制包含 K线折线图、极值高低点、支撑/阻力趋势线及描述摘要的专业分析图表并保存落盘
        /// </summary>
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
            int height = 1080)
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
                autoScaleAxes: true);

            // 确保落盘目录并保存图片
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
