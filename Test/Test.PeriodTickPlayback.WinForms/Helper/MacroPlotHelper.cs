using ScottPlot;
using System;
using System.Collections.Generic;
using System.Linq;
using Test.PeriodTickPlayback.WinForms.Models;
using Color = ScottPlot.Color;

namespace Test.PeriodTickPlayback.WinForms.Helper
{
    /// <summary>
    /// 大周期 K 线图表专业绘制助手
    /// 遵从用户明确要求：“大图除了成交量，其它ma之类的指标全部不要”
    /// 仅呈现纯净金融蜡烛图 (Candlesticks) + 成交量 (Volume)，严禁添加 MA 均线或任何多余附图指标
    /// </summary>
    public static class MacroPlotHelper
    {
        private static readonly string[] PreferredChineseFonts = { "Microsoft YaHei", "PingFang SC", "SimHei", "Segoe UI" };

        public static string GetInstalledChineseFont()
        {
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

        /// <summary>
        /// 极速构建大周期 K 线或折线与成交量图表 (遵从规范：纯净无额外 MA 等指标)
        /// </summary>
        public static void BuildMacroPlot(
            Plot plot,
            IReadOnlyList<MacroKline> completedBars,
            FormingMacroKline? formingBar,
            string coin,
            string periodTitle,
            MacroChartDisplayType displayType = MacroChartDisplayType.Candlestick,
            bool showVolume = true,
            bool autoFollow = true,
            bool showConsecutiveTrend = true,
            int consecutiveMinBars = 5,
            decimal consecutiveMinPct = 2.5m,
            int? selectedStartIndex = null,
            int? selectedEndIndex = null,
            int channelExtensionBars = 15)
        {
            if (plot == null) return;

            AxisLimits oldLimits = plot.Axes.GetLimits();
            plot.Clear();

            string chineseFont = GetInstalledChineseFont();

            // 1. 暗黑 TradingView 极客配色
            plot.FigureBackground.Color = Color.FromHex("#0b0f19"); // 极深黑蓝
            plot.DataBackground.Color = Color.FromHex("#0f172a");   // 主背景 Slate 900

            plot.Axes.Color(Color.FromHex("#94a3b8"));
            plot.Grid.MajorLineColor = Color.FromHex("#1e293b").WithAlpha(180);
            plot.Grid.MinorLineColor = Color.FromHex("#1e293b").WithAlpha(80);

            int completedCount = completedBars?.Count ?? 0;
            bool hasForming = formingBar != null && formingBar.TicksProcessed > 0;
            int totalDisplayCount = completedCount + (hasForming ? 1 : 0);

            if (totalDisplayCount == 0)
            {
                var emptyTxt = plot.Add.Text("等待载入或启动回放...", 0, 0);
                emptyTxt.LabelFontName = chineseFont;
                emptyTxt.LabelFontSize = 14;
                emptyTxt.LabelFontColor = Color.FromHex("#64748b");
                emptyTxt.LabelAlignment = Alignment.MiddleCenter;
                plot.Axes.SetLimits(-10, 10, -10, 10);
                return;
            }

            // 2. 构造数据列表 (K线实体 / 折线坐标 / 成交量)
            var ohlcList = new List<OHLC>(totalDisplayCount);
            var barIndices = new List<int>(totalDisplayCount);
            var volumes = new List<double>(totalDisplayCount);
            var volColors = new List<Color>(totalDisplayCount);

            double maxVolume = 0;
            decimal minPrice = decimal.MaxValue;
            decimal maxPrice = decimal.MinValue;

            // 2.1 添加已定型输出的大周期 K 线
            if (completedBars != null)
            {
                for (int i = 0; i < completedBars.Count; i++)
                {
                    var b = completedBars[i];
                    ohlcList.Add(new OHLC(
                        (double)b.Open,
                        (double)b.High,
                        (double)b.Low,
                        (double)b.Close,
                        DateTime.FromOADate(i), // 使用序号作为 X 轴连续刻度
                        TimeSpan.FromDays(0.7)));

                    barIndices.Add(i);
                    double v = (double)b.Volume;
                    volumes.Add(v);
                    if (v > maxVolume) maxVolume = v;

                    if (b.Low < minPrice) minPrice = b.Low;
                    if (b.High > maxPrice) maxPrice = b.High;

                    volColors.Add(b.IsBullish
                        ? Color.FromHex("#22c55e").WithAlpha(120)  // 绿色多头量
                        : Color.FromHex("#ef4444").WithAlpha(120)); // 红色空头量
                }
            }

            // 2.2 若当前周期桶内正在播放 Tick，展示当前正在凝聚形成的实时蜡烛/变动点
            int formingX = completedCount;
            if (hasForming)
            {
                ohlcList.Add(new OHLC(
                    (double)formingBar!.Open,
                    (double)formingBar.High,
                    (double)formingBar.Low,
                    (double)formingBar.CurrentPrice,
                    DateTime.FromOADate(formingX),
                    TimeSpan.FromDays(0.7)));

                barIndices.Add(formingX);
                double v = (double)formingBar.Volume;
                volumes.Add(v);
                if (v > maxVolume) maxVolume = v;

                if (formingBar.Low < minPrice) minPrice = formingBar.Low;
                if (formingBar.High > maxPrice) maxPrice = formingBar.High;

                volColors.Add(formingBar.IsBullish
                    ? Color.FromHex("#3b82f6").WithAlpha(150)  // 形成中以明亮蓝点缀
                    : Color.FromHex("#f59e0b").WithAlpha(150));
            }

            // 3. 根据显示类型分别绘制：蜡烛图 (Candlestick) 或 收盘折线图 (LineChart)
            if (displayType == MacroChartDisplayType.Candlestick)
            {
                var candlePlot = plot.Add.Candlestick(ohlcList);
                candlePlot.RisingColor = Color.FromHex("#22c55e");   // 涨：翠绿
                candlePlot.FallingColor = Color.FromHex("#ef4444");  // 跌：火红
            }
            else // MacroChartDisplayType.LineChart
            {
                double[] lineXs = new double[totalDisplayCount];
                double[] lineYs = new double[totalDisplayCount];

                for (int i = 0; i < completedCount; i++)
                {
                    lineXs[i] = i;
                    lineYs[i] = (double)completedBars![i].Close;
                }
                if (hasForming)
                {
                    lineXs[completedCount] = formingX;
                    lineYs[completedCount] = (double)formingBar!.CurrentPrice;
                }

                var linePlot = plot.Add.ScatterLine(lineXs, lineYs);
                linePlot.Color = Color.FromHex("#38bdf8"); // 科技天蓝
                linePlot.LineWidth = 2.0f;
                linePlot.MarkerSize = totalDisplayCount <= 60 ? 4f : (totalDisplayCount <= 120 ? 2.5f : 0f);
                linePlot.MarkerShape = MarkerShape.FilledCircle;

                // 标出最新点高亮脉冲圆点
                if (lineXs.Length > 0)
                {
                    int lastIdx = lineXs.Length - 1;
                    var latestDot = plot.Add.Marker(lineXs[lastIdx], lineYs[lastIdx]);
                    latestDot.Shape = MarkerShape.FilledCircle;
                    latestDot.Size = 7;
                    latestDot.Color = hasForming ? Color.FromHex("#f59e0b") : Color.FromHex("#38bdf8");
                }

                // 调整折线图纵坐标极值基于收盘价，确保充满视口
                if (lineYs.Length > 0)
                {
                    minPrice = (decimal)lineYs.Min();
                    maxPrice = (decimal)lineYs.Max();
                }
            }

            // 4. 绘制成交量副图 (纯粹柱状成交量，依附于右侧 Y 轴)
            if (showVolume && volumes.Count > 0)
            {
                var volBars = new List<ScottPlot.Bar>(volumes.Count);
                for (int i = 0; i < volumes.Count; i++)
                {
                    volBars.Add(new ScottPlot.Bar
                    {
                        Position = barIndices[i],
                        Value = volumes[i],
                        FillColor = volColors[i],
                        LineColor = volColors[i].WithAlpha(200),
                        LineWidth = 0.8f
                    });
                }

                var barPlot = plot.Add.Bars(volBars);
                barPlot.Axes.YAxis = plot.Axes.Right; // 右轴独立映射成交量高度

                plot.Axes.Right.Label.Text = "成交量 (Volume)";
                plot.Axes.Right.Label.FontName = chineseFont;
                plot.Axes.Right.Label.FontSize = 9.5f;
                plot.Axes.Right.Label.ForeColor = Color.FromHex("#94a3b8");

                // 将成交量限制在底部 25% 空间，绝不遮挡上方价格主图
                double volLimitY = (maxVolume > 0 ? maxVolume : 1) * 3.8;
                plot.Axes.SetLimitsY(0, volLimitY, plot.Axes.Right);
            }

            // 4.5 绘制连续上涨 / 连续下跌波段标记 (满足门槛：连续 N 根及以上且累计幅度 >= X%)
            if (showConsecutiveTrend && totalDisplayCount >= consecutiveMinBars)
            {
                var trends = MacroConsecutiveTrendDetector.ScanTrends(
                    completedBars,
                    formingBar,
                    consecutiveMinBars,
                    consecutiveMinPct);

                for (int tIdx = 0; tIdx < trends.Count; tIdx++)
                {
                    var tr = trends[tIdx];
                    int sIdx = tr.StartIndex;
                    int eIdx = tr.EndIndex;
                    if (sIdx < 0 || eIdx >= totalDisplayCount || sIdx > eIdx) continue;

                    bool isBull = tr.IsBullish;
                    var themeColor = isBull
                        ? Color.FromHex("#10b981") // 连涨翡翠绿
                        : Color.FromHex("#ef4444"); // 连跌火红

                    // ① 确立点三角形标记 (▲ / ▼)
                    int cIdx = tr.ConfirmedBarIndex;
                    if (cIdx >= sIdx && cIdx <= eIdx)
                    {
                        double confPrice = (double)(cIdx < completedCount
                            ? completedBars![cIdx].Close
                            : (formingBar != null ? formingBar.CurrentPrice : tr.EndPrice));

                        var confMarker = plot.Add.Marker(cIdx, confPrice);
                        confMarker.Shape = isBull ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                        confMarker.Size = 9;
                        confMarker.Color = themeColor;
                    }

                    double tagY;
                    if (tr.HasChannel)
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

                        // ② 平行通道内部半透明微光填充
                        var channelCoords = new Coordinates[]
                        {
                            new Coordinates(sIdx, yUpStart),
                            new Coordinates(eIdx, yUpEnd),
                            new Coordinates(eIdx, yLowEnd),
                            new Coordinates(sIdx, yLowStart)
                        };
                        var channelPoly = plot.Add.Polygon(channelCoords);
                        channelPoly.FillColor = themeColor.WithAlpha(28);
                        channelPoly.LineWidth = 0;

                        // ③ 平行通道上轨与下轨实线 (线宽 1.2f)
                        var lineUp = plot.Add.Line(sIdx, yUpStart, eIdx, yUpEnd);
                        lineUp.Color = themeColor;
                        lineUp.LineWidth = 1.2f;
                        lineUp.LinePattern = LinePattern.Solid;

                        var lineLow = plot.Add.Line(sIdx, yLowStart, eIdx, yLowEnd);
                        lineLow.Color = themeColor;
                        lineLow.LineWidth = 1.2f;
                        lineLow.LinePattern = LinePattern.Solid;

                        // ④ 平行通道中轨虚线
                        var lineMid = plot.Add.Line(sIdx, yMidStart, eIdx, yMidEnd);
                        lineMid.Color = themeColor.WithAlpha(170);
                        lineMid.LineWidth = 0.8f;
                        lineMid.LinePattern = LinePattern.Dashed;

                        // ⑤ 平行通道向右充分延长 (突破截止限制，向前延伸至最新柱后充足未来空间)
                        int forwardBars = Math.Max(5, channelExtensionBars);
                        bool isLatestTrend = (tIdx == trends.Count - 1);
                        int extEnd = isLatestTrend
                            ? Math.Max(totalDisplayCount - 1, eIdx) + forwardBars
                            : Math.Min(Math.Max(totalDisplayCount - 1, eIdx) + forwardBars, eIdx + Math.Max(20, forwardBars));

                        if (extEnd > eIdx)
                        {
                            double yUpExt = (double)(slopeK * extEnd + upperB);
                            double yLowExt = (double)(slopeK * extEnd + lowerB);
                            double yMidExt = (double)(slopeK * extEnd + (upperB + lowerB) / 2m);

                            // 上轨延长虚线
                            var extUp = plot.Add.Line(eIdx, yUpEnd, extEnd, yUpExt);
                            extUp.Color = themeColor.WithAlpha(140);
                            extUp.LineWidth = 1.0f;
                            extUp.LinePattern = LinePattern.Dashed;

                            // 下轨延长虚线
                            var extLow = plot.Add.Line(eIdx, yLowEnd, extEnd, yLowExt);
                            extLow.Color = themeColor.WithAlpha(140);
                            extLow.LineWidth = 1.0f;
                            extLow.LinePattern = LinePattern.Dashed;

                            // 中轨延长虚线 (中枢推进轴)
                            var extMid = plot.Add.Line(eIdx, yMidEnd, extEnd, yMidExt);
                            extMid.Color = themeColor.WithAlpha(100);
                            extMid.LineWidth = 0.8f;
                            extMid.LinePattern = LinePattern.Dotted;

                            // 延伸通道微光填充多边形 (Alpha=14，极轻柔通透)
                            var extPolyCoords = new Coordinates[]
                            {
                                new Coordinates(eIdx, yUpEnd),
                                new Coordinates(extEnd, yUpExt),
                                new Coordinates(extEnd, yLowExt),
                                new Coordinates(eIdx, yLowEnd)
                            };
                            var extPoly = plot.Add.Polygon(extPolyCoords);
                            extPoly.FillColor = themeColor.WithAlpha(14);
                            extPoly.LineWidth = 0;
                        }

                        tagY = isBull ? Math.Max(yUpStart, yUpEnd) : Math.Min(yLowStart, yLowEnd);
                    }
                    else
                    {
                        // 备用简易矩形包络
                        double yTop = (double)tr.MaxHigh;
                        double yBot = (double)tr.MinLow;
                        var corridorCoords = new Coordinates[]
                        {
                            new Coordinates(sIdx, yTop),
                            new Coordinates(eIdx, yTop),
                            new Coordinates(eIdx, yBot),
                            new Coordinates(sIdx, yBot)
                        };
                        var corridorPoly = plot.Add.Polygon(corridorCoords);
                        corridorPoly.FillColor = themeColor.WithAlpha(25);
                        corridorPoly.LineColor = themeColor.WithAlpha(120);
                        corridorPoly.LineWidth = 0.8f;
                        corridorPoly.LinePattern = LinePattern.Dotted;

                        tagY = isBull ? yTop : yBot;
                    }

                    // ⑥ 起止端点圆点标记
                    double pStart = (double)tr.StartPrice;
                    double pEnd = (double)tr.EndPrice;
                    var mStart = plot.Add.Marker(sIdx, pStart);
                    mStart.Shape = MarkerShape.FilledCircle;
                    mStart.Size = 5;
                    mStart.Color = themeColor;

                    var mEnd = plot.Add.Marker(eIdx, pEnd);
                    mEnd.Shape = MarkerShape.FilledCircle;
                    mEnd.Size = 5;
                    mEnd.Color = themeColor;

                    // ⑦ 醒目悬浮信息气泡标签 (连涨 / 连跌 及 平行通道基准根数)
                    string baseTag = tr.HasChannel ? $" [平行通道(基准{tr.ChannelBaseBars}根)]" : "";
                    string tag = isBull
                        ? $"▲ 连涨 {tr.BarCount}根 (+{tr.PriceChangePct:F2}%){baseTag}"
                        : $"▼ 连跌 {tr.BarCount}根 ({tr.PriceChangePct:F2}%){baseTag}";

                    double tagX = (sIdx + eIdx) / 2.0;

                    var txtTag = plot.Add.Text(tag, tagX, tagY);
                    txtTag.LabelFontName = chineseFont;
                    txtTag.LabelFontSize = 9.0f;
                    txtTag.LabelBold = true;
                    txtTag.LabelFontColor = isBull ? Color.FromHex("#34d399") : Color.FromHex("#fca5a5");
                    txtTag.LabelAlignment = isBull ? Alignment.LowerCenter : Alignment.UpperCenter;
                    txtTag.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.88);
                    txtTag.LabelBorderColor = themeColor;
                    txtTag.LabelBorderWidth = 1f;
                }
            }

