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
        public int ConsecutiveMinBars { get; set; } = 5; // 最少连续 K 线根数 (默认 5 根)
        public decimal ConsecutiveMinPct { get; set; } = 2.5m; // 最低累计涨跌幅度百分比 (默认 2.5%)

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
        public bool ShowMacroRecentTrendLines { get; set; } = true; // 大周期 K 线图动态 300 根趋势线开关 (默认开启)
        public bool ShowTickRecentTrendLines { get; set; } = true;  // 微观 Tick 视窗动态 300 根趋势线开关 (默认开启)
        public string CustomAngles { get; set; } = "25, 45, 65"; // 自定义趋势线角度列表 (逗号/分号/空格隔开)
        public bool ShowAngleLines { get => ShowMacroAngleLines; set => ShowMacroAngleLines = value; } // 向后兼容别名
        public bool VerboseLog { get; set; } = false; // 详细定型日志开关 (默认关闭，避免高频刷屏)

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
