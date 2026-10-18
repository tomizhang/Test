using ScottPlot;
using System;
using Color = ScottPlot.Color;

namespace Test.PeriodTickPlayback.WinForms.Helper
{
    /// <summary>
    /// 固定价格分割水平线与走势速度计算助手
    /// 核心功能：
    /// 1. 在指定价格范围内按固定价格步长/间距 (Fixed Price Step) 自动分割并生成全图水平参考线
    /// 2. 结合当前选中的时间周期 (Period Span ΔT) 与当前价格间距 (Price Step ΔP)，精准计算基准速度 v = ΔP / ΔT，并在 X/Y 轴呈现
    /// 适用视窗：宏观大周期 K 线/折线窗口、微观子周期 K 线/逐笔 Tick 窗口
    /// </summary>
    public static class FixedPriceLineHelper
    {
        /// <summary>
        /// 走势速度计算结果
        /// </summary>
        public readonly struct SpeedResult
        {
            public decimal PriceStep { get; init; }
            public TimeSpan PeriodSpan { get; init; }
            public decimal SpeedPerSec { get; init; }
            public decimal SpeedPerMin { get; init; }
            public decimal SpeedPerHour { get; init; }
            public string FormattedShort { get; init; }
            public string FormattedDetailed { get; init; }
            public bool IsValid { get; init; }

            public SpeedResult(decimal priceStep, TimeSpan periodSpan, decimal perSec, decimal perMin, decimal perHour, string shortText, string detailedText, bool isValid)
            {
                PriceStep = priceStep;
                PeriodSpan = periodSpan;
                SpeedPerSec = perSec;
                SpeedPerMin = perMin;
                SpeedPerHour = perHour;
                FormattedShort = shortText;
                FormattedDetailed = detailedText;
                IsValid = isValid;
            }
        }

        /// <summary>
        /// 根据当前价格间距与选中时间周期计算走势速度 (v = ΔP / ΔT)
        /// </summary>
        /// <param name="priceStep">当前价格间距/步长 (USDT)</param>
        /// <param name="periodSpan">当前选中时间周期跨度</param>
        /// <param name="periodTitle">时间周期名称 (如 "5分钟", "30秒")</param>
        public static SpeedResult CalculateSpeed(decimal priceStep, TimeSpan periodSpan, string? periodTitle = null)
        {
            if (priceStep <= 0 || periodSpan <= TimeSpan.Zero)
            {
                return new SpeedResult(priceStep, periodSpan, 0, 0, 0, "速度: --", "速度: --", false);
            }

            decimal totalSeconds = (decimal)periodSpan.TotalSeconds;
            if (totalSeconds <= 0)
            {
                return new SpeedResult(priceStep, periodSpan, 0, 0, 0, "速度: --", "速度: --", false);
            }

            decimal perSec = priceStep / totalSeconds;
            decimal perMin = perSec * 60m;
            decimal perHour = perSec * 3600m;

            string title = !string.IsNullOrWhiteSpace(periodTitle)
                ? periodTitle
                : FormatPeriodSpanFriendly(periodSpan);

            string shortText;
            string detailedText;

            if (totalSeconds < 60m)
            {
                // 秒级短周期：优先突出 USDT/秒，附带 USDT/分
                shortText = $"{FormatNumber(perSec)} USDT/秒";
                detailedText = $"{FormatNumber(perSec)} USDT/秒 ({FormatNumber(perMin)} USDT/分) [间距: {FormatNumber(priceStep)} / 周期: {title}]";
            }
            else if (totalSeconds <= 3600m)
            {
                // 分钟级周期：优先突出 USDT/分，附带 USDT/秒
                shortText = $"{FormatNumber(perMin)} USDT/分";
                detailedText = $"{FormatNumber(perMin)} USDT/分 ({FormatNumber(perSec)} USDT/秒) [间距: {FormatNumber(priceStep)} / 周期: {title}]";
            }
            else
            {
                // 小时及以上周期：优先突出 USDT/时，附带 USDT/分
                shortText = $"{FormatNumber(perHour)} USDT/时";
                detailedText = $"{FormatNumber(perHour)} USDT/时 ({FormatNumber(perMin)} USDT/分) [间距: {FormatNumber(priceStep)} / 周期: {title}]";
            }

            return new SpeedResult(priceStep, periodSpan, perSec, perMin, perHour, shortText, detailedText, true);
        }

        public static string FormatNumber(decimal val)
        {
            if (val >= 1000m) return val.ToString("N2");
            if (val >= 1m) return val.ToString("F2");
            if (val >= 0.01m) return val.ToString("F4");
            return val.ToString("0.######");
        }

        public static string FormatPeriodSpanFriendly(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return $"{span.TotalSeconds:0.##}秒";
            if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0.##}分钟";
            if (span.TotalHours < 24) return $"{span.TotalHours:0.##}小时";
            return $"{span.TotalDays:0.##}天";
        }

