using System;
using System.IO;
using System.Text.Json;

namespace Test.PeriodTickPlayback.WinForms.Models
{
    /// <summary>
    /// 回放用户配置参数持久化模型
    /// </summary>
    public class PeriodPlaybackSettings
    {
        public string Coin { get; set; } = "BTCUSDT";
        public DateTime StartDate { get; set; } = new DateTime(2024, 1, 1);
        public DateTime EndDate { get; set; } = new DateTime(2024, 1, 1);
        public int MacroPeriodMinutes { get; set; } = 30; // 默认 30 分钟
        public int PeriodIndex { get; set; } = 0; // 对应 cboPeriod.SelectedIndex
        public int CustomMinutes { get; set; } = 30; // 对应 numCustomMinutes.Value
        public int CustomSeconds { get; set; } = 5; // 对应 numCustomSeconds.Value
        public int SpeedIndex { get; set; } = 2; // 默认 10x
        public bool AutoFollow { get; set; } = false; // 默认不勾选自动跟随
        public bool ShowVolume { get; set; } = true;
        public int ChartTypeIndex { get; set; } = 0; // 0: 蜡烛图, 1: 折线图
        public bool AutoAppendNextBatch { get; set; } = true; // 自动连续追加后续分批
        public int SplitMainDistance { get; set; } = 480;
        public int SplitBottomDistance { get; set; } = 950;

        // 连续涨跌波段标记配置
        public bool ShowConsecutiveTrend { get; set; } = true; // 连续涨跌标记开关 (默认开启)
        public int ConsecutiveMinBars { get; set; } = 2; // 最少连续 K 线根数 (默认 2 根)
        public decimal ConsecutiveMinPct { get; set; } = 0.1m; // 最低累计涨跌幅度百分比 (默认 0.1%)

        // 微观 Tick 图表价格与多空比值曲线凑近对齐配置
        public bool RatioProximity { get; set; } = true; // 价格与多空比值折线智能对齐凑近 (默认开启)
        public bool ShowTickRatio { get; set; } = true; // 微观 Tick 窗口显示多空比值曲线与指标 (默认开启)

        // 主图平行通道延长配置
        public int ChannelExtensionBars { get; set; } = 15; // 主图平行通道前向延长根数 (默认 15 根)

        // 微观 Tick 窗口独立时间周期与自定义时间周期配置
        public int TickPeriodIndex { get; set; } = 0; // 0: 全部, 1: 1s, 2: 5s, 3: 15s, 4: 30s, 5: 1m, 6: 3m, 7: 5m, 8: 15m, 9: 30m, 10: 1h, 11: 自定秒, 12: 自定分
        public int TickCustomSeconds { get; set; } = 5; // 自定义秒数 (默认 5 秒)
        public int TickCustomMinutes { get; set; } = 5; // 自定义分钟数 (默认 5 分钟)
        public int TickChartTypeIndex { get; set; } = 0; // 微观图表类型: 0: 蜡烛图(K线), 1: 折线图
        public bool ShowTickConsecutiveTrend { get; set; } = true; // 微观 Tick 窗口大通道投影开关 (默认开启)
        public bool ShowTickChannel { get; set; } = true; // 微观 Tick 窗口自身通道开关 (默认开启)
        public int TickConsecutiveMinBars { get; set; } = 5; // 微观通道最少连续根数 (默认 5 根)
        public decimal TickConsecutiveMinPct { get; set; } = 0.8m; // 微观通道最低累计涨跌幅度百分比 (默认 0.8%)
        public bool ShowMacroAngleLines { get; set; } = true; // 大周期 K 线多角度趋势线开关 (默认开启)
        public bool ShowTickAngleLines { get; set; } = true; // 微观 Tick 窗口多角度趋势线开关 (默认开启)
        public string CustomAngles { get; set; } = "25, 45, 65"; // 自定义趋势线角度列表 (逗号/分号/空格隔开)
        public bool ShowAngleLines { get => ShowMacroAngleLines; set => ShowMacroAngleLines = value; } // 向后兼容别名
        public bool ShowChannelSignals { get; set; } = true; // 通道顶底信号与黄色十字标记开关 (默认开启)
        public int ChannelSignalAggregationSeconds { get; set; } = 6; // 微观聚合秒数 (默认 6 秒)
        public int ChannelSignalLookbackTicks { get; set; } = 10000; // 观察期向前提取回溯历史 Tick 数 (默认 10000 笔)
        public bool VerboseLog { get; set; } = false; // 详细定型日志开关 (默认关闭，避免高频刷屏)

