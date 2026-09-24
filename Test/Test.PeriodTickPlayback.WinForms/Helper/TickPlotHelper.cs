using Common;
using ScottPlot;
using ScottPlot.Plottables;
using System;
using System.Collections.Generic;
using System.Linq;
using Test.PeriodTickPlayback.WinForms.Engine;
using Test.PeriodTickPlayback.WinForms.Models;
using Color = ScottPlot.Color;

namespace Test.PeriodTickPlayback.WinForms.Helper
{
    /// <summary>
    /// 大周期内部微观 Tick 走势专业绘制助手
    /// 实时呈现当前 30 分钟周期内的逐笔价格脉冲、买卖盘主动量能与极值点
    /// </summary>
    public static class TickPlotHelper
    {
        private static readonly string[] PreferredChineseFonts = { "Microsoft YaHei", "PingFang SC", "SimHei", "Noto Sans CJK SC", "WenQuanYi Micro Hei" };

        public static string GetInstalledChineseFont()
        {
            try
            {
                string detected = Fonts.Detect("量化回测趋势线高低点走势价格开多开空成交买卖主动");
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    return detected;
                }
            }
            catch
            {
            }

            try
            {
                var installedFonts = System.Drawing.FontFamily.Families.Select(f => f.Name).ToHashSet();
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

        public static void BuildTickPlot(
            Plot plot,
            IReadOnlyList<RawTick> ticks,
            int currentTickCursor,
            DateTime bucketStart,
            DateTime bucketEnd,
            string coin,
            string periodTitle,
            string customTitle = "",
            IReadOnlyList<int>? boundaryTickIndices = null,
            TickLongShortStats? precomputedStats = null,
            bool ratioProximity = true,
            TimeSpan? subPeriodSpan = null,
            MacroChartDisplayType displayType = MacroChartDisplayType.LineChart,
            bool showConsecutiveTrend = false,
            int consecutiveMinBars = 5,
            decimal consecutiveMinPct = 2.5m,
            bool showRatio = true,
            bool showAngleLines = true,
            IReadOnlyList<double>? customAngles = null,
            MacroConsecutiveTrendItem? selectedTrend = null,
            IReadOnlyList<MacroConsecutiveTrendItem>? activeChannels = null,
            int? selectedBarStartIndex = null,
            int? selectedBarEndIndex = null,
            IReadOnlyList<(DateTime StartTime, DateTime EndTime)>? macroBarTimes = null,
            TickHoverIndicator? hoverIndicator = null)
        {
            if (plot == null) return;

            // 若指定了子周期，或者选择以蜡烛图 (K线) 查看，则执行全量子周期 K 线图表构建
            if (subPeriodSpan.HasValue || displayType == MacroChartDisplayType.Candlestick)
            {
                TimeSpan effectiveSpan = subPeriodSpan ?? (bucketEnd - bucketStart > TimeSpan.FromHours(2)
                    ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromMinutes(1));

                BuildSubPeriodKlinesPlot(
                    plot,
                    ticks,
                    currentTickCursor,
                    bucketStart,
                    bucketEnd,
                    effectiveSpan,
                    coin,
                    periodTitle,
                    displayType,
                    customTitle,
                    precomputedStats,
                    ratioProximity,
                    showConsecutiveTrend,
                    consecutiveMinBars,
                    consecutiveMinPct,
                    showRatio,
                    showAngleLines: showAngleLines,
                    customAngles: customAngles,
                    selectedTrend: selectedTrend,
                    activeChannels: activeChannels,
                    selectedBarStartIndex: selectedBarStartIndex,
                    selectedBarEndIndex: selectedBarEndIndex,
                    macroBarTimes: macroBarTimes,
                    hoverIndicator: hoverIndicator);
                return;
            }

            plot.Clear();

            // 彻底清除历史残留的非标准坐标轴，确保仅保留 Left, Right, Bottom, Top 四个内建轴，杜绝多窗口/多坐标轴堆叠！
            var extraAxes = plot.Axes.GetAxes().Where(ax => ax != plot.Axes.Left && ax != plot.Axes.Right && ax != plot.Axes.Bottom && ax != plot.Axes.Top).ToList();
            foreach (var ax in extraAxes)
            {
                plot.Axes.Remove(ax);
            }

            string chineseFont = GetInstalledChineseFont();
            Fonts.Default = chineseFont;

            // 暗黑背景
            plot.FigureBackground.Color = Color.FromHex("#0b0f19");
            plot.DataBackground.Color = Color.FromHex("#0f172a");

            plot.Axes.Color(Color.FromHex("#94a3b8"));
            plot.Grid.MajorLineColor = Color.FromHex("#1e293b").WithAlpha(160);
            plot.Grid.MinorLineColor = Color.FromHex("#1e293b").WithAlpha(60);

            int totalAvailable = ticks?.Count ?? 0;
            int renderCount = Math.Min(totalAvailable, currentTickCursor);

            if (renderCount == 0)
            {
                var emptyTxt = plot.Add.Text("当前周期暂无 Tick 或等待播放...", 0, 0);
                emptyTxt.LabelFontName = chineseFont;
                emptyTxt.LabelFontSize = 13;
                emptyTxt.LabelFontColor = Color.FromHex("#64748b");
                emptyTxt.LabelAlignment = Alignment.MiddleCenter;
                plot.Axes.SetLimits(-10, 10, -10, 10);
                return;
            }

            // 1. 扫描极值与量能统计
            decimal minVal = decimal.MaxValue;
            decimal maxVal = decimal.MinValue;
            int minIdx = 0;
            int maxIdx = 0;
            double maxVol = 0;

            for (int i = 0; i < renderCount; i++)
            {
                var t = ticks![i];
                if (t.Price < minVal) { minVal = t.Price; minIdx = i; }
                if (t.Price > maxVal) { maxVal = t.Price; maxIdx = i; }
                double q = (double)t.Qty;
                if (q > maxVol) maxVol = q;
            }

            // 2. 提取价格折线坐标 (若点数 > 2000 则进行保留极值的智能抽样，保证 60 FPS 极速渲染)
            double[] xs;
            double[] ys;

            if (renderCount <= 2000)
            {
                xs = new double[renderCount];
                ys = new double[renderCount];
                for (int i = 0; i < renderCount; i++)
                {
                    xs[i] = i;
                    ys[i] = (double)ticks![i].Price;
                }
            }
            else
            {
                int targetPoints = 2000;
                double step = (double)(renderCount - 1) / (targetPoints - 1);
                var sampledIndices = new SortedSet<int> { 0, minIdx, maxIdx, renderCount - 1 };
                for (int p = 0; p < targetPoints; p++)
                {
                    int idx = (int)Math.Round(p * step);
                    if (idx >= 0 && idx < renderCount)
                    {
                        sampledIndices.Add(idx);
                    }
                }

                xs = new double[sampledIndices.Count];
                ys = new double[sampledIndices.Count];
                int sIdx = 0;
                foreach (int origIdx in sampledIndices)
                {
                    xs[sIdx] = origIdx;
                    ys[sIdx] = (double)ticks![origIdx].Price;
                    sIdx++;
                }
            }

            // 3. 构建成交量副图柱 (若柱数 > 500 则按屏幕像素密度聚合，杜绝超大列表卡死 UI)
            const int maxVolBars = 500;
            var volBars = new List<ScottPlot.Bar>(Math.Min(renderCount, maxVolBars));

            if (renderCount <= maxVolBars)
            {
                for (int i = 0; i < renderCount; i++)
                {
                    var t = ticks![i];
                    double q = (double)t.Qty;
                    Color vColor = !t.IsBuyerMaker
                        ? Color.FromHex("#22c55e").WithAlpha(100) // 主动买入绿
                        : Color.FromHex("#ef4444").WithAlpha(100); // 主动卖出红

                    volBars.Add(new ScottPlot.Bar
                    {
                        Position = i,
                        Value = q,
                        FillColor = vColor,
                        LineColor = vColor.WithAlpha(180),
                        LineWidth = 0.5f
                    });
                }
            }
            else
            {
                double step = (double)renderCount / maxVolBars;
                double maxChunkVol = 0;
                for (int b = 0; b < maxVolBars; b++)
                {
                    int start = (int)(b * step);
                    int end = Math.Min(renderCount, (int)((b + 1) * step));
                    if (start >= end) continue;

                    double chunkVol = 0;
                    double buyVol = 0;
                    for (int k = start; k < end; k++)
                    {
                        double q = (double)ticks![k].Qty;
                        chunkVol += q;
                        if (!ticks[k].IsBuyerMaker) buyVol += q;
                    }

                    if (chunkVol > maxChunkVol) maxChunkVol = chunkVol;

                    bool chunkIsBuy = buyVol >= (chunkVol - buyVol);
                    Color vColor = chunkIsBuy
                        ? Color.FromHex("#22c55e").WithAlpha(100)
                        : Color.FromHex("#ef4444").WithAlpha(100);

                    double pos = (start + end - 1) / 2.0;
                    volBars.Add(new ScottPlot.Bar
                    {
                        Position = pos,
                        Value = chunkVol,
                        FillColor = vColor,
                        LineColor = vColor.WithAlpha(180),
                        LineWidth = 0.5f
                    });
                }
                if (maxChunkVol > maxVol) maxVol = maxChunkVol;
            }

            decimal firstPrice = ticks![0].Price;
            decimal latestPrice = ticks[renderCount - 1].Price;
            bool isUp = latestPrice >= firstPrice;
            Color mainThemeColor = isUp ? Color.FromHex("#22c55e") : Color.FromHex("#ef4444");

            int sBarIdx = selectedBarStartIndex ?? (selectedTrend?.StartIndex ?? 0);
            int eBarIdx = selectedBarEndIndex ?? (selectedTrend?.EndIndex ?? sBarIdx);

            const int forwardBars = 20;
            var effectiveTrends = new List<MacroConsecutiveTrendItem>();
            if (showConsecutiveTrend || showAngleLines)
            {
                if (activeChannels != null && activeChannels.Count > 0)
                {
                    foreach (var c in activeChannels)
                    {
                        int cExtEnd = c.EndIndex + forwardBars;
                        if (Math.Max(sBarIdx, c.StartIndex) <= Math.Min(eBarIdx, cExtEnd) && !effectiveTrends.Any(x => x.Id == c.Id && x.StartIndex == c.StartIndex && x.EndIndex == c.EndIndex))
                            effectiveTrends.Add(c);
                    }
                }
                else if (selectedTrend != null)
                {
                    int trExtEnd = selectedTrend.EndIndex + forwardBars;
                    if (Math.Max(sBarIdx, selectedTrend.StartIndex) <= Math.Min(eBarIdx, trExtEnd))
                    {
                        effectiveTrends.Add(selectedTrend);
                    }
                }
            }

            double effMinVal = (double)minVal;
            double effMaxVal = (double)maxVal;

            var channelProjections = new List<(MacroConsecutiveTrendItem tr, double xS, double xE, double yUpS, double yUpE, double yLowS, double yLowE)>();
            var angleProjections = new List<(MacroConsecutiveTrendItem tr, double xS, double xE, double barPosS, double barPosE)>();

            foreach (var tr in effectiveTrends)
            {
                int trExtEnd = tr.EndIndex + forwardBars;
                int bStart = Math.Max(sBarIdx, tr.StartIndex);
                int bEnd = Math.Min(eBarIdx, trExtEnd);
                if (bStart > bEnd) continue;

                double xS, xE;
                double barPosS, barPosE;
                if (sBarIdx == eBarIdx)
                {
                    xS = 0;
                    xE = Math.Max(1, renderCount - 1);
                    barPosS = sBarIdx;
                    barPosE = sBarIdx + 1.0;
                }
                else
                {
                    int totalBars = eBarIdx - sBarIdx + 1;
                    if (bStart <= sBarIdx && bEnd >= eBarIdx)
                    {
                        xS = 0;
                        xE = Math.Max(1, renderCount - 1);
                        barPosS = sBarIdx;
                        barPosE = eBarIdx + 1.0;
                    }
                    else
                    {
                        xS = (bStart == sBarIdx)
                            ? 0
                            : ((boundaryTickIndices != null && bStart - sBarIdx - 1 < boundaryTickIndices.Count)
                                ? boundaryTickIndices[bStart - sBarIdx - 1]
                                : Math.Clamp((int)Math.Round((double)(bStart - sBarIdx) / totalBars * (renderCount - 1)), 0, renderCount - 1));

                        xE = (bEnd >= eBarIdx)
                            ? Math.Max(1, renderCount - 1)
                            : ((boundaryTickIndices != null && bEnd - sBarIdx < boundaryTickIndices.Count)
                                ? Math.Max(xS + 1, boundaryTickIndices[bEnd - sBarIdx] - 1)
                                : Math.Clamp((int)Math.Round((double)(bEnd - sBarIdx + 1) / totalBars * (renderCount - 1)), (int)xS + 1, renderCount - 1));

                        barPosS = bStart;
                        barPosE = bEnd + 1.0;
                    }
                }

                if (showConsecutiveTrend && tr.HasChannel)
                {
                    double yUpS, yUpE, yLowS, yLowE;
                    if (sBarIdx == eBarIdx)
                    {
                        yUpS = (double)(tr.SlopeK * sBarIdx + tr.UpperIntercept);
                        yUpE = (double)(tr.SlopeK * (sBarIdx + 1.0m) + tr.UpperIntercept);
                        yLowS = (double)(tr.SlopeK * sBarIdx + tr.LowerIntercept);
                        yLowE = (double)(tr.SlopeK * (sBarIdx + 1.0m) + tr.LowerIntercept);
                    }
                    else
                    {
                        yUpS = (double)(tr.SlopeK * (decimal)barPosS + tr.UpperIntercept);
                        yUpE = (double)(tr.SlopeK * (decimal)barPosE + tr.UpperIntercept);
                        yLowS = (double)(tr.SlopeK * (decimal)barPosS + tr.LowerIntercept);
                        yLowE = (double)(tr.SlopeK * (decimal)barPosE + tr.LowerIntercept);
                    }

                    effMinVal = Math.Min(effMinVal, Math.Min(yLowS, yLowE));
                    effMaxVal = Math.Max(effMaxVal, Math.Max(yUpS, yUpE));

                    channelProjections.Add((tr, xS, xE, yUpS, yUpE, yLowS, yLowE));
                }

                if (showAngleLines)
                {
                    angleProjections.Add((tr, xS, xE, barPosS, barPosE));

                    double s45 = tr.MacroSlope45 > 0 ? tr.MacroSlope45 : (MacroPlotHelper.LastSlope45 > 0 ? MacroPlotHelper.LastSlope45 : (tr.SlopeK != 0 ? (double)Math.Abs(tr.SlopeK) : (double)(tr.StartPrice * 0.005m)));
                    if (s45 > 0)
                    {
                        double dS = barPosS - tr.StartIndex;
                        double dE = barPosE - tr.StartIndex;
                        double yRef1 = (double)tr.FirstBarHigh + (tr.IsBullish ? s45 * dS : -s45 * dS);
                        double yRef2 = (double)tr.FirstBarHigh + (tr.IsBullish ? s45 * dE : -s45 * dE);
                        double yRef3 = (double)tr.FirstBarLow + (tr.IsBullish ? s45 * dS : -s45 * dS);
                        double yRef4 = (double)tr.FirstBarLow + (tr.IsBullish ? s45 * dE : -s45 * dE);
                        double curSpan = (double)(maxVal - minVal);
                        double curMid = (double)(maxVal + minVal) / 2.0;
                        double maxAllowedDev = Math.Max(curSpan * 3.0, curMid * 0.10);
                        foreach (var yr in new[] { yRef1, yRef2, yRef3, yRef4 })
                        {
                            if (yr > 0 && Math.Abs(yr - curMid) <= maxAllowedDev)
                            {
                                effMinVal = Math.Min(effMinVal, yr);
                                effMaxVal = Math.Max(effMaxVal, yr);
                            }
                        }
                    }
                }
            }

            // 计算价格坐标轴的上下边界及归一化视觉位置 (供比值曲线智能“凑近对齐”)
            double priceSpan = effMaxVal - effMinVal;
            double padY = priceSpan * 0.15;
            if (padY <= 0) padY = effMaxVal * 0.01;
            double pLeftYMin = effMinVal - padY;
            double pLeftYMax = effMaxVal + padY;
            double pLeftYSpan = pLeftYMax - pLeftYMin;

            // 价格中枢在画布上的归一化高度 (0.0=底部, 1.0=顶部)
            // 70% 权重偏向最新现价，30% 偏向区间均值，平滑限制在 [0.28, 0.72] 舒适带
            double priceCenterNorm = 0.50;
            if (pLeftYSpan > 0)
            {
                double curNorm = ((double)latestPrice - pLeftYMin) / pLeftYSpan;
                priceCenterNorm = Math.Clamp(0.70 * curNorm + 0.30 * 0.50, 0.28, 0.72);
            }

            // 2. 绘制主价格折线走势
            var scatter = plot.Add.ScatterLine(xs, ys);
            scatter.Color = mainThemeColor;
            scatter.LineWidth = 1.4f;
            scatter.MarkerSize = 0; // 高频不画圆点以保帧率

            // 2.5 若存在相关的大周期平行通道，在微观 Tick 图上同频投影通道导轨与微光多边形
            if (channelProjections.Count > 0 && renderCount >= 1)
            {
                foreach (var proj in channelProjections)
                {
                    DrawChannelRailsSegment(plot, proj.tr, proj.xS, proj.xE, proj.yUpS, proj.yUpE, proj.yLowS, proj.yLowE, chineseFont);
                }
            }

            // 2.6 若开启微观多角度趋势线，在微观 Tick 图上以第一根 K 线高低双点位投影发射趋势线 (独立控制)
            if (showAngleLines && angleProjections.Count > 0 && renderCount >= 1)
            {
                foreach (var ap in angleProjections)
                {
                    DrawAngleLines(plot, ap.tr, ap.xS, ap.xE, ap.barPosS, ap.barPosE, customAngles, chineseFont, ap.tr == selectedTrend);
                }
            }

            // 3. 绘制副图与多空比值多折线 (统一使用单一内建右轴 plot.Axes.Right，绝不动态添加新轴，确保仅有唯一的图表视窗)
            var (ratioXs, tickRatios, volRatios) = TickLongShortStats.CalculateRatioSeries(ticks, renderCount, targetSamplePoints: 1500);

            if (showRatio && ratioXs.Length > 0)
            {
                double minR = Math.Min(tickRatios.Min(), volRatios.Min());
                double maxR = Math.Max(tickRatios.Max(), volRatios.Max());
                double meanR = (minR + maxR) / 2.0;
                double spanR = Math.Max(0.20, maxR - minR);

                double yMinRatio;
                double yMaxRatio;

                if (ratioProximity)
                {
                    // 🌟 智能动态凑近对齐算法 (Smart Proximity Coupling):
                    // 1. 将比值折线视觉波幅约束在 42% 黄金舒适高度 (targetScreenSpan)
                    // 2. 将比值波动的视觉重心与价格曲线的当前视觉重心 (priceCenterNorm) 对齐凑近
                    // 彻底解决“价格和比值差别过大时，两图偏离太远不好查看”的问题！
                    double targetScreenSpan = 0.42;
                    double totalAxisSpan = spanR / targetScreenSpan;

                    // 兼顾 1.0 多空均衡基准虚线：若 1.0 偏离过远，自适应扩展坐标轴确保基准线清晰可见
                    if (meanR - 1.0 > totalAxisSpan * 0.45) totalAxisSpan = (meanR - 1.0) / 0.45;
                    if (1.0 - meanR > totalAxisSpan * 0.45) totalAxisSpan = (1.0 - meanR) / 0.45;

                    // 依据价格视觉位置反求右侧坐标轴上下限，使比值中心与价格中心凑近
                    yMinRatio = meanR - priceCenterNorm * totalAxisSpan;
                    if (yMinRatio < 0) yMinRatio = 0; // 多空比值恒为非负数
                    yMaxRatio = yMinRatio + totalAxisSpan;

                    // 确保 1.0 均衡线与比值极值安全覆盖
                    if (yMaxRatio < 1.05 && maxR <= 1.0) yMaxRatio = 1.05;
                    if (yMinRatio > 0.95 && minR >= 1.0) yMinRatio = 0.95;
                }
                else
                {
                    // 传统独立全尺度视口
                    yMinRatio = Math.Max(0, minR * 0.85);
                    yMaxRatio = Math.Max(2.0, maxR * 1.20);
                }

                // 配置唯一的右侧坐标轴
                plot.Axes.Right.Label.Text = ratioProximity ? "多空比值 [凑近] (Ratio)" : "多空比值 (Ratio)";
                plot.Axes.Right.Label.FontName = chineseFont;
                plot.Axes.Right.Label.FontSize = 8.5f;
                plot.Axes.Right.Label.ForeColor = Color.FromHex("#38bdf8");
                plot.Axes.SetLimitsY(yMinRatio, yMaxRatio, plot.Axes.Right);

                // 绘制成交量副图柱 (高度归一化至底部 18%，与比值线在单轴和谐共存，不压盖主折线与比值线)
                if (volBars.Count > 0 && maxVol > 0)
                {
                    double axisHeight = yMaxRatio - yMinRatio;
                    double volBarMaxHeight = axisHeight * 0.18;
                    for (int i = 0; i < volBars.Count; i++)
                    {
                        var originalBar = volBars[i];
                        volBars[i] = new ScottPlot.Bar
                        {
                            Position = originalBar.Position,
                            ValueBase = yMinRatio,
                            Value = yMinRatio + (originalBar.Value / maxVol) * volBarMaxHeight,
                            FillColor = originalBar.FillColor,
                            LineColor = originalBar.LineColor,
                            LineWidth = originalBar.LineWidth
                        };
                    }
                    var barPlot = plot.Add.Bars(volBars);
                    barPlot.Axes.YAxis = plot.Axes.Right;
                }

                // 1.0 多空均衡基准线 (参考平衡虚线)
                var hEquilibrium = plot.Add.HorizontalLine(1.0);
                hEquilibrium.Axes.YAxis = plot.Axes.Right;
                hEquilibrium.Color = Color.FromHex("#64748b").WithAlpha(160);
                hEquilibrium.LinePattern = LinePattern.Dashed;
                hEquilibrium.LineWidth = 1.0f;

                // 绘制多空数据量 (Tick 笔数) 比值折线 (天蓝)
                var tickRatioLine = plot.Add.ScatterLine(ratioXs, tickRatios);
                tickRatioLine.Axes.YAxis = plot.Axes.Right;
                tickRatioLine.Color = Color.FromHex("#38bdf8");
                tickRatioLine.LineWidth = 1.4f;
                tickRatioLine.MarkerSize = 0;

                // 绘制成交量比值折线 (琥珀金)
                var volRatioLine = plot.Add.ScatterLine(ratioXs, volRatios);
                volRatioLine.Axes.YAxis = plot.Axes.Right;
                volRatioLine.Color = Color.FromHex("#f59e0b");
                volRatioLine.LineWidth = 1.4f;
                volRatioLine.MarkerSize = 0;
            }
            else if (volBars.Count > 0)
            {
                // 无比值数据时的回退处理：右轴仅显示成交量
                var barPlot = plot.Add.Bars(volBars);
                barPlot.Axes.YAxis = plot.Axes.Right;

                plot.Axes.Right.Label.Text = "逐笔成交量";
                plot.Axes.Right.Label.FontName = chineseFont;
                plot.Axes.Right.Label.FontSize = 8.5f;
                plot.Axes.Right.Label.ForeColor = Color.FromHex("#94a3b8");

                double limitY = (maxVol > 0 ? maxVol : 1) * 4.0;
                plot.Axes.SetLimitsY(0, limitY, plot.Axes.Right);
            }

            // 4. 标注最高点 ▲ 与最低点 ▼
            var mHigh = plot.Add.Marker(maxIdx, (double)maxVal);
            mHigh.Shape = MarkerShape.FilledTriangleUp;
            mHigh.Size = 8;
            mHigh.Color = Color.FromHex("#ef4444");

            var tHigh = plot.Add.Text($"▲ {maxVal:F2}", maxIdx, (double)maxVal);
            tHigh.LabelFontName = chineseFont;
            tHigh.LabelFontSize = 8.0f;
            tHigh.LabelFontColor = Color.FromHex("#fca5a5");
            tHigh.LabelAlignment = Alignment.LowerCenter;
            tHigh.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);

            var mLow = plot.Add.Marker(minIdx, (double)minVal);
            mLow.Shape = MarkerShape.FilledTriangleDown;
            mLow.Size = 8;
            mLow.Color = Color.FromHex("#22c55e");

            var tLow = plot.Add.Text($"▼ {minVal:F2}", minIdx, (double)minVal);
            tLow.LabelFontName = chineseFont;
            tLow.LabelFontSize = 8.0f;
            tLow.LabelFontColor = Color.FromHex("#86efac");
            tLow.LabelAlignment = Alignment.UpperCenter;
            tLow.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);

