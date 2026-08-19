using Binance.Net.Enums;
using System;
using System.IO;

namespace Common
{
    public static class Config
    {
        // 统一数据根目录 (默认 D:\data\binance_market_data，具备自动创建与备用回退机制)
        public static string TickDataRoot = "D:\\data\\binance_market_data"; 

        public static string GetRootPath()
        {
            try
            {
                if (!Directory.Exists(TickDataRoot))
                {
                    Directory.CreateDirectory(TickDataRoot);
                }
                return TickDataRoot;
            }
            catch
            {
                // 安全回退：若系统无 D 盘或写入受限，自动使用应用程序根目录下的 data 文件夹
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(fallback))
                {
                    Directory.CreateDirectory(fallback);
                }
                return fallback;
            }
        }

        #region K线路径管理 (klines/{interval}/{yyyy}/{MM})

        /// <summary>
        /// 将 Binance KlineInterval 枚举转换为标准周期字符串 (如 30m, 1h, 1d)
        /// </summary>
        public static string ToIntervalString(this KlineInterval interval)
        {
            return interval switch
            {
                KlineInterval.OneSecond => "1s",
                KlineInterval.OneMinute => "1m",
                KlineInterval.ThreeMinutes => "3m",
                KlineInterval.FiveMinutes => "5m",
                KlineInterval.FifteenMinutes => "15m",
                KlineInterval.ThirtyMinutes => "30m",
                KlineInterval.OneHour => "1h",
                KlineInterval.TwoHour => "2h",
                KlineInterval.FourHour => "4h",
                KlineInterval.SixHour => "6h",
                KlineInterval.EightHour => "8h",
                KlineInterval.TwelveHour => "12h",
                KlineInterval.OneDay => "1d",
                KlineInterval.ThreeDay => "3d",
                KlineInterval.OneWeek => "1w",
                KlineInterval.OneMonth => "1M",
                _ => interval.ToString()
            };
        }

        /// <summary>
        /// 获取指定币种和周期的 K 线基础目录 (如: BTCUSDT/klines/30m)
        /// </summary>
        public static string GetDataPath(string coin, KlineInterval klineInterval)
        {
            return GetKlineDataPath(coin, klineInterval.ToIntervalString());
        }

        /// <summary>
        /// 获取指定币种和周期的 K 线基础目录 (如: BTCUSDT/klines/30m)
        /// </summary>
        public static string GetDataPath(string coin, string interval)
        {
            return GetKlineDataPath(coin, interval);
        }

        /// <summary>
        /// 获取指定币种和周期的 K 线基础目录 (如: BTCUSDT/klines/30m)
        /// </summary>
        public static string GetKlineDataPath(string coin, KlineInterval klineInterval)
        {
            return GetKlineDataPath(coin, klineInterval.ToIntervalString());
        }