        // 连续上涨/连续下跌形态反转做空/做多交易策略配置
        public bool ShowConsecutiveReversalSignals { get; set; } = true; // 连阳/连阴反转交易信号开关 (默认开启)
        public int ConsecutiveBullishBars { get; set; } = 4; // 连续大周期 K 线根数 (默认 4 根)
        public int ConsecutiveTrendBars { get => ConsecutiveBullishBars; set => ConsecutiveBullishBars = value; }
        public int ReversalSubPeriodMinutes { get; set; } = 3; // 反转确认与探顶/探底小周期分钟数 (默认 3 分钟)
        public decimal ReversalHighTolerancePct { get; set; } = 0.2m; // 探顶高点/探底低点容差百分比 (默认 0.2%)
        public decimal ReversalTolerancePct { get => ReversalHighTolerancePct; set => ReversalHighTolerancePct = value; }
        public decimal ReversalPullbackPct { get; set; } = 0.05m; // 冲高回落/探底回升幅度百分比 (默认 0.05%)
        public int MaxObservationPeriods { get; set; } = 5; // 无明显信号时最大允许顺延观察期轮数 (默认 5 轮)
        public decimal DojiBodyRatioMax { get; set; } = 0.25m; // 小十字星实体占全振幅比例上限 (默认 25%)
        public decimal DojiMaxChangePct { get; set; } = 0.15m; // 小十字星实体最大涨跌幅百分比 (默认 0.15%)
        public decimal ConsecutiveMaxDojiRatio { get; set; } = 0.5m; // 连续K线中十字星允许占的最大比例上限 (默认 50%，十字星不能占大部分)

        // 水平价格线配置
        public bool ShowMacroPriceLines { get; set; } = true; // K线窗口水平价格线开关 (默认开启)
        public bool ShowTickPriceLines { get; set; } = true; // 微观 Tick 窗口水平价格线开关 (默认开启)
        public string CustomPriceLines { get; set; } = "step: 100"; // 水平价格线配置 (支持具体价格如 60000, 62000 或步长如 step: 100 / 间隔 100)

        // 图表背景颜色配置 (支持独立自定义)
        public string MacroBackgroundColor { get; set; } = "#0b0f19"; // 大周期 K 线图表背景颜色 (默认极深黑蓝)
        public string TickBackgroundColor { get; set; } = "#0b0f19";  // 微观 Tick 图表背景颜色 (默认极深黑蓝)

        /// <summary>
        /// 解析十六进制颜色字符串为 System.Drawing.Color，若失败则返回回退默认颜色
        /// </summary>
        public static System.Drawing.Color ParseColor(string? hex, System.Drawing.Color defaultColor)
        {
            if (string.IsNullOrWhiteSpace(hex)) return defaultColor;
            try
            {
                string clean = hex.Trim();
                if (!clean.StartsWith("#")) clean = "#" + clean;
                return System.Drawing.ColorTranslator.FromHtml(clean);
            }
            catch
            {
                return defaultColor;
            }
        }