            // 5. 若有正在形成的 K 线，在其上方标出实时变动的形成态提示
            if (hasForming)
            {
                double tagY = displayType == MacroChartDisplayType.LineChart
                    ? (double)formingBar!.CurrentPrice
                    : (double)formingBar!.High;

                string formingTag = $"[形成中... {formingBar!.ProgressPct:F1}%]\n现价: {formingBar.CurrentPrice:F2} ({formingBar.ChangePct:+0.00;-0.00;0.00}%)";
                var tagText = plot.Add.Text(formingTag, formingX, tagY);
                tagText.LabelFontName = chineseFont;
                tagText.LabelFontSize = 8.5f;
                tagText.LabelFontColor = Color.FromHex("#38bdf8"); // 天蓝
                tagText.LabelAlignment = Alignment.LowerCenter;
                tagText.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.85);
                tagText.LabelBorderColor = Color.FromHex("#38bdf8");
                tagText.LabelBorderWidth = 1f;
            }

            // 5.5 绘制用户点击或 Shift 连续多选的 K 线高亮选中区域
            if (selectedStartIndex.HasValue && selectedEndIndex.HasValue && totalDisplayCount > 0)
            {
                int sIdx = Math.Clamp(Math.Min(selectedStartIndex.Value, selectedEndIndex.Value), 0, totalDisplayCount - 1);
                int eIdx = Math.Clamp(Math.Max(selectedStartIndex.Value, selectedEndIndex.Value), 0, totalDisplayCount - 1);

                decimal selHigh = decimal.MinValue;
                decimal selLow = decimal.MaxValue;
                for (int i = sIdx; i <= eIdx; i++)
                {
                    if (i < completedCount && completedBars != null)
                    {
                        if (completedBars[i].High > selHigh) selHigh = completedBars[i].High;
                        if (completedBars[i].Low < selLow) selLow = completedBars[i].Low;
                    }
                    else if (hasForming)
                    {
                        if (formingBar!.High > selHigh) selHigh = formingBar.High;
                        if (formingBar.Low < selLow) selLow = formingBar.Low;
                    }
                }

                if (selHigh >= selLow && selHigh > 0)
                {
                    double selTop = (double)selHigh;
                    double selBot = (double)selLow;
                    double selLeft = sIdx - 0.45;
                    double selRight = eIdx + 0.45;

                    // ① 选中区域半透明高亮填充多边形 (科技天蓝)
                    var selCoords = new Coordinates[]
                    {
                        new Coordinates(selLeft, selTop),
                        new Coordinates(selRight, selTop),
                        new Coordinates(selRight, selBot),
                        new Coordinates(selLeft, selBot)
                    };
                    var selPoly = plot.Add.Polygon(selCoords);
                    selPoly.FillColor = Color.FromHex("#38bdf8").WithAlpha(40);
                    selPoly.LineColor = Color.FromHex("#38bdf8").WithAlpha(210);
                    selPoly.LineWidth = 1.6f;
                    selPoly.LinePattern = LinePattern.Dashed;

                    // ② 左右两端垂直虚线标记
                    var vLeft = plot.Add.VerticalLine(selLeft);
                    vLeft.Color = Color.FromHex("#38bdf8").WithAlpha(140);
                    vLeft.LineWidth = 1.0f;
                    vLeft.LinePattern = LinePattern.Dotted;

                    var vRight = plot.Add.VerticalLine(selRight);
                    vRight.Color = Color.FromHex("#38bdf8").WithAlpha(140);
                    vRight.LineWidth = 1.0f;
                    vRight.LinePattern = LinePattern.Dotted;

                    // ③ 悬浮标牌
                    int selCount = eIdx - sIdx + 1;
                    string selTitle = selCount == 1
                        ? $"[选中 Bar #{sIdx}]"
                        : $"[选中 Bar #{sIdx} ~ #{eIdx} (共 {selCount} 根)]";

                    var txtSel = plot.Add.Text(selTitle, (sIdx + eIdx) / 2.0, selTop);
                    txtSel.LabelFontName = chineseFont;
                    txtSel.LabelFontSize = 8.5f;
                    txtSel.LabelBold = true;
                    txtSel.LabelFontColor = Color.FromHex("#38bdf8");
                    txtSel.LabelAlignment = Alignment.LowerCenter;
                    txtSel.LabelBackgroundColor = Color.FromHex("#0b0f19").WithAlpha(0.92);
                    txtSel.LabelBorderColor = Color.FromHex("#38bdf8");
                    txtSel.LabelBorderWidth = 1f;
                }
            }

