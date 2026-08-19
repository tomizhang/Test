using Binance.Net.Enums;
using Common;
using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Test.Strategy
{
    /// <summary>
    /// 趋势线策略 (TrendLine Strategy)
    /// 核心功能：
    /// 1. 自动维护最近 2000 根周期 K 线的滑动窗口
    /// 2. 每当接收到 OnKline 时，自动计算最新高低点 (波峰/波谷) 与趋势线 (支撑线/阻力线)
    /// 3. 保存计算结果，内置趋势线历史缓存库 (默认最低保存 1000 条)
    /// </summary>
    public class TrendLineStrategy
    {
        public string Symbol { get; private set; } = "BTCUSDT";
        public KlineInterval Interval { get; private set; } = KlineInterval.OneMinute;

        // 1. K 线滑动窗口配置 (默认保留 2000 根)
        public int MaxKlinesCapacity { get; set; } = 2000;

        // 2. 趋势线历史保存容量配置 (初定最低保存 1000 条)
        public int MinTrendLinesCapacity { get; set; } = 1000;

        // 3. 极值与趋势线计算参数配置
        public int LeftLen { get; set; } = 5;               // 波峰波谷左侧对比根数
        public int RightLen { get; set; } = 5;              // 波峰波谷右侧对比根数
        public int MaxSpan { get; set; } = 100;             // 两点间最大 K 线跨度
        public bool AllowInternalPenetration { get; set; } = false; // 是否允许内部 K 线穿透 (默认严格外包络)

        // K 线滑动窗口历史缓存
        private readonly List<RawKline> _klines = new List<RawKline>(2000);
        public IReadOnlyList<RawKline> Klines => _klines;
        public int KlineCount => _klines.Count;

        // 当前最新一帧计算的波峰与波谷
        public List<PivotPoint> CurrentPeaks { get; private set; } = new List<PivotPoint>();
        public List<PivotPoint> CurrentValleys { get; private set; } = new List<PivotPoint>();

        // 当前最新一帧计算的活跃阻力线与支撑线
        public List<TrendLine> CurrentResistanceLines { get; private set; } = new List<TrendLine>();
        public List<TrendLine> CurrentSupportLines { get; private set; } = new List<TrendLine>();

        // 历史趋势线库 (累计保存所有计算出的有效趋势线，初定最低保存 1000 条)
        private readonly List<TrendLine> _historicalTrendLines = new List<TrendLine>(1000);
        public IReadOnlyList<TrendLine> HistoricalTrendLines => _historicalTrendLines;
        public int HistoricalTrendLinesCount => _historicalTrendLines.Count;

        // 当前最新行情快照缓存
        public RawTick LatestTick { get; private set; }
        public RawKline LatestKline { get; private set; }
        public bool HasTickData { get; private set; } = false;
        public bool HasKlineData { get; private set; } = false;

        public TrendLineStrategy()
        {
        }

        public TrendLineStrategy(string symbol, KlineInterval interval, int maxKlines = 2000, int minTrendLines = 1000)
        {
            MaxKlinesCapacity = maxKlines;
            MinTrendLinesCapacity = minTrendLines;
            Initialize(symbol, interval);
        }

        /// <summary>
        /// 策略初始化
        /// </summary>
        /// <param name="symbol">交易对 (如 BTCUSDT)</param>
        /// <param name="interval">K线基准周期 (如 1m, 15m, 1h)</param>
        public void Initialize(string symbol, KlineInterval interval)
        {
            Symbol = symbol;
            Interval = interval;
            Reset();
        }

        /// <summary>
        /// 接收 Tick 逐笔行情推送
        /// </summary>
        /// <param name="tick">当前 Tick 原始结构体 (零装箱)</param>
        public void OnTick(in RawTick tick)
        {
            LatestTick = tick;
            HasTickData = true;

            // TODO: 在此处编写基于 Tick 价格的实时高频碰撞检测、动态止盈止损或微观信号逻辑
        }

        /// <summary>
        /// 接收 K 线周期行情推送（周期切分/Bar Close 收盘事件）
        /// 1. 将新 K 线加入滑动窗口，始终严格保留最近 2000 根
        /// 2. 实时计算当前极值点 (波峰与波谷) 与趋势线 (阻力线与支撑线)
        /// 3. 保存计算结果到当前活跃列表与历史趋势线库 (最低保存 1000 条)
        /// </summary>
        /// <param name="kline">已完成收盘的 K 线原始结构体</param>
        public void OnKline(in RawKline kline)
        {
            LatestKline = kline;
            HasKlineData = true;

            // 1. 滑动窗口维护：添加最新 K 线，超额时移除最老 K 线 (严格保留 2000 根)
            _klines.Add(kline);
            if (_klines.Count > MaxKlinesCapacity)
            {
                _klines.RemoveAt(0);
            }

            // 2. 当 K 线数据量足以构成左右分形时，触发高低点与趋势线计算
            if (_klines.Count >= LeftLen + RightLen + 1)
            {
                // ① 计算当前局部高低点 (波峰/波谷)
                var (peaks, valleys) = PivotHelper.CalculatePeaks(_klines, LeftLen, RightLen);
                CurrentPeaks = peaks;
                CurrentValleys = valleys;

                // ② 拟合生成当前支撑与阻力趋势线
                var (resistanceLines, supportLines) = TrendLineHelper.GenerateTrendLines(
                    _klines,
                    peaks,
                    valleys,
                    maxSpan: MaxSpan,
                    allowInternalPenetration: AllowInternalPenetration);

                CurrentResistanceLines = resistanceLines;
                CurrentSupportLines = supportLines;

                // ③ 保存计算生成的趋势线到历史库中 (去重并保持最低 1000 条容量)
                SaveTrendLinesToHistory(resistanceLines);
                SaveTrendLinesToHistory(supportLines);
            }

            // TODO: 在此处编写形态识别、通道收敛、趋势线突破信号等核心策略决策逻辑
        }

        /// <summary>
        /// 将新计算的趋势线保存到历史趋势线库中
        /// </summary>
        private void SaveTrendLinesToHistory(List<TrendLine> newLines)
        {
            if (newLines == null || newLines.Count == 0) return;

            for (int i = 0; i < newLines.Count; i++)
            {
                var line = newLines[i];
                // 按起始点时间戳与终止点时间戳去重
                bool exists = false;
                for (int j = _historicalTrendLines.Count - 1; j >= 0; j--)
                {
                    var h = _historicalTrendLines[j];
                    if (h.TimestampMs1 == line.TimestampMs1 && h.TimestampMs2 == line.TimestampMs2 && h.Type == line.Type)
                    {
                        exists = true;
                        // 更新延伸信息
                        _historicalTrendLines[j] = line;
                        break;
                    }
                }

                if (!exists)
                {
                    _historicalTrendLines.Add(line);
                }
            }

            // 若历史趋势线数量超过最低保存目标（且超出上限），滑动移除最早的历史趋势线
            int maxHistoryLimit = Math.Max(MinTrendLinesCapacity, 5000);
            if (_historicalTrendLines.Count > maxHistoryLimit)
            {
                int removeCount = _historicalTrendLines.Count - maxHistoryLimit;
                _historicalTrendLines.RemoveRange(0, removeCount);
            }
        }

        /// <summary>
        /// 获取策略当前运行状态与趋势线统计摘要
        /// </summary>
        public string GetStrategySummary()
        {
            return $"[TrendLineStrategy - {Symbol} {Interval.ToIntervalString()}] " +
                   $"Klines: {_klines.Count}/{MaxKlinesCapacity} | " +
                   $"Peaks: {CurrentPeaks.Count}, Valleys: {CurrentValleys.Count} | " +
                   $"Active Resistance: {CurrentResistanceLines.Count}, Support: {CurrentSupportLines.Count} | " +
                   $"History TrendLines Saved: {_historicalTrendLines.Count}";
        }

        /// <summary>
        /// 重置策略状态
        /// </summary>
        public void Reset()
        {
            _klines.Clear();
            CurrentPeaks.Clear();
            CurrentValleys.Clear();
            CurrentResistanceLines.Clear();
            CurrentSupportLines.Clear();
            _historicalTrendLines.Clear();

            LatestTick = default;
            LatestKline = default;
            HasTickData = false;
            HasKlineData = false;
        }
    }
}