        /// <summary>
        /// 解析用户自定义价格水平线输入。
        /// 支持具体价格列表（逗号/分号/空格隔开，如 "60000, 61000, 62000"）
        /// 或步长模式（如 "step: 100", "step 100", "间隔 100", 或单一数值 "100" 且小于 (maxPrice - minPrice)）
        /// </summary>
        public static System.Collections.Generic.List<decimal> ParsePriceLevels(string? input, decimal minPrice, decimal maxPrice)
        {
            var result = new System.Collections.Generic.List<decimal>();
            if (string.IsNullOrWhiteSpace(input) || minPrice > maxPrice || maxPrice <= 0) return result;

            string text = input.Trim();

            // 1. 正则匹配步长模式: step[:= ]?([0-9.]+), 间隔[:= ]?([0-9.]+), 步长[:= ]?([0-9.]+)
            decimal step = 0;
            var stepMatch = System.Text.RegularExpressions.Regex.Match(
                text,
                @"(?:step|步长|间隔)\s*[:=]?\s*([0-9]+(?:\.[0-9]+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (stepMatch.Success && decimal.TryParse(stepMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal matchedStep) && matchedStep > 0)
            {
                step = matchedStep;
            }
            else
            {
                // 尝试解析逗号/分号/空格分割的数值列表
                var parts = text.Split(new[] { ',', ';', ' ', '，', '；', '、', '|' }, StringSplitOptions.RemoveEmptyEntries);
                var numbers = new System.Collections.Generic.List<decimal>();
                foreach (var p in parts)
                {
                    if (decimal.TryParse(p.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal val) ||
                        decimal.TryParse(p.Trim(), out val))
                    {
                        if (val > 0) numbers.Add(val);
                    }
                }

                // 如果只输入了一个单一数字
                if (numbers.Count == 1)
                {
                    decimal singleVal = numbers[0];
                    // 判断是否为步长 (例如数值较小且小于极值的一半，或者在价格跨度范围内)
                    if (singleVal < minPrice * 0.5m || (singleVal <= (maxPrice - minPrice) && singleVal < minPrice))
                    {
                        step = singleVal;
                    }
                    else
                    {
                        // 单一目标具体价格
                        if (singleVal >= minPrice * 0.8m && singleVal <= maxPrice * 1.2m)
                        {
                            result.Add(Math.Round(singleVal, 4));
                        }
                        return result;
                    }
                }
                else if (numbers.Count > 1)
                {
                    // 多个具体价格，直接返回在可视范围或合理范围内的价格
                    foreach (var num in numbers)
                    {
                        result.Add(Math.Round(num, 4));
                    }
                    result.Sort();
                    return System.Linq.Enumerable.ToList(System.Linq.Enumerable.Distinct(result));
                }
            }

            // 若识别为步长模式
            if (step > 0)
            {
                decimal range = maxPrice - minPrice;
                if (range <= 0) return result;

                // 防止步长过小导致生成过多线条卡死 UI，最多限制生成 100 条
                if (range / step > 100)
                {
                    step = range / 50; // 自动调优为合理密度
                }

                decimal start = Math.Floor(minPrice / step) * step;
                decimal end = Math.Ceiling(maxPrice / step) * step;

                for (decimal p = start; p <= end; p += step)
                {
                    if (p >= minPrice && p <= maxPrice)
                    {
                        result.Add(Math.Round(p, 4));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 解析用户自定义角度字符串为升序去重的正浮点度数列表 (0 < deg < 90)
        /// </summary>
        public static double[] ParseAngles(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return new double[] { 25.0, 45.0, 65.0 };
            var parts = input.Split(new[] { ',', ';', ' ', '，', '；', '、', '|' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<double>();
            foreach (var p in parts)
            {
                string clean = p.Replace("°", "").Trim();
                if (double.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double deg) ||
                    double.TryParse(clean, out deg))
                {
                    if (deg > 0.05 && deg < 89.95)
                    {
                        list.Add(Math.Round(deg, 2));
                    }
                }
            }
            if (list.Count == 0) return new double[] { 25.0, 45.0, 65.0 };
            return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.Distinct(list), a => a));
        }

        /// <summary>
        /// 获取 AppData 主配置文件路径 (永久存储，防止 dotnet clean / rebuild 清空 bin 目录导致配置丢失)
        /// </summary>
        public static string GetPrimaryConfigPath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "PeriodTickPlayback");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return Path.Combine(dir, "period_tick_playback_config.json");
            }
            catch
            {
                return GetFallbackConfigPath();
            }
        }

        /// <summary>
        /// 获取程序运行目录备用配置文件路径
        /// </summary>
        public static string GetFallbackConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "period_tick_playback_config.json");
        }

        public static PeriodPlaybackSettings Load(string? customPath = null)
        {
            if (!string.IsNullOrEmpty(customPath))
            {
                try
                {
                    if (File.Exists(customPath))
                    {
                        string json = File.ReadAllText(customPath);
                        var settings = JsonSerializer.Deserialize<PeriodPlaybackSettings>(json);
                        if (settings != null) return settings;
                    }
                }
                catch { }
                return new PeriodPlaybackSettings();
            }

            string primaryPath = GetPrimaryConfigPath();
            string fallbackPath = GetFallbackConfigPath();

            // 🌟 智能加载：若两个配置文件均存在，优先加载修改时间最新 (Most Recently Modified) 的有效配置
            string firstPath = primaryPath;
            string secondPath = fallbackPath;

            bool primaryExists = File.Exists(primaryPath);
            bool fallbackExists = File.Exists(fallbackPath);

            if (primaryExists && fallbackExists)
            {
                try
                {
                    DateTime primaryTime = File.GetLastWriteTimeUtc(primaryPath);
                    DateTime fallbackTime = File.GetLastWriteTimeUtc(fallbackPath);
                    if (fallbackTime > primaryTime)
                    {
                        firstPath = fallbackPath;
                        secondPath = primaryPath;
                    }
                }
                catch { }
            }
            else if (!primaryExists && fallbackExists)
            {
                firstPath = fallbackPath;
                secondPath = primaryPath;
            }

            // 1. 尝试首选路径
            try
            {
                if (File.Exists(firstPath))
                {
                    string json = File.ReadAllText(firstPath);
                    var settings = JsonSerializer.Deserialize<PeriodPlaybackSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch { }

            // 2. 备用尝试次选路径
            try
            {
                if (File.Exists(secondPath))
                {
                    string json = File.ReadAllText(secondPath);
                    var settings = JsonSerializer.Deserialize<PeriodPlaybackSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch { }

            return new PeriodPlaybackSettings();
        }

        public void Save(string? customPath = null)
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            if (!string.IsNullOrEmpty(customPath))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(customPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(customPath, json);
                }
                catch { }
                return;
            }

            // 1. 写入 AppData 永久目录
            try
            {
                string primaryPath = GetPrimaryConfigPath();
                string? dir = Path.GetDirectoryName(primaryPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(primaryPath, json);
            }
            catch
            {
            }

            // 2. 同时备份写入程序运行目录
            try
            {
                string fallbackPath = GetFallbackConfigPath();
                File.WriteAllText(fallbackPath, json);
            }
            catch
            {
            }
        }
    }
}