        /// <summary>
        /// 在图表上绘制按固定价格分割生成的水平线
        /// </summary>
        /// <param name="plot">ScottPlot 图表对象</param>
        /// <param name="minPrice">当前数据或视口最低价</param>
        /// <param name="maxPrice">当前数据或视口最高价</param>
        /// <param name="step">固定价格分割步长 (USDT)</param>
        /// <param name="viewportLimits">可选当前视口坐标限制 (用于非跟随平移/缩放下的完整覆盖)</param>
        public static void DrawFixedPriceLines(
            Plot plot,
            decimal minPrice,
            decimal maxPrice,
            decimal step,
            AxisLimits? viewportLimits = null)
        {
            if (plot == null) return;
            if (step <= 0) return;

            decimal lowBound = minPrice;
            decimal highBound = maxPrice;

            if (viewportLimits.HasValue)
            {
                var l = viewportLimits.Value;
                if (l.Bottom < l.Top && l.Bottom > 0 && !double.IsNaN(l.Bottom) && !double.IsInfinity(l.Bottom))
                {
                    decimal vBot = (decimal)l.Bottom;
                    decimal vTop = (decimal)l.Top;
                    if (lowBound <= 0 || lowBound == decimal.MaxValue) lowBound = vBot;
                    else lowBound = Math.Min(lowBound, vBot);

                    if (highBound <= 0 || highBound == decimal.MinValue) highBound = vTop;
                    else highBound = Math.Max(highBound, vTop);
                }
            }

            if (lowBound <= 0 && highBound <= 0) return;
            if (lowBound == decimal.MaxValue || highBound == decimal.MinValue) return;
            if (lowBound > highBound) (lowBound, highBound) = (highBound, lowBound);

            // 预留适度外延缓冲区 (15% 价格范围或至少 3 个步长)，确保图表微调平移时边缘不留空白
            decimal span = highBound - lowBound;
            if (span <= 0) span = highBound * 0.05m;
            decimal margin = Math.Max(span * 0.15m, step * 3m);

            decimal pStart = Math.Floor(Math.Max(0m, lowBound - margin) / step) * step;
            decimal pEnd = Math.Ceiling((highBound + margin) / step) * step;

            if (pEnd < pStart) return;

            // 密度安全熔断保护：若步长过小导致总线数超过 300 根，自动按整数倍稀疏化，防止主线程卡顿
            decimal totalSpan = pEnd - pStart;
            if (totalSpan / step > 300m)
            {
                decimal factor = Math.Ceiling((totalSpan / 150m) / step);
                if (factor > 1m)
                {
                    step *= factor;
                    pStart = Math.Floor(Math.Max(0m, lowBound - margin) / step) * step;
                    pEnd = Math.Ceiling((highBound + margin) / step) * step;
                }
            }

            for (decimal p = pStart; p <= pEnd; p += step)
            {
                if (p <= 0) continue;

                var hl = plot.Add.HorizontalLine((double)p);
                hl.Axes.YAxis = plot.Axes.Left;
                hl.EnableAutoscale = false;

                // 核心视觉层次：
                // 逢 5 倍步长呈现更明亮通透的科技天蓝虚线，普通步长呈现轻柔半透明虚线
                bool isMajor = (step > 0 && Math.Abs((p / (step * 5m)) - Math.Round(p / (step * 5m))) < 0.0001m);

                if (isMajor)
                {
                    hl.Color = Color.FromHex("#38bdf8").WithAlpha(125); // 明亮天蓝 (5x 关键整数位)
                    hl.LineWidth = 1.0f;
                    hl.LinePattern = LinePattern.Dashed;
                }
                else
                {
                    hl.Color = Color.FromHex("#38bdf8").WithAlpha(65);  // 轻柔极客天蓝 (标准等距位)
                    hl.LineWidth = 0.75f;
                    hl.LinePattern = LinePattern.Dashed;
                }
            }
        }