            // 6. 坐标轴范围与自适应视口
            if (totalDisplayCount > 0)
            {
                int windowBars = 60; // 默认可视 60 根大周期 K 线
                double rightMargin = showConsecutiveTrend ? Math.Max(8.0, channelExtensionBars * 0.7) : 2.5;
                double xMax = totalDisplayCount + rightMargin;
                double xMin = Math.Max(-0.5, totalDisplayCount - windowBars);

                if (autoFollow)
                {
                    // 🌟 核心修复：自动跟随模式下，Y 轴范围必须基于【当前窗口内实际可视的 K 线】，
                    // 彻底解决全局历史远古极值过大导致当前最新蜡烛被压缩成细线、看起来像跟随失效的问题！
                    int startVisibleBar = (int)Math.Max(0, Math.Floor(xMin));
                    decimal winMinPrice = decimal.MaxValue;
                    decimal winMaxPrice = decimal.MinValue;
                    double winMaxVolume = 0;

                    for (int i = startVisibleBar; i < totalDisplayCount; i++)
                    {
                        if (i < completedCount && completedBars != null)
                        {
                            var b = completedBars[i];
                            if (b.Low < winMinPrice) winMinPrice = b.Low;
                            if (b.High > winMaxPrice) winMaxPrice = b.High;
                            if ((double)b.Volume > winMaxVolume) winMaxVolume = (double)b.Volume;
                        }
                        else if (hasForming)
                        {
                            if (formingBar!.Low < winMinPrice) winMinPrice = formingBar.Low;
                            if (formingBar.High > winMaxPrice) winMaxPrice = formingBar.High;
                            if ((double)formingBar.Volume > winMaxVolume) winMaxVolume = (double)formingBar.Volume;
                        }
                    }

                    if (winMinPrice <= winMaxPrice && winMinPrice > 0)
                    {
                        double padY = (double)(winMaxPrice - winMinPrice) * 0.12;
                        if (padY <= 0) padY = (double)winMaxPrice * 0.02;
                        plot.Axes.SetLimits(xMin, xMax, (double)winMinPrice - padY, (double)winMaxPrice + padY);

                        if (showVolume)
                        {
                            double volLimitY = (winMaxVolume > 0 ? winMaxVolume : 1) * 4.2;
                            plot.Axes.SetLimitsY(0, volLimitY, plot.Axes.Right);
                        }
                    }
                }
                else
                {
                    // 🌟 非自动跟随模式：若之前已有有效视口坐标，则保留当前平移与缩放，不被图表重绘冲刷！
                    if (oldLimits.Right > oldLimits.Left && oldLimits.Top > oldLimits.Bottom)
                    {
                        plot.Axes.SetLimits(oldLimits);
                    }
                    else if (minPrice <= maxPrice && minPrice > 0)
                    {
                        double padY = (double)(maxPrice - minPrice) * 0.12;
                        if (padY <= 0) padY = (double)maxPrice * 0.02;
                        plot.Axes.SetLimits(-0.5, totalDisplayCount + rightMargin, (double)minPrice - padY, (double)maxPrice + padY);
                    }
                }
            }

            // 7. 纯净标题 (无任何额外 MA 等指标)
            string chartTypeName = displayType == MacroChartDisplayType.LineChart ? "收盘折线" : "K 线";
            plot.Title($"{coin} {periodTitle} 大周期{chartTypeName}走势图 (已定型: {completedCount} 根)", size: 12);
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#f1f5f9");
            plot.Axes.Left.Label.Text = "价格 (USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.FontSize = 9.5f;
        }
    }
}