            // 5. 标注最新 Tick 实时游标
            int lastIdx = renderCount - 1;
            var curMarker = plot.Add.Marker(lastIdx, (double)latestPrice);
            curMarker.Shape = MarkerShape.FilledCircle;
            curMarker.Size = 7;
            curMarker.Color = Color.FromHex("#f8fafc");

            decimal tickChangePct = firstPrice > 0 ? ((latestPrice - firstPrice) / firstPrice) * 100m : 0m;
            string curTag = $"● 现价: {latestPrice:F2} ({tickChangePct:+0.00;-0.00;0.00}%)";
            var tCur = plot.Add.Text(curTag, lastIdx, (double)latestPrice);
            tCur.LabelFontName = chineseFont;
            tCur.LabelFontSize = 8.5f;
            tCur.LabelFontColor = Color.FromHex("#f8fafc");
            tCur.LabelAlignment = Alignment.LowerLeft;
            tCur.LabelBackgroundColor = Color.FromHex("#1e293b").WithAlpha(0.9);
            tCur.LabelBorderColor = mainThemeColor;

            // 5.2 绘制多空盘口与折线图例 HUD 信息框
            var stats = precomputedStats ?? TickLongShortStats.Calculate(ticks, renderCount);
            string proxStatus = ratioProximity ? "已开启" : "已关闭";
            string hudText =
                $"• Tick多空比: {stats.TickRatio:F2}  [买: {stats.BuyTicks:N0} ({stats.BuyTickPct:F1}%) | 卖: {stats.SellTicks:N0} ({stats.SellTickPct:F1}%)]\n" +
                $"• 成交量比:   {stats.VolumeRatio:F2}  [买: {TickLongShortStats.FormatVolume(stats.BuyVolume)} ({stats.BuyVolumePct:F1}%) | 卖: {TickLongShortStats.FormatVolume(stats.SellVolume)} ({stats.SellVolumePct:F1}%)]\n" +
                $"• 净主动量:   {TickLongShortStats.FormatVolume(stats.NetVolume)}  | 曲线: ━ Tick比(天蓝) ━ 量比(琥珀金) ┈ 1.0均衡线  | 凑近: {proxStatus}";

