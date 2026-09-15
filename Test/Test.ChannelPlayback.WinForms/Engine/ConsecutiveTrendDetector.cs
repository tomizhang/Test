using Common;
using System;
using System.Collections.Generic;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    /// <summary>
    /// 连续上涨 / 连续下跌动能波段专业识别引擎
    /// 核心逻辑：
    /// 1. 连续上涨：至少连续 5 根及以上收盘走高/收阳，且累计涨幅达到 2.5% 及以上
    /// 2. 连续下跌：至少连续 5 根及以上收盘走低/收阴，且累计跌幅达到 2.5% 及以上
    /// 3. 动态展开：一旦首次满足门槛立即确立，并在行情延续时动态向右扩展，走势反转时锁定终止
    /// 4. 时间轴回滚：完美支持 SyncTo 历史时间轴回滚与流式推演
    /// </summary>
    public class ConsecutiveTrendDetector
    {
        private readonly List<ConsecutiveTrendItem> _detectedTrends = new();
        private bool _enableDetection = true;
        private int _minBars = 5;
        private decimal _minPriceChangePct = 2.5m;

        /// <summary>
        /// 当前已识别并确认的所有连续涨跌波段列表
        /// </summary>
        public IReadOnlyList<ConsecutiveTrendItem> DetectedTrends => _detectedTrends;

        /// <summary>
        /// 是否开启连续形态识别
        /// </summary>
        public bool EnableDetection
        {
            get => _enableDetection;
            set => _enableDetection = value;
        }

        /// <summary>
        /// 要求的最小连续 K 线根数门槛 (默认 5 根)
        /// </summary>
        public int MinBars
        {
            get => _minBars;
            set => _minBars = Math.Max(2, value);
        }

        /// <summary>
        /// 要求的累计涨跌幅百分比门槛 (默认 2.5%)
        /// </summary>
        public decimal MinPriceChangePct
        {
            get => _minPriceChangePct;
            set => _minPriceChangePct = Math.Max(0m, value);
        }

        /// <summary>
        /// 连续波段确立事件委托 (波段对象, 首次确认 K 线索引)
        /// </summary>
        public event Action<ConsecutiveTrendItem, int>? OnTrendDetected;

        /// <summary>
        /// 重置所有状态与已识别形态
        /// </summary>
        public void Reset()
        {
            _detectedTrends.Clear();
        }

        /// <summary>
        /// 时间轴跳转或回退
        /// </summary>
        public void SyncTo(int targetBarIndex)
        {
            if (targetBarIndex < 0)
            {
                Reset();
                return;
            }

            // 移除在 targetBarIndex 之后才被首次确认的形态
            _detectedTrends.RemoveAll(t => t.ConfirmedBarIndex > targetBarIndex);

            // 对保留下来的形态，截断其截止位置
            foreach (var t in _detectedTrends)
            {
                if (t.EndIndex > targetBarIndex)
                {
                    t.EndIndex = targetBarIndex;
                    t.IsActive = true;
                }
            }
        }

        /// <summary>
        /// 针对当前推进到的最新 K 线序列，全量自适应扫描并更新形态列表
        /// </summary>
        public void ProcessCurrentSequence(IReadOnlyList<RawKline> allKlines, int currentBarIndex)
        {
            if (!_enableDetection || allKlines == null || currentBarIndex < _minBars - 1)
            {
                _detectedTrends.Clear();
                return;
            }

            int maxIndex = Math.Clamp(currentBarIndex, 0, allKlines.Count - 1);
            var scanned = ScanTrends(allKlines, maxIndex, _minBars, _minPriceChangePct);

            // 比对新触发的确认事件
            var existingIds = new HashSet<int>();
            for (int i = 0; i < _detectedTrends.Count; i++) existingIds.Add(_detectedTrends[i].Id);

            _detectedTrends.Clear();
            _detectedTrends.AddRange(scanned);

            for (int i = 0; i < scanned.Count; i++)
            {
                var t = scanned[i];
                if (!existingIds.Contains(t.Id))
                {
                    OnTrendDetected?.Invoke(t, t.ConfirmedBarIndex);
                }
            }
        }

        /// <summary>
        /// 静态扫描算法：以 O(N) 线性遍历精准识别所有达标连续涨跌波段
        /// </summary>
        public static List<ConsecutiveTrendItem> ScanTrends(
            IReadOnlyList<RawKline> klines,
            int maxIndex,
            int minBars,
            decimal minChangePct)
        {
            var result = new List<ConsecutiveTrendItem>();
            if (klines == null || maxIndex < minBars - 1) return result;

            int n = maxIndex + 1;
            int i = 0;

            while (i < n)
            {
                // 检查第 i 根 K 线是上涨还是下跌 (严格根据当前已走完的 K 线，零未来函数)
                int currentStreakDir = 0;
                if (IsRisingBar(klines, i)) currentStreakDir = 1;
                else if (IsFallingBar(klines, i)) currentStreakDir = -1;
                else
                {
                    i++;
                    continue;
                }

                int streakStart = i; // 连涨/连跌的第一根达标 K 线
                // 基准启动价格：优先取该波段启动前一根的收盘价，若不存在则取第一根的开盘价
                decimal startPrice = streakStart > 0 ? klines[streakStart - 1].Close : klines[streakStart].Open;
                if (startPrice <= 0m) startPrice = klines[streakStart].Open;

                int confirmedAt = -1;
                int j = i;

                while (j < n)
                {
                    bool continues = currentStreakDir == 1 ? IsRisingBar(klines, j) : IsFallingBar(klines, j);
                    if (!continues)
                    {
                        break;
                    }

                    int countSoFar = j - streakStart + 1;
                    decimal endPrice = klines[j].Close;
                    decimal changePct = startPrice > 0m
                        ? ((endPrice - startPrice) / startPrice) * 100m
                        : 0m;

                    decimal absChange = Math.Abs(changePct);
                    // 一旦达到最小根数门槛 (默认 5 根) 且累计幅度达到门槛 (默认 2.5%)，立即在当根确立！
                    if (countSoFar >= minBars && absChange >= minChangePct && confirmedAt < 0)
                    {
                        confirmedAt = j;
                    }

                    j++;
                }

                int streakEnd = j - 1;
                int totalCount = streakEnd - streakStart + 1;
                decimal finalEndPrice = klines[streakEnd].Close;
                decimal finalChangePct = startPrice > 0m
                    ? ((finalEndPrice - startPrice) / startPrice) * 100m
                    : 0m;

                if (confirmedAt >= 0 && totalCount >= minBars && Math.Abs(finalChangePct) >= minChangePct)
                {
                    // 若当前连涨/连跌延续到了最新推进的 K 线 (maxIndex)，则处于活跃延伸状态
                    bool isActive = streakEnd == maxIndex;

                    // 确定性稳定 ID，保证在后续帧动态延伸时不重复触发确立事件
                    int stableId = (currentStreakDir > 0 ? 1000000 : 2000000) + streakStart;

                    // 核心关键：平行通道基准形态严格在首次达到门槛的 K 线 [streakStart, confirmedAt]（例如正好第 5 根）确立！
                    // 无需知道下一根（第 6 根、第 7 根...），在第 5 根闭合时通道即刻完全成型并立马绘制！
                    FitParallelChannel(klines, streakStart, confirmedAt, out decimal slopeK, out decimal upperB, out decimal lowerB);

                    var item = new ConsecutiveTrendItem
                    {
                        Id = stableId,
                        Type = currentStreakDir == 1 ? ConsecutiveTrendType.Bullish : ConsecutiveTrendType.Bearish,
                        StartIndex = streakStart,
                        StartPrice = startPrice,
                        EndIndex = streakEnd,
                        EndPrice = finalEndPrice,
                        PriceChangePct = finalChangePct,
                        ConfirmedBarIndex = confirmedAt,
                        IsActive = isActive,
                        SlopeK = slopeK,
                        UpperIntercept = upperB,
                        LowerIntercept = lowerB,
                        HasChannel = upperB > lowerB
                    };
                    result.Add(item);
                }

                // 下一次从断点 j 开始继续寻找下一个趋势波段
                i = j;
            }

            return result;
        }

        /// <summary>
        /// 针对指定连续 K 线区间拟合严格平行的外包络通道
        /// 核心：通过线性回归计算波段整体斜率 k，并通过最大 High 差值与最小 Low 差值确定平行的上轨与下轨
        /// </summary>
        public static void FitParallelChannel(
            IReadOnlyList<RawKline> klines,
            int startIndex,
            int endIndex,
            out decimal slopeK,
            out decimal upperB,
            out decimal lowerB)
        {
            slopeK = 0m;
            upperB = 0m;
            lowerB = 0m;
            if (klines == null || startIndex < 0 || endIndex >= klines.Count || endIndex < startIndex)
            {
                return;
            }

            int n = endIndex - startIndex + 1;
            if (n < 2)
            {
                upperB = klines[startIndex].High;
                lowerB = klines[startIndex].Low;
                return;
            }

            decimal sumX = 0m;
            decimal sumY = 0m;
            for (int idx = startIndex; idx <= endIndex; idx++)
            {
                sumX += idx;
                sumY += (klines[idx].High + klines[idx].Low) / 2m;
            }
            decimal meanX = sumX / n;
            decimal meanY = sumY / n;

            decimal num = 0m;
            decimal den = 0m;
            for (int idx = startIndex; idx <= endIndex; idx++)
            {
                decimal dx = idx - meanX;
                decimal dy = ((klines[idx].High + klines[idx].Low) / 2m) - meanY;
                num += dx * dy;
                den += dx * dx;
            }

            slopeK = den != 0m ? num / den : 0m;

            decimal maxDiffH = decimal.MinValue;
            decimal minDiffL = decimal.MaxValue;
            for (int idx = startIndex; idx <= endIndex; idx++)
            {
                decimal diffH = klines[idx].High - slopeK * idx;
                decimal diffL = klines[idx].Low - slopeK * idx;
                if (diffH > maxDiffH) maxDiffH = diffH;
                if (diffL < minDiffL) minDiffL = diffL;
            }

            upperB = maxDiffH;
            lowerB = minDiffL;
        }

        /// <summary>
        /// 判定第 index 根 K 线是否属于上涨 K 线 (相较前一根收盘价上涨，或自身为收红阳线且不低于前收)
        /// </summary>
        private static bool IsRisingBar(IReadOnlyList<RawKline> klines, int index)
        {
            if (index < 0 || index >= klines.Count) return false;
            var curr = klines[index];
            if (index == 0) return curr.Close > curr.Open;
            var prev = klines[index - 1];

            return curr.Close > prev.Close || (curr.Close > curr.Open && curr.Close >= prev.Close);
        }

        /// <summary>
        /// 判定第 index 根 K 线是否属于下跌 K 线 (相较前一根收盘价下跌，或自身为阴线且不高于前收)
        /// </summary>
        private static bool IsFallingBar(IReadOnlyList<RawKline> klines, int index)
        {
            if (index < 0 || index >= klines.Count) return false;
            var curr = klines[index];
            if (index == 0) return curr.Close < curr.Open;
            var prev = klines[index - 1];

            return curr.Close < prev.Close || (curr.Close < curr.Open && curr.Close <= prev.Close);
        }
    }
}
