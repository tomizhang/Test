using Common;
using ScottPlot;
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
            bool showRatio = true)
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
                    showRatio);
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

            // 计算价格坐标轴的上下边界及归一化视觉位置 (供比值曲线智能“凑近对齐”)
            double priceSpan = (double)(maxVal - minVal);
            double padY = priceSpan * 0.15;
            if (padY <= 0) padY = (double)maxVal * 0.01;
            double pLeftYMin = (double)minVal - padY;
            double pLeftYMax = (double)maxVal + padY;
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
                double xMax = Math.Max(totalAvailable, renderCount + 5);
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
            bool showRatio = true)
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

            // 绘制微观子周期连续上涨 / 连续下跌平行通道 (基于最低要求K线数 - 1 拟合)
            if (showConsecutiveTrend && M >= consecutiveMinBars)
            {
                var barSnapshots = new List<MacroConsecutiveTrendDetector.BarSnapshot>(M);
                for (int i = 0; i < M; i++)
                {
                    var b = subBuckets[i];
                    decimal o = b.Ticks[0].Price;
                    decimal c = b.Ticks[^1].Price;
                    decimal h = decimal.MinValue;
                    decimal l = decimal.MaxValue;
                    for (int k = 0; k < b.Ticks.Count; k++)
                    {
                        var t = b.Ticks[k];
                        if (t.Price > h) h = t.Price;
                        if (t.Price < l) l = t.Price;
                    }
                    barSnapshots.Add(new MacroConsecutiveTrendDetector.BarSnapshot(i, o, h, l, c));
                }

                var tickTrends = MacroConsecutiveTrendDetector.ScanTrends(barSnapshots, consecutiveMinBars, consecutiveMinPct);
                for (int tIdx = 0; tIdx < tickTrends.Count; tIdx++)
                {
                    var tr = tickTrends[tIdx];
                    int sIdx = tr.StartIndex;
                    int eIdx = tr.EndIndex;
                    if (sIdx < 0 || eIdx >= M || sIdx > eIdx) continue;

                    bool isBull = tr.IsBullish;
                    var themeColor = isBull ? Color.FromHex("#10b981") : Color.FromHex("#ef4444");

                    // ① 确立点三角形标记 (▲ / ▼)
                    int cIdx = tr.ConfirmedBarIndex;
                    if (cIdx >= sIdx && cIdx <= eIdx)
                    {
                        double confPrice = (double)barSnapshots[cIdx].Close;
                        var confMarker = plot.Add.Marker(cIdx, confPrice);
                        confMarker.Shape = isBull ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                        confMarker.Size = 8;
                        confMarker.Color = themeColor;
                    }

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

                        // ⑤ 延伸虚线 (上轨、下轨、中轨与微光填充)
                        bool isLatest = (tIdx == tickTrends.Count - 1);
                        int extEnd = isLatest ? Math.Max(M - 1, eIdx) + 6 : eIdx + 6;
                        if (extEnd > eIdx)
                        {
                            double yUpExt = (double)(slopeK * extEnd + upperB);
                            double yLowExt = (double)(slopeK * extEnd + lowerB);
                            double yMidExt = (double)(slopeK * extEnd + (upperB + lowerB) / 2m);

                            var extUp = plot.Add.Line(eIdx, yUpEnd, extEnd, yUpExt);
                            extUp.Color = themeColor.WithAlpha(140);
                            extUp.LineWidth = 1.0f;
                            extUp.LinePattern = LinePattern.Dashed;

                            var extLow = plot.Add.Line(eIdx, yLowEnd, extEnd, yLowExt);
                            extLow.Color = themeColor.WithAlpha(140);
                            extLow.LineWidth = 1.0f;
                            extLow.LinePattern = LinePattern.Dashed;

                            var extMid = plot.Add.Line(eIdx, yMidEnd, extEnd, yMidExt);
                            extMid.Color = themeColor.WithAlpha(100);
                            extMid.LineWidth = 0.8f;
                            extMid.LinePattern = LinePattern.Dotted;

                            var extCoords = new Coordinates[]
                            {
                                new Coordinates(eIdx, yUpEnd),
                                new Coordinates(extEnd, yUpExt),
                                new Coordinates(extEnd, yLowExt),
                                new Coordinates(eIdx, yLowEnd)
                            };
                            var extPoly = plot.Add.Polygon(extCoords);
                            extPoly.FillColor = themeColor.WithAlpha(14);
                            extPoly.LineWidth = 0;
                        }

                        // ⑥ 悬浮信息气泡标签
                        string baseTag = $" [平行通道(基准{tr.ChannelBaseBars}根)]";
                        string tag = isBull
                            ? $"▲ 连涨 {tr.BarCount}根 (+{tr.PriceChangePct:F2}%){baseTag}"
                            : $"▼ 连跌 {tr.BarCount}根 ({tr.PriceChangePct:F2}%){baseTag}";

                        double tagX = (sIdx + eIdx) / 2.0;
                        double tagY = isBull ? Math.Max(yUpStart, yUpEnd) : Math.Min(yLowStart, yLowEnd);

                        var txtTag = plot.Add.Text(tag, tagX, tagY);
                        txtTag.LabelFontName = chineseFont;
                        txtTag.LabelFontSize = 8.0f;
                        txtTag.LabelBold = true;
                        txtTag.LabelFontColor = isBull ? Color.FromHex("#34d399") : Color.FromHex("#fca5a5");
                        txtTag.LabelAlignment = isBull ? Alignment.LowerCenter : Alignment.UpperCenter;
                        txtTag.LabelBackgroundColor = Color.FromHex("#0f172a").WithAlpha(0.88);
                        txtTag.LabelBorderColor = themeColor;
                        txtTag.LabelBorderWidth = 1f;
                    }
                }
            }

            // X 轴时间刻度标签与坐标范围
            plot.Axes.SetLimitsY((double)yMinPrice, (double)yMaxPrice);
            plot.Axes.SetLimitsX(-0.8, M + (showConsecutiveTrend ? 4.5 : -0.2));

            var xPos = new List<double>();
            var xLabels = new List<string>();
            int step = Math.Max(1, M / 8);
            for (int i = 0; i < M; i += step)
            {
                xPos.Add(i);
                xLabels.Add(subBuckets[i].StartTime.ToString("HH:mm"));
            }
            if (xPos.Count > 0 && xPos[^1] != M - 1)
            {
                xPos.Add(M - 1);
                xLabels.Add(subBuckets[^1].StartTime.ToString("HH:mm"));
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
        }
    }
}