            var annotation = plot.Add.Annotation(hudText, Alignment.UpperLeft);
            annotation.LabelFontName = chineseFont;
            annotation.LabelFontSize = 8.5f;
            annotation.LabelFontColor = Color.FromHex("#f1f5f9");
            annotation.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.90);
            annotation.LabelBorderColor = Color.FromHex("#38bdf8").WithAlpha(180);
            annotation.LabelBorderWidth = 1.0f;

            // 5.3 若存在平行通道，在右上角绘制醒目的通道详细信息 HUD 标牌卡片
            var effectiveChannels = effectiveTrends.Where(t => t.HasChannel).ToList();
            if (effectiveChannels.Count == 1)
            {
                AddChannelInformationAnnotation(plot, effectiveChannels[0], chineseFont, sBarIdx, eBarIdx);
            }
            else if (effectiveChannels.Count > 1)
            {
                AddMultiChannelInformationAnnotation(plot, effectiveChannels, chineseFont);
            }

            // 5.5 若存在多根 K 线的周期分界点，绘制分界垂线
            if (boundaryTickIndices != null && boundaryTickIndices.Count > 0)
            {
                foreach (int bIdx in boundaryTickIndices)
                {
                    if (bIdx > 0 && bIdx < renderCount)
                    {
                        var vLine = plot.Add.VerticalLine(bIdx);
                        vLine.Color = Color.FromHex("#38bdf8").WithAlpha(90);
                        vLine.LineWidth = 1.0f;
                        vLine.LinePattern = LinePattern.Dashed;
                    }
                }
            }

            // 6. 坐标轴自适应 (左侧主价格坐标轴)
            if (minVal <= maxVal && minVal > 0)
            {
                double xMax = Math.Max(totalAvailable, renderCount + (channelProjections.Count > 0 || angleProjections.Count > 0 ? 8 : 5));
                plot.Axes.SetLimits(-1, xMax, pLeftYMin, pLeftYMax);
            }

