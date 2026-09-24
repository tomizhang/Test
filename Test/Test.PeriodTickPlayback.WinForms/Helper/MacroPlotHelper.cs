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
        /// <summary>
        /// 最近一次在宏观图表上计算得到的 45° 基准斜率 (单位价格/每根K线跨度)，供微观 Tick 窗口与命中检测跨图表同频复用
        /// </summary>
        public static double LastSlope45 { get; set; } = 0;

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
            int channelExtensionBars = 15,
            bool showAngleLines = true,
            IReadOnlyList<double>? customAngles = null,
            double canvasWidth = 1200,
            double canvasHeight = 450)
        {
            if (plot == null) return;

            AxisLimits oldLimits = plot.Axes.GetLimits();
            plot.Clear();

            string chineseFont = GetInstalledChineseFont();
            Fonts.Default = chineseFont;

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

            // 预先计算当前视口 X/Y 轴范围与比例，供通道与多角度趋势线计算
            int windowBars = 60; // 默认可视 60 根大周期 K 线
            double rightMargin = showConsecutiveTrend ? Math.Max(8.0, channelExtensionBars * 0.7) : 2.5;
            double xMax = totalDisplayCount + rightMargin;
            double xMin = Math.Max(-0.5, totalDisplayCount - windowBars);
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

            // 计算当前屏幕视口几何斜率基准 (确保 45° 在屏幕上呈现真实几何 45 度角)
            double visibleSpanX = 60;
            double visibleSpanY = 1.0;
            if (autoFollow)
            {
                visibleSpanX = xMax - xMin;
                if (winMinPrice <= winMaxPrice && winMinPrice > 0)
                {
                    visibleSpanY = (double)(winMaxPrice - winMinPrice) * 1.24;
                }
            }
            else if (oldLimits.Right > oldLimits.Left && oldLimits.Top > oldLimits.Bottom)
            {
                visibleSpanX = oldLimits.Right - oldLimits.Left;
                visibleSpanY = oldLimits.Top - oldLimits.Bottom;
            }
            if (visibleSpanX <= 0) visibleSpanX = 60;
            if (visibleSpanY <= 0) visibleSpanY = (double)(winMaxPrice > 0 ? winMaxPrice * 0.05m : 1.0m);

            double pixelAspect = (canvasWidth > 0 && canvasHeight > 0) ? (canvasWidth / canvasHeight) : 2.5;
            if (pixelAspect <= 0.1 || pixelAspect > 10.0) pixelAspect = 2.5;

            double slope45 = (visibleSpanY / visibleSpanX) * pixelAspect;
            if (slope45 <= 0) slope45 = (double)(winMaxPrice > 0 ? winMaxPrice * 0.005m : 0.01m);
            LastSlope45 = slope45;

            // 4.5 绘制连续上涨 / 连续下跌波段标记与多角度趋势线 (满足门槛：连续 N 根及以上且累计幅度 >= X%)
            if ((showConsecutiveTrend || showAngleLines) && totalDisplayCount >= consecutiveMinBars)
            {
                var trends = MacroConsecutiveTrendDetector.ScanTrends(
                    completedBars,
                    formingBar,
                    consecutiveMinBars,
                    consecutiveMinPct);

                for (int tIdx = 0; tIdx < trends.Count; tIdx++)
                {
                    var tr = trends[tIdx];
                    tr.MacroSlope45 = slope45;
                    int sIdx = tr.StartIndex;
                    int eIdx = tr.EndIndex;
                    if (sIdx < 0 || eIdx >= totalDisplayCount || sIdx > eIdx) continue;

                    bool isBull = tr.IsBullish;
                    var themeColor = isBull
                        ? Color.FromHex("#10b981") // 连涨翡翠绿
                        : Color.FromHex("#ef4444"); // 连跌火红

                    bool isChannelSelected = selectedStartIndex.HasValue && selectedEndIndex.HasValue &&
                                            sIdx == selectedStartIndex.Value && eIdx == selectedEndIndex.Value;
                    float channelLineWidth = isChannelSelected ? 2.0f : 1.2f;
                    byte channelFillAlpha = isChannelSelected ? (byte)52 : (byte)28;

                    int forwardBars = Math.Max(5, channelExtensionBars);
                    bool isLatestTrend = (tIdx == trends.Count - 1);
                    int extEnd = isLatestTrend
                        ? Math.Max(totalDisplayCount - 1, eIdx) + forwardBars
                        : Math.Min(Math.Max(totalDisplayCount - 1, eIdx) + forwardBars, eIdx + Math.Max(20, forwardBars));

                    if (showConsecutiveTrend)
                    {
                        // ① 确立点三角形标记 (▲ / ▼)
                        int cIdx = tr.ConfirmedBarIndex;
                    if (cIdx >= sIdx && cIdx <= eIdx)
                    {
                        double confPrice = (double)(cIdx < completedCount
                            ? completedBars![cIdx].Close
                            : (formingBar != null ? formingBar.CurrentPrice : tr.EndPrice));

                        var confMarker = plot.Add.Marker(cIdx, confPrice);
                        confMarker.Shape = isBull ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                        confMarker.Size = isChannelSelected ? 12 : 9;
                        confMarker.Color = isChannelSelected ? Color.FromHex("#fbbf24") : themeColor;
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
                        channelPoly.FillColor = themeColor.WithAlpha(channelFillAlpha);
                        channelPoly.LineWidth = 0;

                        // ③ 平行通道上轨与下轨实线 (选中时线宽 2.0f，默认 1.2f)
                        var lineUp = plot.Add.Line(sIdx, yUpStart, eIdx, yUpEnd);
                        lineUp.Color = isChannelSelected ? Color.FromHex("#fbbf24") : themeColor;
                        lineUp.LineWidth = channelLineWidth;
                        lineUp.LinePattern = LinePattern.Solid;

                        var lineLow = plot.Add.Line(sIdx, yLowStart, eIdx, yLowEnd);
                        lineLow.Color = isChannelSelected ? Color.FromHex("#fbbf24") : themeColor;
                        lineLow.LineWidth = channelLineWidth;
                        lineLow.LinePattern = LinePattern.Solid;

                        // ④ 平行通道中轨虚线
                        var lineMid = plot.Add.Line(sIdx, yMidStart, eIdx, yMidEnd);
                        lineMid.Color = themeColor.WithAlpha(isChannelSelected ? (byte)220 : (byte)170);
                        lineMid.LineWidth = isChannelSelected ? 1.2f : 0.8f;
                        lineMid.LinePattern = LinePattern.Dashed;

                        // ⑤ 平行通道向右充分延长 (突破截止限制，向前延伸至最新柱后充足未来空间)
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
                    string selectedPrefix = isChannelSelected ? "⭐ [已选中] " : "";
                    string tag = isBull
                        ? $"{selectedPrefix}▲ 连涨 {tr.BarCount}根 (+{tr.PriceChangePct:F2}%){baseTag}"
                        : $"{selectedPrefix}▼ 连跌 {tr.BarCount}根 ({tr.PriceChangePct:F2}%){baseTag}";

                    double tagX = (sIdx + eIdx) / 2.0;

                    var txtTag = plot.Add.Text(tag, tagX, tagY);
                    txtTag.LabelFontName = chineseFont;
                    txtTag.LabelFontSize = isChannelSelected ? 9.5f : 9.0f;
                    txtTag.LabelBold = true;
                    txtTag.LabelFontColor = isChannelSelected ? Color.FromHex("#fbbf24") : (isBull ? Color.FromHex("#34d399") : Color.FromHex("#fca5a5"));
                    txtTag.LabelAlignment = isBull ? Alignment.LowerCenter : Alignment.UpperCenter;
                    txtTag.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.92);
                    txtTag.LabelBorderColor = isChannelSelected ? Color.FromHex("#fbbf24") : themeColor;
                    txtTag.LabelBorderWidth = isChannelSelected ? 2f : 1f;
                    } // end of if (showConsecutiveTrend)

                    // ⑧ 基于第一根 K 线高低双点位的多角度趋势线 (延长趋势线，可独立显示)
                    if (showAngleLines && slope45 > 0)
                    {
                        decimal firstHigh = tr.FirstBarHigh > 0 ? tr.FirstBarHigh : (sIdx < completedCount && completedBars != null ? completedBars[sIdx].High : (formingBar?.High ?? tr.StartPrice));
                        decimal firstLow = tr.FirstBarLow > 0 ? tr.FirstBarLow : (sIdx < completedCount && completedBars != null ? completedBars[sIdx].Low : (formingBar?.Low ?? tr.StartPrice));
                        double yHigh = (double)firstHigh;
                        double yLow = (double)firstLow;
                        double xStart = sIdx;
                        double xEnd = extEnd;
                        double dx = xEnd - xStart;

                        if (dx > 0)
                        {
                            var angles = (customAngles != null && customAngles.Count > 0) ? customAngles : new double[] { 25.0, 45.0, 65.0 };
                            var angleConfigs = new List<(double deg, double slope, LinePattern pattern, float width)>(angles.Count);
                            foreach (var deg in angles)
                            {
                                double rad = deg * Math.PI / 180.0;
                                double slope = Math.Tan(rad) * slope45;
                                bool is45 = Math.Abs(deg - 45.0) < 0.01;
                                LinePattern pat = is45 ? LinePattern.Solid : (deg < 45.0 ? LinePattern.Dashed : LinePattern.Dotted);
                                float w = is45 ? (isChannelSelected ? 1.8f : 1.3f) : (isChannelSelected ? 1.4f : 1.0f);
                                angleConfigs.Add((deg, slope, pat, w));
                            }

                            // 高低双点位 (连续下跌：第一根为高点绘制，同理以第一根低点绘制；连续上涨：第一根为低点绘制，同理以第一根高点绘制)
                            var anchorPoints = isBull
                                ? new (string label, double price, bool isPrimary)[] { ("L", yLow, true), ("H", yHigh, false) }
                                : new (string label, double price, bool isPrimary)[] { ("H", yHigh, true), ("L", yLow, false) };

                            foreach (var anchor in anchorPoints)
                            {
                                // 绘制第一根 K 线锚点圆点标记
                                var anchorMarker = plot.Add.Marker(xStart, anchor.price);
                                anchorMarker.Shape = MarkerShape.FilledCircle;
                                anchorMarker.Size = isChannelSelected ? 6 : 4;
                                anchorMarker.Color = themeColor;

                                foreach (var ac in angleConfigs)
                                {
                                    double targetY = isBull
                                        ? anchor.price + ac.slope * dx
                                        : anchor.price - ac.slope * dx;

                                    // 底部保护截断 (防止价格跌破零)
                                    double actualXEnd = xEnd;
                                    if (targetY <= 0 && anchor.price > 0)
                                    {
                                        actualXEnd = xStart + (anchor.price / ac.slope);
                                        targetY = 0;
                                    }

                                    if (actualXEnd <= xStart) continue;

                                    Color rayColor;
                                    bool is45 = Math.Abs(ac.deg - 45.0) < 0.01;
                                    if (isBull)
                                    {
                                        rayColor = is45
                                            ? Color.FromHex("#10b981") // 45° 翡翠绿基准
                                            : (ac.deg < 45.0 ? Color.FromHex("#34d399") : Color.FromHex("#a3e635"));
                                    }
                                    else
                                    {
                                        rayColor = is45
                                            ? Color.FromHex("#ef4444") // 45° 烈火红基准
                                            : (ac.deg < 45.0 ? Color.FromHex("#fb923c") : Color.FromHex("#f43f5e"));
                                    }

                                    if (isChannelSelected)
                                    {
                                        rayColor = is45 ? Color.FromHex("#fbbf24") : rayColor;
                                    }

                                    byte rayAlpha = isChannelSelected ? (byte)230 : (isLatestTrend ? (byte)180 : (byte)100);

                                    var angleRay = plot.Add.Line(xStart, anchor.price, actualXEnd, targetY);
                                    angleRay.Color = rayColor.WithAlpha(rayAlpha);
                                    angleRay.LineWidth = ac.width;
                                    angleRay.LinePattern = ac.pattern;

                                    // 射线末端角度标注 (为最新活跃波段或选中波段标注)
                                    if (isLatestTrend || isChannelSelected)
                                    {
                                        string signStr = isBull ? "+" : "-";
                                        string endLabel = $"{anchor.label} {signStr}{ac.deg:0.##}°";
                                        var txtAngle = plot.Add.Text(endLabel, actualXEnd, targetY);
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
                    }
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
                if (autoFollow)
                {
                    // 🌟 自动跟随模式：Y 轴范围基于当前窗口内实际可视的 K 线
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
            plot.Title($"{coin} {periodTitle} {chartTypeName}走势图 (已定型: {completedCount} 根)", size: 12);
            plot.Axes.Title.Label.FontName = chineseFont;
            plot.Axes.Title.Label.ForeColor = Color.FromHex("#f1f5f9");
            plot.Axes.Left.Label.Text = "价格 (USDT)";
            plot.Axes.Left.Label.FontName = chineseFont;
            plot.Axes.Left.Label.FontSize = 9.5f;
        }

        /// <summary>
        /// 平行通道像素级命中检测 (Hit-Test)
        /// 检测鼠标是否点击或悬停在上轨、下轨、中轨、前向延伸虚线或通道悬浮气泡标签附近 (容差 tolerancePx 像素)
        /// </summary>
        public static MacroConsecutiveTrendItem? FindHitChannel(
            Plot plot,
            Pixel mousePixel,
            Coordinates mouseCoord,
            IReadOnlyList<MacroConsecutiveTrendItem>? trends,
            int totalDisplayCount,
            int channelExtensionBars = 15,
            double tolerancePx = 14.0)
        {
            if (plot == null || trends == null || trends.Count == 0 || totalDisplayCount == 0)
                return null;

            MacroConsecutiveTrendItem? bestHit = null;
            double minDistance = double.MaxValue;
            double tagTolerancePx = tolerancePx * 1.3;

            foreach (var tr in trends)
            {
                int sIdx = tr.StartIndex;
                int eIdx = tr.EndIndex;
                if (sIdx < 0 || eIdx >= totalDisplayCount || sIdx > eIdx) continue;

                double currentMinDist = double.MaxValue;

                if (tr.HasChannel)
                {
                    decimal slopeK = tr.SlopeK;
                    decimal upperB = tr.UpperIntercept;
                    decimal lowerB = tr.LowerIntercept;
                    int extEnd = eIdx + channelExtensionBars;

                    double yUpStart = (double)(slopeK * sIdx + upperB);
                    double yUpEnd = (double)(slopeK * eIdx + upperB);
                    double yLowStart = (double)(slopeK * sIdx + lowerB);
                    double yLowEnd = (double)(slopeK * eIdx + lowerB);
                    double yMidStart = (double)(slopeK * sIdx + (upperB + lowerB) / 2m);
                    double yMidEnd = (double)(slopeK * eIdx + (upperB + lowerB) / 2m);

                    Pixel pUpStart = plot.GetPixel(new Coordinates(sIdx, yUpStart));
                    Pixel pUpEnd = plot.GetPixel(new Coordinates(eIdx, yUpEnd));
                    Pixel pLowStart = plot.GetPixel(new Coordinates(sIdx, yLowStart));
                    Pixel pLowEnd = plot.GetPixel(new Coordinates(eIdx, yLowEnd));
                    Pixel pMidStart = plot.GetPixel(new Coordinates(sIdx, yMidStart));
                    Pixel pMidEnd = plot.GetPixel(new Coordinates(eIdx, yMidEnd));

                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pUpStart, pUpEnd));
                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pLowStart, pLowEnd));
                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pMidStart, pMidEnd));

                    if (channelExtensionBars > 0)
                    {
                        double yUpExt = (double)(slopeK * extEnd + upperB);
                        double yLowExt = (double)(slopeK * extEnd + lowerB);
                        double yMidExt = (double)(slopeK * extEnd + (upperB + lowerB) / 2m);

                        Pixel pUpExt = plot.GetPixel(new Coordinates(extEnd, yUpExt));
                        Pixel pLowExt = plot.GetPixel(new Coordinates(extEnd, yLowExt));
                        Pixel pMidExt = plot.GetPixel(new Coordinates(extEnd, yMidExt));

                        currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pUpEnd, pUpExt));
                        currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pLowEnd, pLowExt));
                        currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pMidEnd, pMidExt));
                    }

                    // 检测气泡标签
                    double tagX = (sIdx + eIdx) / 2.0;
                    double tagY = tr.IsBullish ? Math.Max(yUpStart, yUpEnd) : Math.Min(yLowStart, yLowEnd);
                    Pixel pTag = plot.GetPixel(new Coordinates(tagX, tagY));
                    float tagDx = mousePixel.X - pTag.X;
                    float tagDy = mousePixel.Y - pTag.Y;
                    double tagDist = Math.Sqrt(tagDx * tagDx + tagDy * tagDy);
                    if (tagDist <= tagTolerancePx)
                    {
                        currentMinDist = Math.Min(currentMinDist, tagDist);
                    }
                }
                else
                {
                    double yTop = (double)tr.MaxHigh;
                    double yBot = (double)tr.MinLow;

                    Pixel pTopLeft = plot.GetPixel(new Coordinates(sIdx, yTop));
                    Pixel pTopRight = plot.GetPixel(new Coordinates(eIdx, yTop));
                    Pixel pBotLeft = plot.GetPixel(new Coordinates(sIdx, yBot));
                    Pixel pBotRight = plot.GetPixel(new Coordinates(eIdx, yBot));

                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pTopLeft, pTopRight));
                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pBotLeft, pBotRight));
                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pTopLeft, pBotLeft));
                    currentMinDist = Math.Min(currentMinDist, DistanceToSegment(mousePixel, pTopRight, pBotRight));
                }

                if (currentMinDist <= tolerancePx && currentMinDist < minDistance)
                {
                    minDistance = currentMinDist;
                    bestHit = tr;
                }
            }

            if (bestHit != null && bestHit.MacroSlope45 <= 0 && LastSlope45 > 0)
            {
                bestHit.MacroSlope45 = LastSlope45;
            }

            return bestHit;
        }

        public static double DistanceToSegment(Pixel p, Pixel a, Pixel b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-6f)
            {
                float px = p.X - a.X;
                float py = p.Y - a.Y;
                return Math.Sqrt(px * px + py * py);
            }
            float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0f, 1f);
            float projX = a.X + t * dx;
            float projY = a.Y + t * dy;
            float distX = p.X - projX;
            float distY = p.Y - projY;
            return Math.Sqrt(distX * distX + distY * distY);
        }
    }
}
