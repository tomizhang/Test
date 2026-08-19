using Common.Models;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.IO;

namespace Common.Helper
{
    /// <summary>
    /// 基于 ScottPlot 5 的专业量化回测图表绘制帮助类
    /// 支持折线图走势、高低点标记、阻力/支撑趋势线绘制与结构化描述摘要卡片落地为 PNG 图片
    /// </summary>
    public static class PlotHelper
    {
        /// <summary>
        /// 绘制包含 K线折线图、极值高低点、支撑/阻力趋势线及描述摘要的专业分析图表并保存落盘
        /// </summary>
        /// <param name="klines">K线历史数据序列</param>
        /// <param name="peaks">波峰/高点集合</param>
        /// <param name="valleys">波谷/低点集合</param>
        /// <param name="resistanceLines">阻力趋势线集合</param>
        /// <param name="supportLines">支撑趋势线集合</param>
        /// <param name="summaryDescription">图表内嵌描述摘要信息 (展示在左上角/右上角摘要卡片中)</param>
        /// <param name="title">图表主标题</param>
        /// <param name="startGlobalIndex">首根 K 线的全局索引起点 (默认 0)</param>
        /// <param name="outputFilePath">指定输出图片完整路径 (默认保存至 Config.GetChartsPath())</param>
        /// <param name="width">图片宽度像素 (默认 1920)</param>
        /// <param name="height">图片高度像素 (默认 1080)</param>
        /// <returns>生成的 PNG 图片绝对路径</returns>
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

            // 1. 初始化 ScottPlot 画布与中文字体支持 (彻底解决 CJK 中文显示方块乱码问题)
            var plot = new Plot();
            string chineseFont = "Microsoft YaHei"; // 微软雅黑 (Windows 自带中文字体)
            Fonts.Default = chineseFont;

            // 2. 专业 TradingView 暗色系主题配色
            plot.FigureBackground.Color = Color.FromHex("#0f172a"); // 外底色: 深深蓝灰 Slate 900
            plot.DataBackground.Color = Color.FromHex("#1e293b");   // 绘图区底色: Slate 800
            plot.Axes.Color(Color.FromHex("#94a3b8"));              // 统一设置坐标轴边框、刻度线与文字颜色 Slate 400
            plot.Grid.MajorLineColor = Color.FromHex("#334155");    // 网格线 Slate 700

            // 3. 提取 K 线价格折线数据 (以收盘价 Close 绘制基准折线，线宽 0.8f)
            int count = klines.Count;
            double[] xs = new double[count];
            double[] ys = new double[count];

            for (int i = 0; i < count; i++)
            {
                xs[i] = startGlobalIndex + i;
                ys[i] = (double)klines[i].Close;
            }

            // 添加主价格折线 (线宽 0.8f)
            var priceLine = plot.Add.Scatter(xs, ys);
            priceLine.LineWidth = 0.8f;
            priceLine.MarkerSize = 0;
            priceLine.Color = Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
            priceLine.LegendText = $"价格收盘折线 (共 {count:N0} 根)";

            // 4. 绘制高低点 (Peaks & Valleys) 标记 (标记大小改为 4)
            if (peaks != null && peaks.Count > 0)
            {
                var peakXs = new List<double>();
                var peakYs = new List<double>();

                foreach (var p in peaks)
                {
                    peakXs.Add(p.Index);
                    peakYs.Add((double)p.Price);
                }

                var peakScatter = plot.Add.Scatter(peakXs.ToArray(), peakYs.ToArray());
                peakScatter.MarkerShape = MarkerShape.FilledTriangleUp;
                peakScatter.MarkerSize = 4; // 标记大小改为 4
                peakScatter.Color = Color.FromHex("#ef4444"); // 红色高点
                peakScatter.LineWidth = 0;
                peakScatter.LegendText = $"波峰高点 (Peaks: {peaks.Count})";
            }

