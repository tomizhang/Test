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
            float lineWidth = 0.8f,
            TrendLine? selectedTrendLine = null,
            int? selectedKlineIndex = null,
            ChartType chartType = ChartType.Candlestick)
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

            // 3. 绘制主价格走势 (支持标准红绿蜡烛图 Candlestick 或收盘价折线图 Line)
            if (chartType == ChartType.Candlestick)
            {
                var ohlcList = new List<OHLC>(count);
                for (int i = 0; i < count; i++)
                {
                    var k = klines[i];
                    double x = startGlobalIndex + i;
                    var ohlc = new OHLC(
                        (double)k.Open,
                        (double)k.High,
                        (double)k.Low,
                        (double)k.Close,
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
                    xs[i] = startGlobalIndex + i;
                    ys[i] = (double)klines[i].Close;
                }

                var priceLine = plot.Add.Scatter(xs, ys);
                priceLine.LineWidth = Math.Max(0.5f, lineWidth);
                priceLine.MarkerSize = 0;
                priceLine.Color = Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
                priceLine.LegendText = $"价格收盘折线 ({count:N0}根)";
            }

            // 4. 绘制高低点极值标记 (分 3 个等级：L1 小/浅淡色，L2 中/鲜亮色，L3 大/深浓色；大小逐渐增加，颜色浓度递增)
            int peaksL1Count = 0, peaksL2Count = 0, peaksL3Count = 0;
            var p1Xs = new List<double>(); var p1Ys = new List<double>();
            var p2Xs = new List<double>(); var p2Ys = new List<double>();
            var p3Xs = new List<double>(); var p3Ys = new List<double>();

            if (peaks != null && peaks.Count > 0)
            {
                foreach (var p in peaks)
                {
                    if (p.Index >= startGlobalIndex && p.Index <= endGlobalIndex)
                    {
                        if (p.Level == 3)
                        {
                            p3Xs.Add(p.Index);
                            p3Ys.Add((double)p.Price);
                            peaksL3Count++;
                        }
                        else if (p.Level == 2)
                        {
                            p2Xs.Add(p.Index);
                            p2Ys.Add((double)p.Price);
                            peaksL2Count++;
                        }
                        else
                        {
                            p1Xs.Add(p.Index);
                            p1Ys.Add((double)p.Price);
                            peaksL1Count++;
                        }
                    }
                }

                // 一级高点 (L1): 大小 4, 浅粉红色 (低浓度)
                if (p1Xs.Count > 0)
                {
                    var p1Scatter = plot.Add.Scatter(p1Xs.ToArray(), p1Ys.ToArray());
                    p1Scatter.MarkerShape = MarkerShape.FilledTriangleUp;
                    p1Scatter.MarkerSize = 4;
                    p1Scatter.Color = Color.FromHex("#fca5a5").WithAlpha(0.65);
                    p1Scatter.LineWidth = 0;
                    p1Scatter.LegendText = $"一级高点 L1 ({peaksL1Count})";
                }

                // 二级高点 (L2): 大小 7, 鲜亮大红 (中浓度)
                if (p2Xs.Count > 0)
                {
                    var p2Scatter = plot.Add.Scatter(p2Xs.ToArray(), p2Ys.ToArray());
                    p2Scatter.MarkerShape = MarkerShape.FilledTriangleUp;
                    p2Scatter.MarkerSize = 7;
                    p2Scatter.Color = Color.FromHex("#ef4444");
                    p2Scatter.LineWidth = 0;
                    p2Scatter.LegendText = $"二级高点 L2 ({peaksL2Count})";
                }

                // 三级高点 (L3): 大小 11, 深浓大红 (高浓度)
                if (p3Xs.Count > 0)
                {
                    var p3Scatter = plot.Add.Scatter(p3Xs.ToArray(), p3Ys.ToArray());
                    p3Scatter.MarkerShape = MarkerShape.FilledTriangleUp;
                    p3Scatter.MarkerSize = 11;
                    p3Scatter.Color = Color.FromHex("#991b1b");
                    p3Scatter.LineWidth = 0;
                    p3Scatter.LegendText = $"三级高点 L3 ({peaksL3Count})";
                }
            }

            int valleysL1Count = 0, valleysL2Count = 0, valleysL3Count = 0;
            var v1Xs = new List<double>(); var v1Ys = new List<double>();
            var v2Xs = new List<double>(); var v2Ys = new List<double>();
            var v3Xs = new List<double>(); var v3Ys = new List<double>();

            if (valleys != null && valleys.Count > 0)
            {
                foreach (var v in valleys)
                {
                    if (v.Index >= startGlobalIndex && v.Index <= endGlobalIndex)
                    {
                        if (v.Level == 3)
                        {
                            v3Xs.Add(v.Index);
                            v3Ys.Add((double)v.Price);
                            valleysL3Count++;
                        }
                        else if (v.Level == 2)
                        {
                            v2Xs.Add(v.Index);
                            v2Ys.Add((double)v.Price);
                            valleysL2Count++;
                        }
                        else
                        {
                            v1Xs.Add(v.Index);
                            v1Ys.Add((double)v.Price);
                            valleysL1Count++;
                        }
                    }
                }

                // 一级低点 (L1): 大小 4, 浅青绿色 (低浓度)
                if (v1Xs.Count > 0)
                {
                    var v1Scatter = plot.Add.Scatter(v1Xs.ToArray(), v1Ys.ToArray());
                    v1Scatter.MarkerShape = MarkerShape.FilledTriangleDown;
                    v1Scatter.MarkerSize = 4;
                    v1Scatter.Color = Color.FromHex("#86efac").WithAlpha(0.65);
                    v1Scatter.LineWidth = 0;
                    v1Scatter.LegendText = $"一级低点 L1 ({valleysL1Count})";
                }

                // 二级低点 (L2): 大小 7, 鲜亮翠绿 (中浓度)
                if (v2Xs.Count > 0)
                {
                    var v2Scatter = plot.Add.Scatter(v2Xs.ToArray(), v2Ys.ToArray());
                    v2Scatter.MarkerShape = MarkerShape.FilledTriangleDown;
                    v2Scatter.MarkerSize = 7;
                    v2Scatter.Color = Color.FromHex("#22c55e");
                    v2Scatter.LineWidth = 0;
                    v2Scatter.LegendText = $"二级低点 L2 ({valleysL2Count})";
                }

                // 三级低点 (L3): 大小 11, 深浓翡翠绿 (高浓度)
                if (v3Xs.Count > 0)
                {
                    var v3Scatter = plot.Add.Scatter(v3Xs.ToArray(), v3Ys.ToArray());
                    v3Scatter.MarkerShape = MarkerShape.FilledTriangleDown;
                    v3Scatter.MarkerSize = 11;
                    v3Scatter.Color = Color.FromHex("#15803d");
                    v3Scatter.LineWidth = 0;
                    v3Scatter.LegendText = $"三级低点 L3 ({valleysL3Count})";
                }
            }

            // 5. 绘制趋势线 (选中线: 红色高亮加粗；趋势通道: 红色线宽0.8f；特殊趋势线: 紫色线宽0.8f；三点确认线: 金黄色加粗；触发线: 绿色加粗；活跃阻力: 橙红；活跃支撑: 青色；已击穿: 灰暗色)
            int resistanceDrawn = 0;
            int supportDrawn = 0;
            int triggeredDrawn = 0;
            int threePointConfirmedDrawn = 0;
            int channelLinesDrawn = 0;
            int specialLinesDrawn = 0;
            bool channelLegendSet = false;
            bool specialLegendSet = false;
            bool selectedLineDrawn = false;

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
                    // 1. 若为已被击穿的趋势线 (包括紫色特殊趋势线与普通击穿线，CollidedKlineIndex >= 0)，则延长至发生穿透时的 K 线位置严格终止
                    // 2. 若为未发生击穿的活跃趋势线 (CollidedKlineIndex == -1)，则向右延长至当前图表最右端 (endGlobalIndex)
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

                    bool isSelected = selectedTrendLine.HasValue &&
                                      line.X1 == selectedTrendLine.Value.X1 &&
                                      line.X2 == selectedTrendLine.Value.X2 &&
                                      line.Type == selectedTrendLine.Value.Type;

                    if (isSelected)
                    {
                        // 🌟 用户点击选中的趋势线: 鲜亮大红色 + 加粗突出显示
                        linePlot.Color = Color.FromHex("#ef4444"); // Red 500
                        linePlot.LineWidth = Math.Max(2.5f, lineWidth * 3.5f);
                        selectedLineDrawn = true;
                    }
                    else if (line.IsInChannel)
                    {
                        // 趋势通道线条: 红色高亮显示，线宽 0.8f
                        linePlot.Color = Color.FromHex("#ef4444"); // 鲜亮红色 Red 500
                        linePlot.LineWidth = 0.8f;
                        if (!channelLegendSet)
                        {
                            linePlot.LegendText = "趋势通道 (线宽 0.8f)";
                            channelLegendSet = true;
                        }
                        channelLinesDrawn++;
                    }
                    else if (line.IsSpecialTrendLine)
                    {
                        // 特殊结构突破趋势线: 紫色高亮显示，线宽 0.8f
                        linePlot.Color = Color.FromHex("#a855f7"); // Purple 500
                        linePlot.LineWidth = 0.8f;
                        if (!specialLegendSet)
                        {
                            linePlot.LegendText = "特殊趋势线 (线宽 0.8f)";
                            specialLegendSet = true;
                        }
                        specialLinesDrawn++;
                    }
                    else if (line.IsTriggered)
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

            // 绘制用户点击选中的趋势线 (如果不在已绘制的前200条列表中，单独绘制红色高亮线与端点标记)
            if (selectedTrendLine.HasValue)
            {
                var sel = selectedTrendLine.Value;
                int selEndX = sel.CollidedKlineIndex >= 0 ? sel.CollidedKlineIndex : Math.Max(sel.X2, endGlobalIndex);

                if (!selectedLineDrawn && selEndX >= startGlobalIndex && sel.X1 <= endGlobalIndex)
                {
                    var selLinePlot = plot.Add.Line(sel.X1, (double)sel.Y1, selEndX, (double)sel.GetPriceAt(selEndX));
                    selLinePlot.Color = Color.FromHex("#ef4444"); // 鲜亮大红色
                    selLinePlot.LineWidth = Math.Max(2.5f, lineWidth * 3.5f);
                }

                // 绘制选中趋势线的端点红色标记
                var selXs = new List<double>();
                var selYs = new List<double>();
                if (sel.X1 >= startGlobalIndex && sel.X1 <= endGlobalIndex)
                {
                    selXs.Add(sel.X1);
                    selYs.Add((double)sel.Y1);
                }
                if (sel.X2 >= startGlobalIndex && sel.X2 <= endGlobalIndex)
                {
                    selXs.Add(sel.X2);
                    selYs.Add((double)sel.Y2);
                }
                if (sel.IsThreePointConfirmed && sel.X3 >= startGlobalIndex && sel.X3 <= endGlobalIndex)
                {
                    selXs.Add(sel.X3);
                    selYs.Add(sel.Y3 > 0m ? (double)sel.Y3 : (double)sel.GetPriceAt(sel.X3));
                }

                if (selXs.Count > 0)
                {
                    var selScatter = plot.Add.Scatter(selXs.ToArray(), selYs.ToArray());
                    selScatter.MarkerShape = MarkerShape.FilledCircle;
                    selScatter.MarkerSize = 7;
                    selScatter.Color = Color.FromHex("#ef4444"); // 红色端点
                    selScatter.LineWidth = 0;
                    selScatter.LegendText = $"选中趋势线 (#{sel.X1}->#{sel.X2})";
                }

                // 🌟 若选中的趋势线存在通道，同时高亮显示整个趋势通道 (包括对侧轨道)
                TrendLine? channelPartner = null;
                if (trendLines != null && trendLines.Count > 0)
                {
                    if (sel.ChannelId > 0)
                    {
                        for (int i = 0; i < trendLines.Count; i++)
                        {
                            var l = trendLines[i];
                            if (l.ChannelId == sel.ChannelId && l.Type != sel.Type)
                            {
                                channelPartner = l;
                                break;
                            }
                        }
                    }

                    if (!channelPartner.HasValue)
                    {
                        for (int i = 0; i < trendLines.Count; i++)
                        {
                            var l = trendLines[i];
                            if (l.Type != sel.Type && l.IsValid)
                            {
                                var rLine = sel.IsResistance ? sel : l;
                                var sLine = sel.IsSupport ? sel : l;
                                var testChannels = TrendLineHelper.DetectTrendChannels(
                                    new List<TrendLine> { rLine },
                                    new List<TrendLine> { sLine },
                                    endGlobalIndex);
                                if (testChannels.Count > 0)
                                {
                                    channelPartner = l;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (channelPartner.HasValue)
                {
                    var partner = channelPartner.Value;
                    int partnerEndX = partner.CollidedKlineIndex >= 0 ? partner.CollidedKlineIndex : Math.Max(partner.X2, endGlobalIndex);

                    if (partnerEndX >= startGlobalIndex && partner.X1 <= endGlobalIndex)
                    {
                        var partnerLinePlot = plot.Add.Line(partner.X1, (double)partner.Y1, partnerEndX, (double)partner.GetPriceAt(partnerEndX));
                        partnerLinePlot.Color = Color.FromHex("#ef4444"); // 鲜亮大红色
                        partnerLinePlot.LineWidth = Math.Max(2.0f, lineWidth * 2.8f);
                    }

                    // 绘制对侧轨道的端点标记
                    var pXs = new List<double>();
                    var pYs = new List<double>();
                    if (partner.X1 >= startGlobalIndex && partner.X1 <= endGlobalIndex)
                    {
                        pXs.Add(partner.X1);
                        pYs.Add((double)partner.Y1);
                    }
                    if (partner.X2 >= startGlobalIndex && partner.X2 <= endGlobalIndex)
                    {
                        pXs.Add(partner.X2);
                        pYs.Add((double)partner.Y2);
                    }
                    if (partner.IsThreePointConfirmed && partner.X3 >= startGlobalIndex && partner.X3 <= endGlobalIndex)
                    {
                        pXs.Add(partner.X3);
                        pYs.Add(partner.Y3 > 0m ? (double)partner.Y3 : (double)partner.GetPriceAt(partner.X3));
                    }

                    if (pXs.Count > 0)
                    {
                        var pScatter = plot.Add.Scatter(pXs.ToArray(), pYs.ToArray());
                        pScatter.MarkerShape = MarkerShape.FilledCircle;
                        pScatter.MarkerSize = 6;
                        pScatter.Color = Color.FromHex("#f87171"); // 亮红色端点
                        pScatter.LineWidth = 0;
                        string partnerType = partner.IsResistance ? "阻力上轨" : "支撑下轨";
                        pScatter.LegendText = $"关联通道对轨 ({partnerType} #{partner.X1}->#{partner.X2})";
                    }
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
                starScatter.LegendText = $"三点确认 ({thirdPointXs.Count})";
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
                    buyScatter.LegendText = $"开多信号 ({longSignalsDrawn})";
                }

                if (sellXs.Count > 0)
                {
                    var sellScatter = plot.Add.Scatter(sellXs.ToArray(), sellYs.ToArray());
                    sellScatter.MarkerShape = MarkerShape.FilledSquare;
                    sellScatter.MarkerSize = 6;
                    sellScatter.Color = Color.FromHex("#f43f5e"); // 玫红开空标记
                    sellScatter.LineWidth = 0;
                    sellScatter.LegendText = $"开空信号 ({shortSignalsDrawn})";
                }
            }

            // 6.5 绘制用户点击选中的 K 线高亮光标 (垂直青色线 + 高亮点)
            if (selectedKlineIndex.HasValue)
            {
                int selIdx = selectedKlineIndex.Value;
                if (selIdx >= startGlobalIndex && selIdx <= endGlobalIndex)
                {
                    int localIdx = selIdx - startGlobalIndex;
                    if (localIdx >= 0 && localIdx < count)
                    {
                        var k = klines[localIdx];
                        double yLow = (double)k.Low;
                        double yHigh = (double)k.High;
                        double yClose = (double)k.Close;

                        if (yLow == yHigh)
                        {
                            yLow = yClose * 0.999;
                            yHigh = yClose * 1.001;
                        }

                        // 垂直高亮光标
                        var klineBarLine = plot.Add.Line(selIdx, yLow, selIdx, yHigh);
                        klineBarLine.Color = Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
                        klineBarLine.LineWidth = Math.Max(2.5f, lineWidth * 3.0f);

                        // 选中的收盘价高亮点 (青色光晕大圆点)
                        var selScatter = plot.Add.Scatter(new double[] { selIdx }, new double[] { yClose });
                        selScatter.MarkerShape = MarkerShape.FilledCircle;
                        selScatter.MarkerSize = 10;
                        selScatter.Color = Color.FromHex("#38bdf8");
                        selScatter.LineWidth = 0;
                        selScatter.LegendText = $"选中K线 (Bar #{selIdx})";
                    }
                }
            }

            // 7. 添加左上角结构化描述摘要卡片 (强制中文字体)
            string timeRange = $"{TimeHelper.FromUnixTimeMilliseconds(klines[0].OpenTime):yyyy-MM-dd HH:mm} ~ {TimeHelper.FromUnixTimeMilliseconds(klines[count - 1].CloseTime):yyyy-MM-dd HH:mm}";
            string channelSummary = channelLinesDrawn > 0 ? $", 趋势通道={channelLinesDrawn / 2}组" : "";
            string specialSummary = specialLinesDrawn > 0 ? $", 特殊趋势线={specialLinesDrawn}条" : "";
            int totalPeaks = peaksL1Count + peaksL2Count + peaksL3Count;
            int totalValleys = valleysL1Count + valleysL2Count + valleysL3Count;
            string peaksSummary = $"高点={totalPeaks} (L1:{peaksL1Count}/L2:{peaksL2Count}/L3:{peaksL3Count})";
            string valleysSummary = $"低点={totalValleys} (L1:{valleysL1Count}/L2:{valleysL2Count}/L3:{valleysL3Count})";
            string fullSummary = $"【量化结构指标摘要】\n" +
                                 $"• 时间跨度: {timeRange} (UTC+0)\n" +
                                 $"• K线根数: {count:N0} 根 | 价格区间: {klines[0].Close:F2} -> {klines[count - 1].Close:F2}\n" +
                                 $"• 极值高低点: {peaksSummary}, {valleysSummary}\n" +
                                 $"• 绘制趋势线: 阻力线={resistanceDrawn}条, 支撑线={supportDrawn}条 (三点共线={threePointConfirmedDrawn}条{channelSummary}{specialSummary})\n" +
                                 $"• 开仓信号: 多单={longSignalsDrawn}笔, 空单={shortSignalsDrawn}笔 (总计 {longSignalsDrawn + shortSignalsDrawn} 笔)\n" +
                                 $"• 策略备注: {summaryDescription}";

            var annotation = plot.Add.Annotation(fullSummary, Alignment.UpperLeft);
            annotation.LabelStyle.FontName = chineseFont;
            annotation.LabelStyle.FontSize = 12;
            annotation.LabelStyle.ForeColor = Color.FromHex("#f8fafc");
            annotation.LabelStyle.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.88);
            annotation.LabelStyle.BorderColor = Color.FromHex("#475569");
            annotation.LabelStyle.BorderWidth = 1.5f;
            annotation.LabelStyle.ShadowColor = Colors.Transparent;

            // 8. 设置标题、坐标轴标签与图例 (右上角图例字体设为白色)
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
            plot.Legend.FontColor = Color.FromHex("#ffffff"); // 🌟 右上角图例字体颜色设为纯白
            plot.Legend.FontSize = 12;
            plot.Legend.BackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.90);
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
            float lineWidth = 0.8f,
            ChartType chartType = ChartType.Candlestick)
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
                lineWidth,
                chartType: chartType);
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
            float lineWidth = 0.8f,
            ChartType chartType = ChartType.Candlestick)
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
                lineWidth: lineWidth,
                chartType: chartType);

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