            // 标题
            if (!string.IsNullOrEmpty(customTitle))
            {
                plot.Title(customTitle, size: 11);
            }
            else
            {
                double progress = totalAvailable > 0 ? ((double)renderCount / totalAvailable) * 100.0 : 0;
                plot.Title(
                    $"微观 Tick 走势: {bucketStart:HH:mm:ss} ~ {bucketEnd:HH:mm:ss} (已播: {renderCount:N0}/{totalAvailable:N0} Ticks, {progress:F1}%) | Tick多空比: {stats.TickRatio:F2} | 量比: {stats.VolumeRatio:F2}",
                    size: 11);
            }
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#e2e8f0");
            plot.Axes.Left.Label.Text = "逐笔价格 (USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.FontSize = 9.0f;
            plot.Axes.Left.Label.ForeColor = Color.FromHex("#94a3b8");

            // 附着鼠标悬停交互指示器 (十字准星 + 走势吸附标记 + 悬浮详情卡片)
            if (hoverIndicator != null)
            {
                hoverIndicator.Attach(plot, chineseFont, ticks, renderCount, sBarIdx, eBarIdx);
            }
        }

        /// <summary>
        /// 全量绘制指定子周期内的所有 K 线 (支持蜡烛图与收盘折线图两种模态，含成交量副图与多空比曲线)
        /// </summary>
        public static void BuildSubPeriodKlinesPlot(
            Plot plot,
            IReadOnlyList<RawTick> ticks,
            int currentTickCursor,
            DateTime bucketStart,
            DateTime bucketEnd,
            TimeSpan subSpan,
            string coin,
            string periodTitle,
            MacroChartDisplayType displayType = MacroChartDisplayType.Candlestick,
            string customTitle = "",
            TickLongShortStats? precomputedStats = null,
            bool ratioProximity = true,
            bool showConsecutiveTrend = false,
            int consecutiveMinBars = 5,
            decimal consecutiveMinPct = 2.5m,
            bool showRatio = true,
            bool showAngleLines = true,
            IReadOnlyList<double>? customAngles = null,
            MacroConsecutiveTrendItem? selectedTrend = null,
            IReadOnlyList<MacroConsecutiveTrendItem>? activeChannels = null,
            int? selectedBarStartIndex = null,
            int? selectedBarEndIndex = null,
            IReadOnlyList<(DateTime StartTime, DateTime EndTime)>? macroBarTimes = null,
            TickHoverIndicator? hoverIndicator = null)
        {
            if (plot == null) return;

            plot.Clear();

            // 彻底清除历史残留的非标准坐标轴
            var extraAxes = plot.Axes.GetAxes().Where(ax => ax != plot.Axes.Left && ax != plot.Axes.Right && ax != plot.Axes.Bottom && ax != plot.Axes.Top).ToList();
            foreach (var ax in extraAxes)
            {
                plot.Axes.Remove(ax);
            }

            string chineseFont = GetInstalledChineseFont();
            Fonts.Default = chineseFont;

            plot.FigureBackground.Color = Color.FromHex("#0b0f19");
            plot.DataBackground.Color = Color.FromHex("#0f172a");

            plot.Axes.Color(Color.FromHex("#94a3b8"));
            plot.Grid.MajorLineColor = Color.FromHex("#1e293b").WithAlpha(160);
            plot.Grid.MinorLineColor = Color.FromHex("#1e293b").WithAlpha(60);

            int totalAvailable = ticks?.Count ?? 0;
            int renderCount = Math.Min(totalAvailable, currentTickCursor);

            if (renderCount == 0)
            {
                var emptyTxt = plot.Add.Text("当前周期暂无 Tick 或等待播放...", 0, 0);
                emptyTxt.LabelFontName = chineseFont;
                emptyTxt.LabelFontSize = 13;
                emptyTxt.LabelFontColor = Color.FromHex("#64748b");
                emptyTxt.LabelAlignment = Alignment.MiddleCenter;
                plot.Axes.SetLimits(-10, 10, -10, 10);
                return;
            }

            var availableTicks = new List<RawTick>(renderCount);
            for (int i = 0; i < renderCount; i++)
            {
                availableTicks.Add(ticks![i]);
            }

            var subBuckets = PeriodBucketLoader.SliceTicksIntoBuckets(availableTicks, subSpan);
            if (subBuckets == null || subBuckets.Count == 0)
            {
                var emptyTxt = plot.Add.Text("未生成有效子周期 K 线", 0, 0);
                emptyTxt.LabelFontName = chineseFont;
                emptyTxt.LabelFontSize = 13;
                emptyTxt.LabelFontColor = Color.FromHex("#64748b");
                emptyTxt.LabelAlignment = Alignment.MiddleCenter;
                plot.Axes.SetLimits(-10, 10, -10, 10);
                return;
            }

            int M = subBuckets.Count;
            var ohlcList = new List<OHLC>(M);
            double[] lineXs = new double[M];
            double[] lineYs = new double[M];
            double[] volumes = new double[M];
            Color[] volColors = new Color[M];

            decimal minPrice = decimal.MaxValue;
            decimal maxPrice = decimal.MinValue;
            int minPriceIdx = 0, maxPriceIdx = 0;
            double maxVolume = 0;

            for (int i = 0; i < M; i++)
            {
                var b = subBuckets[i];
                decimal o = b.Ticks[0].Price;
                decimal c = b.Ticks[^1].Price;
                decimal h = decimal.MinValue;
                decimal l = decimal.MaxValue;
                decimal v = 0m;

                for (int k = 0; k < b.Ticks.Count; k++)
                {
                    var t = b.Ticks[k];
                    if (t.Price > h) h = t.Price;
                    if (t.Price < l) l = t.Price;
                    v += t.Qty;
                }

                if (h > maxPrice) { maxPrice = h; maxPriceIdx = i; }
                if (l < minPrice) { minPrice = l; minPriceIdx = i; }

                double vDouble = (double)v;
                volumes[i] = vDouble;
                if (vDouble > maxVolume) maxVolume = vDouble;

                bool isBull = c >= o;
                volColors[i] = isBull
                    ? Color.FromHex("#22c55e").WithAlpha(130)
                    : Color.FromHex("#ef4444").WithAlpha(130);

                ohlcList.Add(new OHLC(
                    (double)o,
                    (double)h,
                    (double)l,
                    (double)c,
                    DateTime.FromOADate(i),
                    TimeSpan.FromDays(0.7)));

                lineXs[i] = i;
                lineYs[i] = (double)c;
            }

            // 绘制价格图元 (蜡烛图或折线图)
            if (displayType == MacroChartDisplayType.Candlestick)
            {
                var candlePlot = plot.Add.Candlestick(ohlcList);
                candlePlot.RisingColor = Color.FromHex("#22c55e");
                candlePlot.FallingColor = Color.FromHex("#ef4444");
            }
            else
            {
                var linePlot = plot.Add.ScatterLine(lineXs, lineYs);
                linePlot.Color = Color.FromHex("#38bdf8");
                linePlot.LineWidth = 2.0f;
                linePlot.MarkerSize = M <= 60 ? 4f : (M <= 120 ? 2.5f : 0f);
                linePlot.MarkerShape = MarkerShape.FilledCircle;
            }

            // 若存在相关的大周期平行通道或多角度趋势线，将其轨线价格纳入坐标轴上下界计算并绘制通道导轨与角度线
            int sBarIdx = selectedBarStartIndex ?? (selectedTrend?.StartIndex ?? 0);
            int eBarIdx = selectedBarEndIndex ?? (selectedTrend?.EndIndex ?? sBarIdx);

            const int forwardBars = 20;
            var effectiveTrends = new List<MacroConsecutiveTrendItem>();
            if (showConsecutiveTrend || showAngleLines)
            {
                if (activeChannels != null && activeChannels.Count > 0)
                {
                    foreach (var c in activeChannels)
                    {
                        int cExtEnd = c.EndIndex + forwardBars;
                        if (Math.Max(sBarIdx, c.StartIndex) <= Math.Min(eBarIdx, cExtEnd) && !effectiveTrends.Any(x => x.Id == c.Id && x.StartIndex == c.StartIndex && x.EndIndex == c.EndIndex))
                            effectiveTrends.Add(c);
                    }
                }
                else if (selectedTrend != null)
                {
                    int trExtEnd = selectedTrend.EndIndex + forwardBars;
                    if (Math.Max(sBarIdx, selectedTrend.StartIndex) <= Math.Min(eBarIdx, trExtEnd))
                    {
                        effectiveTrends.Add(selectedTrend);
                    }
                }
            }

            var channelProjections = new List<(MacroConsecutiveTrendItem tr, double xS, double xE, double yUpS, double yUpE, double yLowS, double yLowE)>();
            var angleProjections = new List<(MacroConsecutiveTrendItem tr, double xS, double xE, double barPosS, double barPosE)>();

            foreach (var tr in effectiveTrends)
            {
                int trExtEnd = tr.EndIndex + forwardBars;
                int bStart = Math.Max(sBarIdx, tr.StartIndex);
                int bEnd = Math.Min(eBarIdx, trExtEnd);
                if (bStart > bEnd) continue;

                double xS, xE;
                double barPosS, barPosE;
                if (sBarIdx == eBarIdx)
                {
                    xS = 0;
                    xE = Math.Max(1, M - 1);
                    barPosS = sBarIdx;
                    barPosE = sBarIdx + 1.0;
                }
                else
                {
                    int totalBars = eBarIdx - sBarIdx + 1;
                    if (bStart <= sBarIdx && bEnd >= eBarIdx)
                    {
                        xS = 0;
                        xE = Math.Max(1, M - 1);
                        barPosS = sBarIdx;
                        barPosE = eBarIdx + 1.0;
                    }
                    else
                    {
                        if (macroBarTimes != null && macroBarTimes.Count > bEnd && bEnd < macroBarTimes.Count)
                        {
                            DateTime tStart = macroBarTimes[bStart].StartTime;
                            DateTime tEnd = macroBarTimes[Math.Min(bEnd, macroBarTimes.Count - 1)].EndTime;
                            int foundStart = -1;
                            int foundEnd = -1;
                            for (int k = 0; k < M; k++)
                            {
                                if (foundStart < 0 && subBuckets[k].EndTime >= tStart) foundStart = k;
                                if (subBuckets[k].StartTime <= tEnd) foundEnd = k;
                            }
                            xS = foundStart >= 0 ? foundStart : 0;
                            xE = foundEnd >= 0 ? Math.Max(xS + 1, foundEnd) : M - 1;
                        }
                        else
                        {
                            xS = Math.Clamp((int)Math.Round((double)(bStart - sBarIdx) / totalBars * (M - 1)), 0, M - 1);
                            xE = Math.Clamp((int)Math.Round((double)(bEnd - sBarIdx + 1) / totalBars * (M - 1)), (int)xS + 1, M - 1);
                        }
                        barPosS = bStart;
                        barPosE = bEnd + 1.0;
                    }
                }

                if (showConsecutiveTrend && tr.HasChannel)
                {
                    double yUpS, yUpE, yLowS, yLowE;
                    if (sBarIdx == eBarIdx)
                    {
                        yUpS = (double)(tr.SlopeK * sBarIdx + tr.UpperIntercept);
                        yUpE = (double)(tr.SlopeK * (sBarIdx + 1.0m) + tr.UpperIntercept);
                        yLowS = (double)(tr.SlopeK * sBarIdx + tr.LowerIntercept);
                        yLowE = (double)(tr.SlopeK * (sBarIdx + 1.0m) + tr.LowerIntercept);
                    }
                    else
                    {
                        yUpS = (double)(tr.SlopeK * (decimal)barPosS + tr.UpperIntercept);
                        yUpE = (double)(tr.SlopeK * (decimal)barPosE + tr.UpperIntercept);
                        yLowS = (double)(tr.SlopeK * (decimal)barPosS + tr.LowerIntercept);
                        yLowE = (double)(tr.SlopeK * (decimal)barPosE + tr.LowerIntercept);
                    }

                    decimal chanMin = (decimal)Math.Min(yLowS, yLowE);
                    decimal chanMax = (decimal)Math.Max(yUpS, yUpE);
                    if (chanMin < minPrice) minPrice = chanMin;
                    if (chanMax > maxPrice) maxPrice = chanMax;

                    channelProjections.Add((tr, xS, xE, yUpS, yUpE, yLowS, yLowE));
                }

                if (showAngleLines)
                {
                    angleProjections.Add((tr, xS, xE, barPosS, barPosE));

                    double s45 = tr.MacroSlope45 > 0 ? tr.MacroSlope45 : (MacroPlotHelper.LastSlope45 > 0 ? MacroPlotHelper.LastSlope45 : (tr.SlopeK != 0 ? (double)Math.Abs(tr.SlopeK) : (double)(tr.StartPrice * 0.005m)));
                    if (s45 > 0)
                    {
                        double dS = barPosS - tr.StartIndex;
                        double dE = barPosE - tr.StartIndex;
                        double yRef1 = (double)tr.FirstBarHigh + (tr.IsBullish ? s45 * dS : -s45 * dS);
                        double yRef2 = (double)tr.FirstBarHigh + (tr.IsBullish ? s45 * dE : -s45 * dE);
                        double yRef3 = (double)tr.FirstBarLow + (tr.IsBullish ? s45 * dS : -s45 * dS);
                        double yRef4 = (double)tr.FirstBarLow + (tr.IsBullish ? s45 * dE : -s45 * dE);
                        double curSpan = (double)(maxPrice - minPrice);
                        double curMid = (double)(maxPrice + minPrice) / 2.0;
                        double maxAllowedDev = Math.Max(curSpan * 3.0, curMid * 0.10);
                        foreach (var yr in new[] { yRef1, yRef2, yRef3, yRef4 })
                        {
                            if (yr > 0 && Math.Abs(yr - curMid) <= maxAllowedDev)
                            {
                                if ((decimal)yr < minPrice) minPrice = (decimal)yr;
                                if ((decimal)yr > maxPrice) maxPrice = (decimal)yr;
                            }
                        }
                    }
                }
            }

            if (channelProjections.Count > 0 && M >= 1)
            {
                foreach (var proj in channelProjections)
                {
                    DrawChannelRailsSegment(plot, proj.tr, proj.xS, proj.xE, proj.yUpS, proj.yUpE, proj.yLowS, proj.yLowE, chineseFont);
                }
            }

            if (showAngleLines && angleProjections.Count > 0 && M >= 1)
            {
                foreach (var ap in angleProjections)
                {
                    DrawAngleLines(plot, ap.tr, ap.xS, ap.xE, ap.barPosS, ap.barPosE, customAngles, chineseFont, ap.tr == selectedTrend);
                }
            }

            // 绘制底部成交量柱
            decimal priceRange = maxPrice - minPrice;
            if (priceRange <= 0) priceRange = 1m;
            decimal yMinPrice = minPrice - (priceRange * 0.28m);
            decimal yMaxPrice = maxPrice + (priceRange * 0.12m);
            double volScale = maxVolume > 0 ? (double)(priceRange * 0.20m) / maxVolume : 0;

            var vBars = new List<Bar>(M);
            for (int i = 0; i < M; i++)
            {
                double val = (double)yMinPrice + (volumes[i] * volScale);
                vBars.Add(new Bar
                {
                    Position = i,
                    Value = val,
                    ValueBase = (double)yMinPrice,
                    FillColor = volColors[i],
                    LineColor = volColors[i],
                    LineWidth = 1,
                    Size = 0.7
                });
            }
            plot.Add.Bars(vBars);

            // 计算多空比值曲线并绘制于右 Y 轴 (若开启 showRatio)
            if (showRatio)
            {
                double[] ratioXs = new double[M];
                double[] tickRatios = new double[M];
                double[] volRatios = new double[M];

                int cumBuyTicks = 0, cumSellTicks = 0;
                decimal cumBuyVol = 0m, cumSellVol = 0m;
                for (int i = 0; i < M; i++)
                {
                    ratioXs[i] = i;
                    for (int k = 0; k < subBuckets[i].Ticks.Count; k++)
                    {
                        var t = subBuckets[i].Ticks[k];
                        if (!t.IsBuyerMaker)
                        {
                            cumBuyTicks++;
                            cumBuyVol += t.Qty;
                        }
                        else
                        {
                            cumSellTicks++;
                            cumSellVol += t.Qty;
                        }
                    }
                    double tR = cumSellTicks > 0 ? (double)cumBuyTicks / cumSellTicks : (cumBuyTicks > 0 ? 10.0 : 1.0);
                    double vR = cumSellVol > 0 ? (double)(cumBuyVol / cumSellVol) : (cumBuyVol > 0 ? 10.0 : 1.0);
                    tickRatios[i] = Math.Clamp(tR, 0.05, 50.0);
                    volRatios[i] = Math.Clamp(vR, 0.05, 50.0);
                }

                var rightAxis = plot.Axes.Right;
                rightAxis.Label.Text = ratioProximity ? "多空比值 [凑近] (Ratio)" : "多空比值 (Ratio)";
                rightAxis.Label.FontName = chineseFont;
                rightAxis.Label.FontSize = 8.5f;
                rightAxis.Label.ForeColor = Color.FromHex("#38bdf8");

                var lineTickR = plot.Add.ScatterLine(ratioXs, tickRatios);
                lineTickR.Axes.YAxis = rightAxis;
                lineTickR.Color = Color.FromHex("#38bdf8");
                lineTickR.LineWidth = 1.8f;

                var lineVolR = plot.Add.ScatterLine(ratioXs, volRatios);
                lineVolR.Axes.YAxis = rightAxis;
                lineVolR.Color = Color.FromHex("#f59e0b");
                lineVolR.LineWidth = 1.8f;

                double[] parityYs = new double[M];
                Array.Fill(parityYs, 1.0);
                var parityLine = plot.Add.ScatterLine(ratioXs, parityYs);
                parityLine.Axes.YAxis = rightAxis;
                parityLine.Color = Color.FromHex("#64748b");
                parityLine.LineWidth = 1.0f;
                parityLine.LinePattern = LinePattern.Dashed;

                // 智能凑近视口计算
                double minRatio = Math.Min(tickRatios.Min(), volRatios.Min());
                double maxRatio = Math.Max(tickRatios.Max(), volRatios.Max());
                double deltaR = Math.Max(0.20, maxRatio - minRatio);
                double avgR = (minRatio + maxRatio) / 2.0;

                if (ratioProximity)
                {
                    decimal latestP = subBuckets[^1].Ticks[^1].Price;
                    double pCenterNorm = Math.Clamp(0.70 * (double)((latestP - minPrice) / priceRange) + 0.15, 0.28, 0.72);
                    double targetSpan = deltaR / 0.42;
                    double rMinY = Math.Max(0.0, avgR - pCenterNorm * targetSpan);
                    double rMaxY = rMinY + targetSpan;
                    if (rMinY > 0.95) rMinY = 0.95;
                    if (rMaxY < 1.05) rMaxY = 1.05;
                    plot.Axes.SetLimitsY(rMinY, rMaxY, rightAxis);
                }
                else
                {
                    plot.Axes.SetLimitsY(Math.Min(0.4, minRatio * 0.85), Math.Max(2.0, maxRatio * 1.15), rightAxis);
                }
            }
            else
            {
                // 不显示比值：隐藏右轴标签与刻度文字
                plot.Axes.Right.Label.Text = "";
                plot.Axes.Right.TickLabelStyle.IsVisible = false;
            }

            // 标注最高价与最低价
            var mHigh = plot.Add.Marker(maxPriceIdx, (double)maxPrice);
            mHigh.Shape = MarkerShape.FilledTriangleUp;
            mHigh.Size = 7;
            mHigh.Color = Color.FromHex("#ef4444");

            var tHigh = plot.Add.Text($"▲ {maxPrice:F2}", maxPriceIdx, (double)maxPrice);
            tHigh.LabelFontName = chineseFont;
            tHigh.LabelFontSize = 8.0f;
            tHigh.LabelFontColor = Color.FromHex("#fca5a5");
            tHigh.LabelAlignment = Alignment.LowerCenter;

            var mLow = plot.Add.Marker(minPriceIdx, (double)minPrice);
            mLow.Shape = MarkerShape.FilledTriangleDown;
            mLow.Size = 7;
            mLow.Color = Color.FromHex("#22c55e");

            var tLow = plot.Add.Text($"▼ {minPrice:F2}", minPriceIdx, (double)minPrice);
            tLow.LabelFontName = chineseFont;
            tLow.LabelFontSize = 8.0f;
            tLow.LabelFontColor = Color.FromHex("#86efac");
            tLow.LabelAlignment = Alignment.UpperCenter;

            // X 轴时间刻度标签与坐标范围 (若存在大通道投影或多角度趋势线，右侧预留适当空间供标签呼吸)
            plot.Axes.SetLimitsY((double)yMinPrice, (double)yMaxPrice);
            plot.Axes.SetLimitsX(-0.8, M + (channelProjections.Count > 0 || angleProjections.Count > 0 ? 3.5 : -0.2));

            var xPos = new List<double>();
            var xLabels = new List<string>();
            int step = Math.Max(1, M / 8);
            string timeFormat = subSpan.TotalSeconds < 60 ? "HH:mm:ss" : "HH:mm";
            for (int i = 0; i < M; i += step)
            {
                xPos.Add(i);
                xLabels.Add(subBuckets[i].StartTime.ToString(timeFormat));
            }
            if (xPos.Count > 0 && xPos[^1] != M - 1)
            {
                xPos.Add(M - 1);
                xLabels.Add(subBuckets[^1].StartTime.ToString(timeFormat));
            }
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(xPos.ToArray(), xLabels.ToArray());

            // 标注 HUD 信息框
            var stats = precomputedStats ?? TickLongShortStats.Calculate(availableTicks);
            string proxStatus = ratioProximity ? "已开启" : "已关闭";
            decimal latestPrice = subBuckets[^1].Ticks[^1].Price;
            decimal firstPrice = subBuckets[0].Ticks[0].Price;
            decimal changePct = firstPrice > 0 ? ((latestPrice - firstPrice) / firstPrice) * 100m : 0m;
            string modeName = displayType == MacroChartDisplayType.Candlestick ? "蜡烛图" : "折线图";

            string hudText =
                $"• 最新价: {latestPrice:F2} ({changePct:+0.00;-0.00;0.00}%) | K线数: {M} 根 ({periodTitle} {modeName})\n" +
                $"• Tick多空比: {stats.TickRatio:F2}  [买: {stats.BuyTicks:N0} ({stats.BuyTickPct:F1}%) | 卖: {stats.SellTicks:N0} ({stats.SellTickPct:F1}%)]\n" +
                $"• 成交量比:   {stats.VolumeRatio:F2}  [买: {TickLongShortStats.FormatVolume(stats.BuyVolume)} ({stats.BuyVolumePct:F1}%) | 卖: {TickLongShortStats.FormatVolume(stats.SellVolume)} ({stats.SellVolumePct:F1}%)]\n" +
                $"• 曲线: ━ Tick比(天蓝) ━ 量比(琥珀金) ┈ 1.0均衡线  | 模式: {modeName} | 凑近: {proxStatus}";

            var annotation = plot.Add.Annotation(hudText, Alignment.UpperLeft);
            annotation.LabelFontName = chineseFont;
            annotation.LabelFontSize = 8.5f;
            annotation.LabelFontColor = Color.FromHex("#f1f5f9");
            annotation.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.90);
            annotation.LabelBorderColor = Color.FromHex("#38bdf8").WithAlpha(180);
            annotation.LabelBorderWidth = 1.0f;

            // 标注选中的大周期平行通道详情卡片 (右上角)
            var effectiveChannels = effectiveTrends.Where(t => t.HasChannel).ToList();
            if (effectiveChannels.Count == 1)
            {
                AddChannelInformationAnnotation(plot, effectiveChannels[0], chineseFont, sBarIdx, eBarIdx);
            }
            else if (effectiveChannels.Count > 1)
            {
                AddMultiChannelInformationAnnotation(plot, effectiveChannels, chineseFont);
            }

            string defaultTitle = string.IsNullOrEmpty(customTitle)
                ? $"{coin} | 微观走势 [{periodTitle} - 共 {M} 根K线, {modeName}] ({bucketStart:HH:mm}~{bucketEnd:HH:mm})"
                : customTitle;
            plot.Title(defaultTitle, size: 11);
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#e2e8f0");
            plot.Axes.Left.Label.Text = $"{modeName}价格 (USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.FontSize = 9.0f;
            plot.Axes.Left.Label.ForeColor = Color.FromHex("#94a3b8");

            // 附着鼠标悬停交互指示器 (子周期 K 线模式)
            if (hoverIndicator != null)
            {
                hoverIndicator.AttachSubPeriod(plot, chineseFont, subBuckets, periodTitle);
            }
        }

        private static void AddChannelInformationAnnotation(
            Plot plot,
            MacroConsecutiveTrendItem tr,
            string chineseFont,
            int? selectedBarStartIndex = null,
            int? selectedBarEndIndex = null)
        {
            bool isBull = tr.IsBullish;
            string dirText = isBull ? "▲ 连续上涨 (多头通道)" : "▼ 连续下跌 (空头通道)";
            decimal diffPrice = tr.EndPrice - tr.StartPrice;
            decimal channelH = tr.ChannelHeight;
            decimal channelHPct = tr.StartPrice > 0 ? (channelH / tr.StartPrice * 100m) : 0m;
            decimal amplitude = tr.MaxHigh - tr.MinLow;

            string spanDesc = (selectedBarStartIndex.HasValue && selectedBarEndIndex.HasValue &&
                               (selectedBarStartIndex.Value != tr.StartIndex || selectedBarEndIndex.Value != tr.EndIndex))
                ? $"• 选区视窗: {(selectedBarStartIndex.Value == selectedBarEndIndex.Value ? $"Bar #{selectedBarStartIndex.Value}" : $"Bar #{selectedBarStartIndex.Value}~#{selectedBarEndIndex.Value}")} (通道内部微观分段)\n"
                : "";

            string channelHud =
                $"⭐【大周期平行通道详情】\n" +
                spanDesc +
                $"• 方向形态: {dirText} | 确立节点: Bar #{tr.ConfirmedBarIndex}\n" +
                $"• 覆盖跨度: Bar #{tr.StartIndex}~#{tr.EndIndex} (共 {tr.BarCount} 根大周期 K 线)\n" +
                $"• 价格变动: {tr.StartPrice:F2} -> {tr.EndPrice:F2} ({diffPrice:+0.00;-0.00;0.00} USDT, {tr.PriceChangePct:+0.00;-0.00;0.00}%)\n" +
                $"• 极值空间: 最高 {tr.MaxHigh:F2} | 最低 {tr.MinLow:F2} | 振幅 {amplitude:F2} USDT\n" +
                $"• 通道拟合: 基准 {tr.ChannelBaseBars} 根拟合 | 斜率 k = {tr.SlopeK:+0.000000;-0.000000;0.000000} USDT/Bar\n" +
                $"• 轨道截距: 上轨 {tr.UpperIntercept:F2} | 下轨 {tr.LowerIntercept:F2} | 中轨 {((tr.UpperIntercept + tr.LowerIntercept) / 2m):F2}\n" +
                $"• 通道高度: {channelH:F2} USDT ({channelHPct:F2}%)";

            var channelBox = plot.Add.Annotation(channelHud, Alignment.UpperRight);
            channelBox.LabelFontName = chineseFont;
            channelBox.LabelFontSize = 8.5f;
            channelBox.LabelFontColor = Color.FromHex("#fbbf24");
            channelBox.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.92);
            channelBox.LabelBorderColor = Color.FromHex("#fbbf24").WithAlpha(220);
            channelBox.LabelBorderWidth = 1.2f;
        }

        private static void AddMultiChannelInformationAnnotation(
            Plot plot,
            IReadOnlyList<MacroConsecutiveTrendItem> channels,
            string chineseFont)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"⭐【选区内存在 {channels.Count} 个大周期平行通道】");
            for (int i = 0; i < channels.Count; i++)
            {
                var tr = channels[i];
                string dirText = tr.IsBullish ? "▲ 连涨" : "▼ 连跌";
                sb.AppendLine($"• 通道 {i + 1}: {dirText} #{tr.StartIndex}~#{tr.EndIndex} ({tr.BarCount}根, {tr.PriceChangePct:+0.00;-0.00;0.00}%) | k={tr.SlopeK:+0.0000;-0.0000} | 高度 {tr.ChannelHeight:F2} USDT");
            }
            var channelBox = plot.Add.Annotation(sb.ToString().TrimEnd(), Alignment.UpperRight);
            channelBox.LabelFontName = chineseFont;
            channelBox.LabelFontSize = 8.5f;
            channelBox.LabelFontColor = Color.FromHex("#fbbf24");
            channelBox.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.92);
            channelBox.LabelBorderColor = Color.FromHex("#fbbf24").WithAlpha(220);
            channelBox.LabelBorderWidth = 1.2f;
        }

        private static void DrawChannelRailsSegment(
            Plot plot,
            MacroConsecutiveTrendItem tr,
            double xStart,
            double xEnd,
            double yUpStart,
            double yUpEnd,
            double yLowStart,
            double yLowEnd,
            string chineseFont)
        {
            if (xEnd <= xStart)
            {
                xStart = Math.Max(0, xStart - 0.4);
                xEnd = xEnd + 0.4;
            }

            double yMidStart = (yUpStart + yLowStart) / 2.0;
            double yMidEnd = (yUpEnd + yLowEnd) / 2.0;

            // ① 通道微光多边形 (琥珀金透明度 22)
            var corridorCoords = new Coordinates[]
            {
                new Coordinates(xStart, yUpStart),
                new Coordinates(xEnd, yUpEnd),
                new Coordinates(xEnd, yLowEnd),
                new Coordinates(xStart, yLowStart)
            };
            var corridorPoly = plot.Add.Polygon(corridorCoords);
            corridorPoly.FillColor = Color.FromHex("#fbbf24").WithAlpha(22);
            corridorPoly.LineWidth = 0;

            // ② 上轨实线
            var lineUp = plot.Add.Line(xStart, yUpStart, xEnd, yUpEnd);
            lineUp.Color = Color.FromHex("#fbbf24");
            lineUp.LineWidth = 1.5f;
            lineUp.LinePattern = LinePattern.Solid;

            // ③ 下轨实线
            var lineLow = plot.Add.Line(xStart, yLowStart, xEnd, yLowEnd);
            lineLow.Color = Color.FromHex("#fbbf24");
            lineLow.LineWidth = 1.5f;
            lineLow.LinePattern = LinePattern.Solid;

            // ④ 中轴中枢虚线
            var lineMid = plot.Add.Line(xStart, yMidStart, xEnd, yMidEnd);
            lineMid.Color = Color.FromHex("#fbbf24").WithAlpha(170);
            lineMid.LineWidth = 1.0f;
            lineMid.LinePattern = LinePattern.Dashed;

            // ⑤ 右侧端点价格标签
            var tUp = plot.Add.Text($"上轨: {yUpEnd:F2}", xEnd, yUpEnd);
            tUp.LabelFontName = chineseFont;
            tUp.LabelFontSize = 8.0f;
            tUp.LabelFontColor = Color.FromHex("#fbbf24");
            tUp.LabelAlignment = Alignment.LowerRight;
            tUp.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.85);

            var tLow = plot.Add.Text($"下轨: {yLowEnd:F2}", xEnd, yLowEnd);
            tLow.LabelFontName = chineseFont;
            tLow.LabelFontSize = 8.0f;
            tLow.LabelFontColor = Color.FromHex("#fbbf24");
            tLow.LabelAlignment = Alignment.UpperRight;
            tLow.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.85);
        }

        private static void DrawProjectedChannelRails(
            Plot plot,
            MacroConsecutiveTrendItem tr,
            int maxX,
            string chineseFont,
            out double yUpStart,
            out double yUpEnd,
            out double yLowStart,
            out double yLowEnd)
        {
            yUpStart = (double)(tr.SlopeK * tr.StartIndex + tr.UpperIntercept);
            yUpEnd = (double)(tr.SlopeK * tr.EndIndex + tr.UpperIntercept);
            yLowStart = (double)(tr.SlopeK * tr.StartIndex + tr.LowerIntercept);
            yLowEnd = (double)(tr.SlopeK * tr.EndIndex + tr.LowerIntercept);

            double xStart = 0;
            double xEnd = maxX;
            if (maxX <= 0)
            {
                xStart = -0.4;
                xEnd = 0.4;
            }

            DrawChannelRailsSegment(plot, tr, xStart, xEnd, yUpStart, yUpEnd, yLowStart, yLowEnd, chineseFont);
        }

        private static void DrawAngleLines(
            Plot plot,
            MacroConsecutiveTrendItem tr,
            double xS,
            double xE,
            double barPosS,
            double barPosE,
            IReadOnlyList<double>? customAngles,
            string chineseFont,
            bool isSelected = false)
        {
            if (plot == null || tr == null || xE <= xS) return;

            bool isBull = tr.IsBullish;
            decimal firstHigh = tr.FirstBarHigh > 0 ? tr.FirstBarHigh : tr.StartPrice;
            decimal firstLow = tr.FirstBarLow > 0 ? tr.FirstBarLow : tr.StartPrice;
            double yHigh = (double)firstHigh;
            double yLow = (double)firstLow;

            double slope45 = tr.MacroSlope45;
            if (slope45 <= 0 && MacroPlotHelper.LastSlope45 > 0)
            {
                slope45 = MacroPlotHelper.LastSlope45;
            }
            if (slope45 <= 0)
            {
                slope45 = tr.SlopeK != 0 ? (double)Math.Abs(tr.SlopeK) : (double)(tr.StartPrice * 0.005m);
            }
            if (slope45 <= 0) slope45 = 1.0;

            var angles = (customAngles != null && customAngles.Count > 0) ? customAngles : new double[] { 25.0, 45.0, 65.0 };

            var anchorPoints = isBull
                ? new (string label, double price, bool isPrimary)[] { ("L", yLow, true), ("H", yHigh, false) }
                : new (string label, double price, bool isPrimary)[] { ("H", yHigh, true), ("L", yLow, false) };

            double deltaBarS = barPosS - tr.StartIndex;
            double deltaBarE = barPosE - tr.StartIndex;
            if (deltaBarE <= deltaBarS) deltaBarE = deltaBarS + 1.0;

            foreach (var anchor in anchorPoints)
            {
                // 如果当前正好包含趋势的第一根 K 线的起点，在微观图上也绘制圆点锚点
                if (Math.Abs(deltaBarS) < 1e-4)
                {
                    var mAnchor = plot.Add.Marker(xS, anchor.price);
                    mAnchor.Shape = MarkerShape.FilledCircle;
                    mAnchor.Size = isSelected ? 6 : 4;
                    mAnchor.Color = isBull ? Color.FromHex("#10b981") : Color.FromHex("#ef4444");
                }

                foreach (var deg in angles)
                {
                    double rad = deg * Math.PI / 180.0;
                    double slopeDeg = Math.Tan(rad) * slope45;
                    bool is45 = Math.Abs(deg - 45.0) < 0.01;

                    double yS = isBull
                        ? anchor.price + slopeDeg * deltaBarS
                        : anchor.price - slopeDeg * deltaBarS;

                    double yE = isBull
                        ? anchor.price + slopeDeg * deltaBarE
                        : anchor.price - slopeDeg * deltaBarE;

                    // 截断保护：如果已经完全小于等于 0 则跳过
                    if (yS <= 0 && yE <= 0) continue;

                    double actualXE = xE;
                    double actualYE = yE;
                    if (yS > 0 && yE < 0)
                    {
                        double frac = yS / (yS - yE);
                        actualXE = xS + frac * (xE - xS);
                        actualYE = 0;
                    }

                    if (actualXE <= xS) continue;

                    Color rayColor;
                    if (isBull)
                    {
                        rayColor = is45
                            ? Color.FromHex("#10b981") // 45° 翡翠绿
                            : (deg < 45.0 ? Color.FromHex("#34d399") : Color.FromHex("#a3e635"));
                    }
                    else
                    {
                        rayColor = is45
                            ? Color.FromHex("#ef4444") // 45° 烈火红
                            : (deg < 45.0 ? Color.FromHex("#fb923c") : Color.FromHex("#f43f5e"));
                    }

                    if (isSelected)
                    {
                        rayColor = is45 ? Color.FromHex("#fbbf24") : rayColor;
                    }

                    byte rayAlpha = isSelected ? (byte)230 : (byte)160;
                    var pattern = is45 ? LinePattern.Solid : (deg < 45.0 ? LinePattern.Dashed : LinePattern.Dotted);
                    float lineWidth = is45 ? (isSelected ? 1.8f : 1.3f) : (isSelected ? 1.4f : 1.0f);

                    var angleLine = plot.Add.Line(xS, yS, actualXE, actualYE);
                    angleLine.Color = rayColor.WithAlpha(rayAlpha);
                    angleLine.LineWidth = lineWidth;
                    angleLine.LinePattern = pattern;

                    // 绘制末端角度标注
                    string signStr = isBull ? "+" : "-";
                    string endLabel = $"{anchor.label} {signStr}{deg:0.##}°";
                    var txtAngle = plot.Add.Text(endLabel, actualXE, actualYE);
                    txtAngle.LabelFontName = chineseFont;
                    txtAngle.LabelFontSize = 7.5f;
                    txtAngle.LabelBold = is45;
                    txtAngle.LabelFontColor = rayColor;
                    txtAngle.LabelAlignment = isBull ? Alignment.LowerLeft : Alignment.UpperLeft;
                    txtAngle.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.85);
                }
            }
        }
    }

    /// <summary>
    /// 微观 Tick / 子周期 K 线图表鼠标悬停交互指示器 (十字准星 + 走势吸附标记 + 悬浮微型数据看板)
    /// 严格使用系统最佳中文字体渲染，彻底杜绝乱码与豆腐块
    /// </summary>
    public class TickHoverIndicator
    {
        public Crosshair? Crosshair { get; private set; }
        public Marker? SnapMarker { get; private set; }
        public Annotation? HoverCard { get; private set; }

        public IReadOnlyList<RawTick>? ActiveTicks { get; private set; }
        public int ActiveRenderCount { get; private set; }
        public IReadOnlyList<PeriodBucket>? ActiveSubBuckets { get; private set; }
        public bool IsSubPeriodMode { get; private set; }
        public string PeriodTitle { get; private set; } = "";
        public int? SelectedBarStartIndex { get; private set; }
        public int? SelectedBarEndIndex { get; private set; }

        public bool IsAttached => Crosshair != null && SnapMarker != null && HoverCard != null;

        public void Attach(
            Plot plot,
            string chineseFont,
            IReadOnlyList<RawTick>? ticks,
            int renderCount,
            int? selectedBarStartIndex = null,
            int? selectedBarEndIndex = null)
        {
            if (plot == null) return;

            ActiveTicks = ticks;
            ActiveRenderCount = renderCount;
            ActiveSubBuckets = null;
            IsSubPeriodMode = false;
            PeriodTitle = "";
            SelectedBarStartIndex = selectedBarStartIndex;
            SelectedBarEndIndex = selectedBarEndIndex;

            Crosshair = plot.Add.Crosshair(0, 0);
            Crosshair.LineColor = Color.FromHex("#38bdf8").WithAlpha(0.65);
            Crosshair.LinePattern = LinePattern.Dashed;
            Crosshair.LineWidth = 1.0f;
            Crosshair.IsVisible = false;

            SnapMarker = plot.Add.Marker(0, 0);
            SnapMarker.Shape = MarkerShape.FilledCircle;
            SnapMarker.Size = 8;
            SnapMarker.Color = Color.FromHex("#38bdf8");
            SnapMarker.IsVisible = false;

            HoverCard = plot.Add.Annotation("", Alignment.LowerLeft);
            HoverCard.LabelFontName = chineseFont;
            HoverCard.LabelFontSize = 8.5f;
            HoverCard.LabelFontColor = Color.FromHex("#f1f5f9");
            HoverCard.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.92);
            HoverCard.LabelBorderColor = Color.FromHex("#38bdf8").WithAlpha(200);
            HoverCard.LabelBorderWidth = 1.0f;
            HoverCard.IsVisible = false;
        }

        public void AttachSubPeriod(
            Plot plot,
            string chineseFont,
            IReadOnlyList<PeriodBucket>? subBuckets,
            string periodTitle)
        {
            if (plot == null) return;

            ActiveTicks = null;
            ActiveRenderCount = 0;
            ActiveSubBuckets = subBuckets;
            IsSubPeriodMode = true;
            PeriodTitle = periodTitle;
            SelectedBarStartIndex = null;
            SelectedBarEndIndex = null;

            Crosshair = plot.Add.Crosshair(0, 0);
            Crosshair.LineColor = Color.FromHex("#38bdf8").WithAlpha(0.65);
            Crosshair.LinePattern = LinePattern.Dashed;
            Crosshair.LineWidth = 1.0f;
            Crosshair.IsVisible = false;

            SnapMarker = plot.Add.Marker(0, 0);
            SnapMarker.Shape = MarkerShape.FilledCircle;
            SnapMarker.Size = 8;
            SnapMarker.Color = Color.FromHex("#38bdf8");
            SnapMarker.IsVisible = false;

            HoverCard = plot.Add.Annotation("", Alignment.LowerLeft);
            HoverCard.LabelFontName = chineseFont;
            HoverCard.LabelFontSize = 8.5f;
            HoverCard.LabelFontColor = Color.FromHex("#f1f5f9");
            HoverCard.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.92);
            HoverCard.LabelBorderColor = Color.FromHex("#38bdf8").WithAlpha(200);
            HoverCard.LabelBorderWidth = 1.0f;
            HoverCard.IsVisible = false;
        }

        public void Clear()
        {
            if (Crosshair != null) Crosshair.IsVisible = false;
            if (SnapMarker != null) SnapMarker.IsVisible = false;
            if (HoverCard != null) HoverCard.IsVisible = false;
        }

        public bool UpdateHover(
            double mouseX,
            double mouseY,
            string chineseFont,
            out string headerBadgeText,
            out System.Drawing.Color badgeColor)
        {
            headerBadgeText = "";
            badgeColor = System.Drawing.Color.FromArgb(148, 163, 184);

            if (!IsAttached) return false;

            if (IsSubPeriodMode)
            {
                if (ActiveSubBuckets == null || ActiveSubBuckets.Count == 0) return false;

                int barIdx = Math.Clamp((int)Math.Round(mouseX), 0, ActiveSubBuckets.Count - 1);
                var b = ActiveSubBuckets[barIdx];
                var k = b.FinalKline;

                decimal o = k?.Open ?? (b.Ticks.Count > 0 ? b.Ticks[0].Price : 0);
                decimal h = k?.High ?? (b.Ticks.Count > 0 ? b.Ticks.Max(t => t.Price) : 0);
                decimal l = k?.Low ?? (b.Ticks.Count > 0 ? b.Ticks.Min(t => t.Price) : 0);
                decimal c = k?.Close ?? (b.Ticks.Count > 0 ? b.Ticks[^1].Price : 0);
                decimal v = k?.Volume ?? (b.Ticks.Sum(t => t.Qty));
                decimal q = k?.QuoteVolume ?? (b.Ticks.Sum(t => t.QuoteQty));
                int tCount = b.Ticks.Count;

                bool isBarUp = c >= o;
                decimal barChg = o > 0 ? (c - o) / o * 100m : 0m;
                string barChgStr = (barChg >= 0 ? "+" : "") + $"{barChg:F2}%";
                string dirStr = isBarUp ? "🟢阳线" : "🔴阴线";

                badgeColor = isBarUp
                    ? System.Drawing.Color.FromArgb(74, 222, 128)
                    : System.Drawing.Color.FromArgb(248, 113, 113);
                string subTimeFmt = (ActiveSubBuckets.Count > 0 && (ActiveSubBuckets[0].EndTime - ActiveSubBuckets[0].StartTime).TotalSeconds < 60) ? "HH:mm:ss" : "HH:mm";
                headerBadgeText = $"[Bar #{barIdx + 1}/{ActiveSubBuckets.Count}] {b.StartTime.ToString(subTimeFmt)}~{b.EndTime.ToString(subTimeFmt)} | 开:{o:F2} 高:{h:F2} 低:{l:F2} 收:{c:F2} ({barChgStr} {dirStr}) | 量:{v:N2} | {tCount:N0} Ticks";

                string cardText =
                    $"⭐【微观 K 线巡检】 Bar #{barIdx + 1}/{ActiveSubBuckets.Count} ({PeriodTitle})\n" +
                    $"• 时段范围: {b.StartTime:HH:mm:ss} ~ {b.EndTime:HH:mm:ss}\n" +
                    $"• 价格OHLC: 开 {o:F2} | 高 {h:F2} | 低 {l:F2} | 收 {c:F2}\n" +
                    $"• 波段涨跌: {barChgStr} ({dirStr}) | 振幅: {(h - l):F2} USDT\n" +
                    $"• 量能统计: {v:N4} | 额: {q:N2} USDT | 笔数: {tCount:N0} Ticks";

                HoverCard!.LabelFontName = chineseFont;
                HoverCard.Text = cardText;
                HoverCard.LabelBorderColor = isBarUp ? Color.FromHex("#10b981") : Color.FromHex("#ef4444");
                HoverCard.IsVisible = true;

                Crosshair!.Position = new Coordinates(barIdx, (double)c);
                Crosshair.LineColor = (isBarUp ? Color.FromHex("#10b981") : Color.FromHex("#ef4444")).WithAlpha(0.65);
                Crosshair.IsVisible = true;

                SnapMarker!.Coordinates = new Coordinates(barIdx, (double)c);
                SnapMarker.Color = isBarUp ? Color.FromHex("#22c55e") : Color.FromHex("#ef4444");
                SnapMarker.IsVisible = true;

                return true;
            }
            else
            {
                if (ActiveTicks == null || ActiveRenderCount <= 0) return false;

                int tickIdx = Math.Clamp((int)Math.Round(mouseX), 0, ActiveRenderCount - 1);
                var tick = ActiveTicks[tickIdx];

                DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).UtcDateTime;
                string timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string shortTime = dt.ToString("HH:mm:ss.fff");
                decimal price = tick.Price;
                decimal qty = tick.Qty;
                decimal quote = tick.QuoteQty > 0 ? tick.QuoteQty : (tick.Price * tick.Qty);
                bool isBuy = !tick.IsBuyerMaker;
                string sideName = isBuy ? "🟢主动买入" : "🔴主动卖出";

                decimal firstPrice = ActiveTicks[0].Price;
                decimal diff = firstPrice > 0 ? (price - firstPrice) / firstPrice * 100m : 0m;
                string diffStr = (diff >= 0 ? "+" : "") + $"{diff:F2}%";

                badgeColor = isBuy
                    ? System.Drawing.Color.FromArgb(74, 222, 128)
                    : System.Drawing.Color.FromArgb(248, 113, 113);
                headerBadgeText = $"[#{tickIdx + 1}/{ActiveRenderCount}] {shortTime} | 价格: {price:F2} ({diffStr}) | 量: {qty:F4} | {sideName} | 额: {quote:F2} USDT";

                string barContext = (SelectedBarStartIndex.HasValue && SelectedBarEndIndex.HasValue)
                    ? (SelectedBarStartIndex == SelectedBarEndIndex ? $"Bar #{SelectedBarStartIndex} 内部" : $"选区 Bar #{SelectedBarStartIndex}~#{SelectedBarEndIndex}")
                    : "";
                string contextLine = string.IsNullOrEmpty(barContext) ? "" : $" ({barContext})";

                string cardText =
                    $"⭐【逐笔 Tick 巡检】 #{tickIdx + 1}/{ActiveRenderCount}{contextLine}\n" +
                    $"• 时间戳: {timeStr}\n" +
                    $"• 成交价: {price:F2} USDT  ({diffStr})\n" +
                    $"• 成交量: {qty:F4}  |  成交额: {quote:F2} USDT\n" +
                    $"• 方向:   {sideName}";

                HoverCard!.LabelFontName = chineseFont;
                HoverCard.Text = cardText;
                HoverCard.LabelBorderColor = isBuy ? Color.FromHex("#10b981") : Color.FromHex("#ef4444");
                HoverCard.IsVisible = true;

                Crosshair!.Position = new Coordinates(tickIdx, (double)price);
                Crosshair.LineColor = (isBuy ? Color.FromHex("#10b981") : Color.FromHex("#ef4444")).WithAlpha(0.65);
                Crosshair.IsVisible = true;

                SnapMarker!.Coordinates = new Coordinates(tickIdx, (double)price);
                SnapMarker.Color = isBuy ? Color.FromHex("#22c55e") : Color.FromHex("#ef4444");
                SnapMarker.IsVisible = true;

                return true;
            }
        }
    }
}