            if (valleys != null && valleys.Count > 0)
            {
                var valleyXs = new List<double>();
                var valleyYs = new List<double>();

                foreach (var v in valleys)
                {
                    valleyXs.Add(v.Index);
                    valleyYs.Add((double)v.Price);
                }

                var valleyScatter = plot.Add.Scatter(valleyXs.ToArray(), valleyYs.ToArray());
                valleyScatter.MarkerShape = MarkerShape.FilledTriangleDown;
                valleyScatter.MarkerSize = 4; // 标记大小改为 4
                valleyScatter.Color = Color.FromHex("#22c55e"); // 绿色低点
                valleyScatter.LineWidth = 0;
                valleyScatter.LegendText = $"波谷低点 (Valleys: {valleys.Count})";
            }

            // 5. 绘制阻力趋势线 (橙红色线段) 与 支撑趋势线 (青色线段)，线宽统一为 0.8f
            int resistanceDrawn = 0;
            if (resistanceLines != null)
            {
                foreach (var line in resistanceLines)
                {
                    int endX = line.CollidedKlineIndex >= 0 ? line.CollidedKlineIndex : (line.X2 + line.LineExtensionRange);
                    double yStart = (double)line.Y1;
                    double yEnd = (double)line.GetPriceAt(endX);

                    var linePlot = plot.Add.Line((double)line.X1, yStart, (double)endX, yEnd);
                    linePlot.LineWidth = 0.8f; // 线宽统一为 0.8f
                    linePlot.Color = Color.FromHex("#f97316"); // 橙红色阻力线
                    resistanceDrawn++;
                }
            }

            int supportDrawn = 0;
            if (supportLines != null)
            {
                foreach (var line in supportLines)
                {
                    int endX = line.CollidedKlineIndex >= 0 ? line.CollidedKlineIndex : (line.X2 + line.LineExtensionRange);
                    double yStart = (double)line.Y1;
                    double yEnd = (double)line.GetPriceAt(endX);

                    var linePlot = plot.Add.Line((double)line.X1, yStart, (double)endX, yEnd);
                    linePlot.LineWidth = 0.8f; // 线宽统一为 0.8f
                    linePlot.Color = Color.FromHex("#06b6d4"); // 青色支撑线
                    supportDrawn++;
                }
            }

            // 6. 添加左上角结构化描述摘要卡片 (中文字体支持)
            string timeRange = $"{TimeHelper.FromUnixTimeMilliseconds(klines[0].OpenTime):yyyy-MM-dd HH:mm} ~ {TimeHelper.FromUnixTimeMilliseconds(klines[count - 1].CloseTime):yyyy-MM-dd HH:mm}";
            string fullSummary = $"【量化结构指标摘要】\n" +
                                 $"• 时间跨度: {timeRange} (UTC+0)\n" +
                                 $"• K线根数: {count:N0} 根 | 价格范围: {klines[0].Close:F2} -> {klines[count - 1].Close:F2}\n" +
                                 $"• 识别极值: 高点(Peaks)={peaks?.Count ?? 0}, 低点(Valleys)={valleys?.Count ?? 0}\n" +
                                 $"• 绘制趋势线: 阻力线={resistanceDrawn}条, 支撑线={supportDrawn}条\n" +
                                 $"• 策略备注: {summaryDescription}";

            var annotation = plot.Add.Annotation(fullSummary, Alignment.UpperLeft);
            annotation.LabelStyle.FontName = chineseFont; // 强制指定中文字体
            annotation.LabelStyle.FontSize = 13;
            annotation.LabelStyle.ForeColor = Color.FromHex("#f8fafc");
            annotation.LabelStyle.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.88);
            annotation.LabelStyle.BorderColor = Color.FromHex("#475569");
            annotation.LabelStyle.BorderWidth = 1.5f;
            annotation.LabelStyle.ShadowColor = Colors.Transparent;

            // 7. 设置标题、坐标轴标签与图例 (统一中文字体)
            plot.Title(title, size: 18);
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#f8fafc");

            plot.Axes.Bottom.Label.Text = "全局 K 线序列号 (Global Bar Index)";
            plot.Axes.Bottom.Label.FontName = chineseFont;
            plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#cbd5e1");

            plot.Axes.Left.Label.Text = "价格 (USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.ForeColor = Color.FromHex("#cbd5e1");

            plot.ShowLegend(Alignment.UpperRight);
            plot.Legend.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
            plot.Legend.OutlineColor = Color.FromHex("#475569");

            // 8. 确保落盘目录并保存图片
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