        /// <summary>
        /// 在图表上绘制 K 线涨跌速度折线图 (类似折线图，依附于独立的右侧 Y 轴)
        /// 具备：
        /// 1. 独立右侧速度坐标轴 (USDT/分 或 USDT/秒)
        /// 2. 0 速度基准中轴虚线
        /// 3. 结合当前价格间距与时间周期计算得到的 ±1.0 步长基准速度参考线
        /// 4. 科技紫折线 + 节点高亮圆点，末尾实时变动点高亮呈现
        /// </summary>
        public static void DrawSpeedLinePlot(
            Plot plot,
            IReadOnlyList<(double X, decimal Open, decimal Close)> bars,
            TimeSpan periodSpan,
            decimal fixedPriceStep,
            string periodTitle,
            string chineseFont)
        {
            if (plot == null || bars == null || bars.Count == 0) return;
            if (periodSpan <= TimeSpan.Zero) periodSpan = TimeSpan.FromMinutes(30);

            decimal totalSeconds = (decimal)periodSpan.TotalSeconds;
            if (totalSeconds <= 0) return;

            string unitStr;
            decimal multiplier;
            if (totalSeconds < 60m)
            {
                unitStr = "USDT/秒";
                multiplier = 1.0m / totalSeconds;
            }
            else if (totalSeconds <= 3600m)
            {
                unitStr = "USDT/分";
                multiplier = 60.0m / totalSeconds;
            }
            else
            {
                unitStr = "USDT/时";
                multiplier = 3600.0m / totalSeconds;
            }

            int n = bars.Count;
            double[] xs = new double[n];
            double[] speeds = new double[n];
            double maxAbs = 0;

            for (int i = 0; i < n; i++)
            {
                xs[i] = bars[i].X;
                decimal dP = bars[i].Close - bars[i].Open;
                double spd = (double)(dP * multiplier);
                speeds[i] = spd;
                double abs = Math.Abs(spd);
                if (abs > maxAbs) maxAbs = abs;
            }

            // 使用内建单一右轴 plot.Axes.Right，绝不动态调用 AddRightAxis，杜绝坐标轴堆叠与图表挤压
            var speedAxis = plot.Axes.Right;
            if (speedAxis is ScottPlot.AxisPanels.RightAxis ra)
            {
                ra.IsVisible = true;
                ra.LabelText = fixedPriceStep > 0
                    ? $"K线涨跌速度 ({unitStr}, 步长:{FormatNumber(fixedPriceStep)})"
                    : $"K线涨跌速度 ({unitStr})";
                ra.LabelFontName = chineseFont;
                ra.LabelFontSize = 8.5f;
                ra.LabelFontColor = Color.FromHex("#c084fc"); // 科技紫
                ra.Color(Color.FromHex("#c084fc"));
                ra.TickLabelStyle.IsVisible = true;
                ra.TickLabelStyle.ForeColor = Color.FromHex("#c084fc");
            }

            double stepSpeed = (fixedPriceStep > 0) ? (double)(fixedPriceStep * multiplier) : 0;
            double limitY = Math.Max(stepSpeed * 1.35, maxAbs * 1.35);
            if (limitY <= 0) limitY = 1.0;
            plot.Axes.SetLimitsY(-limitY, limitY, speedAxis);

            // 1. 0 速度基准中轴虚线
            var zeroLine = plot.Add.HorizontalLine(0);
            zeroLine.Axes.YAxis = speedAxis;
            zeroLine.Color = Color.FromHex("#94a3b8").WithAlpha(160);
            zeroLine.LinePattern = LinePattern.Dashed;
            zeroLine.LineWidth = 1.0f;

            // 2. 结合当前价格间距计算得到的 ±1.0 步长基准速度参考线
            if (stepSpeed > 0)
            {
                var stepUp = plot.Add.HorizontalLine(stepSpeed);
                stepUp.Axes.YAxis = speedAxis;
                stepUp.Color = Color.FromHex("#22c55e").WithAlpha(90);
                stepUp.LinePattern = LinePattern.Dotted;
                stepUp.LineWidth = 0.8f;

                var stepDown = plot.Add.HorizontalLine(-stepSpeed);
                stepDown.Axes.YAxis = speedAxis;
                stepDown.Color = Color.FromHex("#ef4444").WithAlpha(90);
                stepDown.LinePattern = LinePattern.Dotted;
                stepDown.LineWidth = 0.8f;
            }

            // 3. 速度折线 (ScatterLine)
            var speedLine = plot.Add.ScatterLine(xs, speeds);
            speedLine.Axes.YAxis = speedAxis;
            speedLine.Color = Color.FromHex("#c084fc");
            speedLine.LineWidth = 1.8f;
            speedLine.MarkerShape = MarkerShape.FilledCircle;
            speedLine.MarkerSize = 4.0f;
            speedLine.MarkerFillColor = Color.FromHex("#e9d5ff");
            speedLine.MarkerLineColor = Color.FromHex("#a855f7");

            // 4. 最新形成点高亮脉冲圆点
            if (n > 0)
            {
                int lastIdx = n - 1;
                var lastMarker = plot.Add.Marker(xs[lastIdx], speeds[lastIdx]);
                lastMarker.Axes.YAxis = speedAxis;
                lastMarker.Shape = MarkerShape.FilledCircle;
                lastMarker.Size = 6.5f;
                lastMarker.Color = speeds[lastIdx] >= 0 ? Color.FromHex("#38bdf8") : Color.FromHex("#f59e0b");
            }
        }
    }
}