        /// <summary>
        /// 获取指定币种和周期的 K 线基础目录 (如: BTCUSDT/klines/30m)
        /// </summary>
        public static string GetKlineDataPath(string coin, string interval)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpper(), "klines", interval);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 获取指定币种、周期及年月层级的 K 线目录 (如: BTCUSDT/klines/30m/2026/01)
        /// </summary>
        public static string GetKlineDataPath(string coin, KlineInterval klineInterval, DateTime date)
        {
            return GetKlineDataPath(coin, klineInterval.ToIntervalString(), date.Year, date.Month);
        }

        /// <summary>
        /// 获取指定币种、周期及年月层级的 K 线目录 (如: BTCUSDT/klines/30m/2026/01)
        /// </summary>
        public static string GetKlineDataPath(string coin, string interval, DateTime date)
        {
            return GetKlineDataPath(coin, interval, date.Year, date.Month);
        }

        /// <summary>
        /// 获取指定币种、周期及年月层级的 K 线目录 (如: BTCUSDT/klines/30m/2026/01)
        /// </summary>
        public static string GetKlineDataPath(string coin, KlineInterval klineInterval, int year, int month)
        {
            return GetKlineDataPath(coin, klineInterval.ToIntervalString(), year, month);
        }

        /// <summary>
        /// 获取指定币种、周期及年月层级的 K 线目录 (如: BTCUSDT/klines/30m/2026/01)
        /// </summary>
        public static string GetKlineDataPath(string coin, string interval, int year, int month)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpper(), "klines", interval, year.ToString(), month.ToString("D2"));
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 获取指定日期的 K 线文件名 (如: BTCUSDT-30m-2026-01-01.zip)
        /// </summary>
        public static string GetKlineFileName(string coin, KlineInterval klineInterval, DateTime date, string extension = ".zip")
        {
            return GetKlineFileName(coin, klineInterval.ToIntervalString(), date, extension);
        }

        /// <summary>
        /// 获取指定日期的 K 线文件名 (如: BTCUSDT-30m-2026-01-01.zip)
        /// </summary>
        public static string GetKlineFileName(string coin, string interval, DateTime date, string extension = ".zip")
        {
            string ext = extension.StartsWith(".") ? extension : "." + extension;
            return $"{coin.ToUpper()}-{interval}-{date:yyyy-MM-dd}{ext}";
        }

        /// <summary>
        /// 获取指定日期的 K 线文件完整路径 (如: BTCUSDT/klines/30m/2026/01/BTCUSDT-30m-2026-01-01.zip)
        /// </summary>
        public static string GetKlineFilePath(string coin, KlineInterval klineInterval, DateTime date, string extension = ".zip")
        {
            return GetKlineFilePath(coin, klineInterval.ToIntervalString(), date, extension);
        }

        /// <summary>
        /// 获取指定日期的 K 线文件完整路径 (如: BTCUSDT/klines/30m/2026/01/BTCUSDT-30m-2026-01-01.zip)
        /// </summary>
        public static string GetKlineFilePath(string coin, string interval, DateTime date, string extension = ".zip")
        {
            string dir = GetKlineDataPath(coin, interval, date.Year, date.Month);
            string fileName = GetKlineFileName(coin, interval, date, extension);
            return Path.Combine(dir, fileName);
        }

        #endregion

        #region Trade 路径管理 (trades/{yyyy}/{MM})

        /// <summary>
        /// 获取指定币种的 Trade 基础目录 (如: BTCUSDT/trades)
        /// </summary>
        public static string GetTradeDataPath(string coin)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpper(), "trades");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 获取指定币种及年月层级的 Trade 目录 (如: BTCUSDT/trades/2025/01)
        /// </summary>
        public static string GetTradeDataPath(string coin, DateTime date)
        {
            return GetTradeDataPath(coin, date.Year, date.Month);
        }

        /// <summary>
        /// 获取指定币种及年月层级的 Trade 目录 (如: BTCUSDT/trades/2025/01)
        /// </summary>
        public static string GetTradeDataPath(string coin, int year, int month)
        {
            string path = Path.Combine(GetRootPath(), coin.ToUpper(), "trades", year.ToString(), month.ToString("D2"));
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 获取指定日期的 Trade 文件名 (如: BTCUSDT-trades-2025-01-01.zip)
        /// </summary>
        public static string GetTradeFileName(string coin, DateTime date, string extension = ".zip")
        {
            string ext = extension.StartsWith(".") ? extension : "." + extension;
            return $"{coin.ToUpper()}-trades-{date:yyyy-MM-dd}{ext}";
        }

        /// <summary>
        /// 获取指定日期的 Trade 文件完整路径 (如: BTCUSDT/trades/2025/01/BTCUSDT-trades-2025-01-01.zip)
        /// </summary>
        public static string GetTradeFilePath(string coin, DateTime date, string extension = ".zip")
        {
            string dir = GetTradeDataPath(coin, date.Year, date.Month);
            string fileName = GetTradeFileName(coin, date, extension);
            return Path.Combine(dir, fileName);
        }

        #endregion

        #region 日志与图表路径管理

        public static string GetLogsPath()
        {
            string path = Path.Combine(GetRootPath(), "logs");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string GetChartsPath()
        {
            string path = Path.Combine(GetRootPath(), "charts");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        #endregion
    }
}

