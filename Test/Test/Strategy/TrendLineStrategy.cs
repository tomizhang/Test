using Binance.Net.Enums;
using Common;
using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Test.Strategy
{
    /// <summary>
    /// 高性能三层增量趋势线策略 (Multi-Layer Incremental TrendLine Strategy)
    /// 架构设计：
    /// 1. 自动维护 2000 根 K 线的滑动窗口，使用全局单调索引 (_globalBarIndex) 消除窗口滑动对坐标系的影响
    /// 2. 第 1 层 (O(1)): 单步增量极值判定，仅在候选点走出右侧确认窗口时进行一次 10 步比较
    /// 3. 第 2 层 (O(M)): 增量趋势线生成，当且仅当产生新极值点时，仅与历史活跃极值点单向配对
    /// 4. 第 3 层 (O(ActiveLines)): 增量延伸碰撞更新，单次代入最新 K 线完成寿命自增与碰撞判定 (耗时 < 1 微秒)
    /// 5. 趋势线历史库持久化 (最低保存 1000 条)
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

        // 全局单调递增 K 线序列号计数器 (0, 1, 2, ... 500,000)
        private int _globalBarIndex = 0;
        public int GlobalBarIndex => _globalBarIndex;

        // K 线滑动窗口历史缓存 (最大 2000 根)
        private readonly List<RawKline> _klines = new List<RawKline>(2000);
        public IReadOnlyList<RawKline> Klines => _klines;
        public int KlineCount => _klines.Count;

        // 历史已确认的所有波峰与波谷列表 (按全局单调索引递增)
        private readonly List<PivotPoint> _peaks = new List<PivotPoint>(500);
        private readonly List<PivotPoint> _valleys = new List<PivotPoint>(500);
        public IReadOnlyList<PivotPoint> Peaks => _peaks;
        public IReadOnlyList<PivotPoint> Valleys => _valleys;

        // 当前存量的活跃阻力线与支撑线
        public List<TrendLine> ActiveResistanceLines { get; } = new List<TrendLine>(500);
        public List<TrendLine> ActiveSupportLines { get; } = new List<TrendLine>(500);

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
        public void Initialize(string symbol, KlineInterval interval)
        {
            Symbol = symbol;
            Interval = interval;
            Reset();
        }

        /// <summary>
        /// 接收 Tick 逐笔行情推送
        /// </summary>
        public void OnTick(in RawTick tick)
        {
            LatestTick = tick;
            HasTickData = true;

            // TODO: 在此处编写基于 Tick 价格的实时高频碰撞检测、动态止盈止损或微观信号逻辑
        }

        /// <summary>
        /// 接收 K 线周期行情推送（周期切分/Bar Close 收盘事件）
        /// 采用三层增量流水线驱动计算：
        /// Layer 1: O(1) 判定候选点是否为新极值点
        /// Layer 2: 若产生新极值点，O(M) 增量生成新趋势线
        /// Layer 3: O(ActiveLines) 增量推进存量趋势线寿命与碰撞检测
        /// </summary>
        public void OnKline(in RawKline kline)
        {
            LatestKline = kline;
            HasKlineData = true;

            int currentGlobalIndex = _globalBarIndex++;

            // 1. 滑动窗口维护：追加新 K 线，超额时移除最老 K 线 (严格保留 2000 根)
            _klines.Add(kline);
            if (_klines.Count > MaxKlinesCapacity)
            {
                _klines.RemoveAt(0);
            }

            // 2. 【第 1 层: O(1) 增量极值判定】
            // 候选点位置为当前全局索引倒数第 (RightLen) 根
            int candidateGlobalIndex = currentGlobalIndex - RightLen;
            var (hasPeak, hasValley, newPeak, newValley) = PivotHelper.TryDetectIncrementalPivot(
                _klines,
                candidateGlobalIndex,
                LeftLen,
                RightLen);

            // 3. 【第 2 层: O(M) 增量趋势线生成】
            if (hasPeak)
            {
                // 生成新阻力线并加入活跃列表与历史库
                var newResistanceLines = TrendLineHelper.GenerateIncrementalTrendLines(
                    newPeak,
                    _peaks,
                    _klines,
                    currentGlobalIndex,
                    maxSpan: MaxSpan,
                    allowInternalPenetration: AllowInternalPenetration);

                _peaks.Add(newPeak);
                ActiveResistanceLines.AddRange(newResistanceLines);
                SaveTrendLinesToHistory(newResistanceLines);
            }

            if (hasValley)
            {
                // 生成新支撑线并加入活跃列表与历史库
                var newSupportLines = TrendLineHelper.GenerateIncrementalTrendLines(
                    newValley,
                    _valleys,
                    _klines,
                    currentGlobalIndex,
                    maxSpan: MaxSpan,
                    allowInternalPenetration: AllowInternalPenetration);

                _valleys.Add(newValley);
                ActiveSupportLines.AddRange(newSupportLines);
                SaveTrendLinesToHistory(newSupportLines);
            }

            // 4. 【第 3 层: O(ActiveLines) 增量单步延伸与碰撞更新】
            TrendLineHelper.UpdateActiveTrendLinesStep(ActiveResistanceLines, kline, currentGlobalIndex);
            TrendLineHelper.UpdateActiveTrendLinesStep(ActiveSupportLines, kline, currentGlobalIndex);

            // 5. 适度清理长期已击穿且老化的非活跃趋势线 (保持活跃集合紧凑高效)
            PruneInactiveTrendLines(currentGlobalIndex);

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
                bool exists = false;
                for (int j = _historicalTrendLines.Count - 1; j >= 0; j--)
                {
                    var h = _historicalTrendLines[j];
                    if (h.TimestampMs1 == line.TimestampMs1 && h.TimestampMs2 == line.TimestampMs2 && h.Type == line.Type)
                    {
                        exists = true;
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
        /// 清理超龄且已被碰撞穿透的失效趋势线
        /// </summary>
        private void PruneInactiveTrendLines(int currentGlobalIndex)
        {
            // 活跃阻力线清理 (已被穿透超过 500 根 K 线的不再作为活跃线监控)
            ActiveResistanceLines.RemoveAll(line => line.CollidedKlineIndex != -1 && (currentGlobalIndex - line.CollidedKlineIndex > 500));
            ActiveSupportLines.RemoveAll(line => line.CollidedKlineIndex != -1 && (currentGlobalIndex - line.CollidedKlineIndex > 500));
        }

        /// <summary>
        /// 获取策略当前运行状态与趋势线统计摘要
        /// </summary>
        public string GetStrategySummary()
        {
            return $"[TrendLineStrategy (Incremental) - {Symbol} {Interval.ToIntervalString()}] " +
                   $"GlobalBars: {_globalBarIndex}, Window: {_klines.Count}/{MaxKlinesCapacity} | " +
                   $"Peaks: {_peaks.Count}, Valleys: {_valleys.Count} | " +
                   $"Active Resistance: {ActiveResistanceLines.Count}, Support: {ActiveSupportLines.Count} | " +
                   $"History TrendLines Saved: {_historicalTrendLines.Count}";
        }

        /// <summary>
        /// 重置策略状态
        /// </summary>
        public void Reset()
        {
            _globalBarIndex = 0;
            _klines.Clear();
            _peaks.Clear();
            _valleys.Clear();
            ActiveResistanceLines.Clear();
            ActiveSupportLines.Clear();
            _historicalTrendLines.Clear();

            LatestTick = default;
            LatestKline = default;
            HasTickData = false;
            HasKlineData = false;
        }
    }
}
