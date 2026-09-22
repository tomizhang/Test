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
        private decimal _minPriceChangePct = 0.0m;
        private ConsecutiveChannelPriceMode _channelPriceMode = ConsecutiveChannelPriceMode.Close;
        private bool _enableChannelAutoUpdate = true;
        private ChannelUpdateMode _channelUpdateMode = ChannelUpdateMode.Rolling;
        private Func<int, int, bool>? _hasTradingSignalFunc;
        private readonly HashSet<string> _loggedUpdateKeys = new();

        /// <summary>
        /// 平行通道拟合取值模式 (默认 Close 收盘价窄通道，可选 HighLow 宽通道)
        /// </summary>
        public ConsecutiveChannelPriceMode ChannelPriceMode
        {
            get => _channelPriceMode;
            set => _channelPriceMode = value;
        }

        /// <summary>
        /// 是否开启 K 线超出通道且无交易信号时自动更新绘制新通道 (默认开启)
        /// </summary>
        public bool EnableChannelAutoUpdate
        {
            get => _enableChannelAutoUpdate;
            set => _enableChannelAutoUpdate = value;
        }

        /// <summary>
        /// 通道动态更新模式 (默认 Rolling 滚动最新 MinBars 根，可选 Expanding 扩展全波段)
        /// </summary>
        public ChannelUpdateMode ChannelUpdateMode
        {
            get => _channelUpdateMode;
            set => _channelUpdateMode = value;
        }

        /// <summary>
        /// 外部交易信号查询委托：(trendId, upToBarIndex) => 是否已产生做单交易信号
        /// </summary>
        public Func<int, int, bool>? HasTradingSignalFunc
        {
            get => _hasTradingSignalFunc;
            set => _hasTradingSignalFunc = value;
        }

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
        /// 通道动态更新事件委托 (波段对象, 历史快照, 描述说明)
        /// </summary>
        public event Action<ConsecutiveTrendItem, ChannelSnapshot, string>? OnChannelUpdated;

        /// <summary>
        /// 重置所有状态与已识别形态
        /// </summary>
        public void Reset()
        {
            _detectedTrends.Clear();
            _loggedUpdateKeys.Clear();
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

            // 移除在 targetBarIndex 之后的通道更新日志标记
            _loggedUpdateKeys.RemoveWhere(k =>
            {
                var parts = k.Split('_');
                return parts.Length >= 2 && int.TryParse(parts[1], out int bIdx) && bIdx > targetBarIndex;
            });
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
            var scanned = ScanTrends(allKlines, maxIndex, _minBars, _minPriceChangePct, _channelPriceMode, _enableChannelAutoUpdate, _channelUpdateMode, _hasTradingSignalFunc);

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

                // 检查是否有新的通道更新事件需要通知
                if (t.IsChannelUpdated && t.PreviousChannels.Count > 0)
                {
                    for (int sIdx = 0; sIdx < t.PreviousChannels.Count; sIdx++)
                    {
                        var snap = t.PreviousChannels[sIdx];
                        string updateKey = $"{t.Id}_{snap.TriggerBarIndex}";
                        if (!_loggedUpdateKeys.Contains(updateKey))
                        {
                            _loggedUpdateKeys.Add(updateKey);
                            string trendDir = t.Type == ConsecutiveTrendType.Bullish ? "连续上涨" : "连续下跌";
                            string modeDesc = _channelUpdateMode == ChannelUpdateMode.Rolling ? $"滚动最新{_minBars}根" : "扩展全波段";
                            string msg = $"[通道动态更新] 🔄 {trendDir}形态已在 Bar #{snap.TriggerBarIndex} 更新绘制新通道！\n" +
                                         $"  └ 📋 更新理由: {snap.Reason} 且未出现交易信号\n" +
                                         $"  └ 📐 新通道参数: 拟合区间: Bar #{t.ChannelStartIndex}~#{t.ChannelEndIndex} ({modeDesc}) | 斜率:{t.SlopeK:+0.00;-0.00} | 高度:{t.ChannelHeight:F2} | 第 {sIdx + 1} 次更新";
                            OnChannelUpdated?.Invoke(t, snap, msg);
                        }
                    }
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
            decimal minChangePct,
            ConsecutiveChannelPriceMode priceMode = ConsecutiveChannelPriceMode.Close,
            bool enableAutoUpdate = true,
            ChannelUpdateMode updateMode = ChannelUpdateMode.Rolling,
            Func<int, int, bool>? hasTradingSignalFunc = null)
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
                    // 一旦达到最小根数门槛 (默认 5 根)，立即在第 5 根当根确立！
                    if (countSoFar >= minBars && (minChangePct <= 0m || absChange >= minChangePct) && confirmedAt < 0)
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

                if (confirmedAt >= 0 && totalCount >= minBars && (minChangePct <= 0m || Math.Abs(finalChangePct) >= minChangePct))
                {
                    // 若当前连涨/连跌延续到了最新推进的 K 线 (maxIndex)，则处于活跃延伸状态
                    bool isActive = streakEnd == maxIndex;

                    // 确定性稳定 ID，保证在后续帧动态延伸时不重复触发确立事件
                    int stableId = (currentStreakDir > 0 ? 1000000 : 2000000) + streakStart;

                    // 核心关键：平行通道基准形态严格在首次达到门槛的 K 线 [streakStart, confirmedAt]（例如正好第 5 根）确立！
                    // 无需知道下一根（第 6 根、第 7 根...），在第 5 根闭合时通道即刻完全成型并立马绘制！
                    FitParallelChannel(klines, streakStart, confirmedAt, out decimal slopeK, out decimal upperB, out decimal lowerB, priceMode);

                    int activeStart = streakStart;
                    int activeEnd = confirmedAt;
                    decimal activeSlope = slopeK;
                    decimal activeUpper = upperB;
                    decimal activeLower = lowerB;
                    int updateCount = 0;
                    var snapshots = new List<ChannelSnapshot>();

                    // 优化 1：若开启超出通道自动更新新通道，考察 [confirmedAt + 1, streakEnd] 区间
                    if (enableAutoUpdate && confirmedAt < streakEnd)
                    {
                        for (int k = confirmedAt + 1; k <= streakEnd; k++)
                        {
                            // 检查在 bar k 结束前是否已产生做单交易信号
                            bool hasSignal = hasTradingSignalFunc != null && hasTradingSignalFunc(stableId, k);
                            if (hasSignal)
                            {
                                // 一旦产生做单交易信号，锁定通道不再更新
                                break;
                            }

                            // 检查 bar k 是否超出当前活跃通道
                            bool exceeded = false;
                            decimal checkPrice = 0m;
                            if (currentStreakDir == 1) // Bullish
                            {
                                decimal curUpper = activeSlope * k + activeUpper;
                                checkPrice = priceMode switch
                                {
                                    ConsecutiveChannelPriceMode.Close => klines[k].Close,
                                    ConsecutiveChannelPriceMode.OpenClose => Math.Max(klines[k].Open, klines[k].Close),
                                    _ => Math.Max(klines[k].High, klines[k].Close)
                                };
                                exceeded = checkPrice > curUpper;
                            }
                            else // Bearish
                            {
                                decimal curLower = activeSlope * k + activeLower;
                                checkPrice = priceMode switch
                                {
                                    ConsecutiveChannelPriceMode.Close => klines[k].Close,
                                    ConsecutiveChannelPriceMode.OpenClose => Math.Min(klines[k].Open, klines[k].Close),
                                    _ => Math.Min(klines[k].Low, klines[k].Close)
                                };
                                exceeded = checkPrice < curLower;
                            }

                            if (exceeded)
                            {
                                // 记录旧通道快照
                                string dirDesc = currentStreakDir == 1 ? "超出通道上轨" : "跌破通道下轨";
                                decimal boundVal = currentStreakDir == 1 ? (activeSlope * k + activeUpper) : (activeSlope * k + activeLower);
                                var snap = new ChannelSnapshot
                                {
                                    StartIndex = activeStart,
                                    EndIndex = activeEnd,
                                    SlopeK = activeSlope,
                                    UpperIntercept = activeUpper,
                                    LowerIntercept = activeLower,
                                    TriggerBarIndex = k,
                                    TriggerPrice = checkPrice,
                                    Reason = $"Bar #{k} {dirDesc} (价:{checkPrice:F2}, 轨:{boundVal:F2})"
                                };
                                snapshots.Add(snap);

                                // 计算新通道拟合区间
                                int newStart = updateMode == ChannelUpdateMode.Rolling
                                    ? Math.Max(streakStart, k - minBars + 1)
                                    : streakStart;
                                int newEnd = k;

                                FitParallelChannel(klines, newStart, newEnd, out decimal newSlope, out decimal newUpper, out decimal newLower, priceMode);
                                activeStart = newStart;
                                activeEnd = newEnd;
                                activeSlope = newSlope;
                                activeUpper = newUpper;
                                activeLower = newLower;
                                updateCount++;
                            }
                        }
                    }

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
                        PriceMode = priceMode,
                        SlopeK = activeSlope,
                        UpperIntercept = activeUpper,
                        LowerIntercept = activeLower,
                        HasChannel = activeUpper > activeLower,
                        ChannelStartIndex = activeStart,
                        ChannelEndIndex = activeEnd,
                        IsChannelUpdated = updateCount > 0,
                        ChannelUpdateCount = updateCount
                    };
                    item.PreviousChannels.AddRange(snapshots);
                    result.Add(item);
                }

                // 下一次从断点 j 开始继续寻找下一个趋势波段
                i = j;
            }

            return result;
        }

        /// <summary>
        /// 针对指定连续 K 线区间拟合严格平行的通道
        /// 1. Close 模式：以 Close 进行一元线性回归斜率拟合，并以 Close 在回归线上的最大正残差和最大负残差作为上轨与下轨（紧贴实体窄通道）
        /// 2. HighLow 模式：以 (High+Low)/2 进行回归，以 High 最大残差与 Low 最小残差作为外包络（宽通道）
        /// 3. OpenClose 模式：以 (Open+Close)/2 为回归中心，以 Max(Open,Close) 最大残差与 Min(Open,Close) 最小残差作为实体边界（实体通道）
        /// </summary>
        public static void FitParallelChannel(
            IReadOnlyList<RawKline> klines,
            int startIndex,
            int endIndex,
            out decimal slopeK,
            out decimal upperB,
            out decimal lowerB,
            ConsecutiveChannelPriceMode priceMode = ConsecutiveChannelPriceMode.Close)
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
                if (priceMode == ConsecutiveChannelPriceMode.Close)
                {
                    decimal c = klines[startIndex].Close;
                    decimal span = Math.Max(0.01m, c * 0.0005m);
                    upperB = c + span;
                    lowerB = c - span;
                }
                else if (priceMode == ConsecutiveChannelPriceMode.OpenClose)
                {
                    decimal top = Math.Max(klines[startIndex].Open, klines[startIndex].Close);
                    decimal btm = Math.Min(klines[startIndex].Open, klines[startIndex].Close);
                    if (top <= btm)
                    {
                        decimal span = Math.Max(0.01m, top * 0.0005m);
                        top += span;
                        btm -= span;
                    }
                    upperB = top;
                    lowerB = btm;
                }
                else
                {
                    upperB = klines[startIndex].High;
                    lowerB = klines[startIndex].Low;
                }
                return;
            }

            if (priceMode == ConsecutiveChannelPriceMode.Close)
            {
                // 1. 窄通道拟合：以 Close 价格序列作为回归中心
                decimal sumX = 0m;
                decimal sumY = 0m;
                for (int idx = startIndex; idx <= endIndex; idx++)
                {
                    sumX += idx;
                    sumY += klines[idx].Close;
                }
                decimal meanX = sumX / n;
                decimal meanY = sumY / n;

                decimal num = 0m;
                decimal den = 0m;
                for (int idx = startIndex; idx <= endIndex; idx++)
                {
                    decimal dx = idx - meanX;
                    decimal dy = klines[idx].Close - meanY;
                    num += dx * dy;
                    den += dx * dx;
                }

                slopeK = den != 0m ? num / den : 0m;

                // 2. 窄通道边界：以 Close 在回归线上的最大残差作为上轨，最小残差作为下轨
                decimal maxDiffC = decimal.MinValue;
                decimal minDiffC = decimal.MaxValue;
                for (int idx = startIndex; idx <= endIndex; idx++)
                {
                    decimal diffC = klines[idx].Close - slopeK * idx;
                    if (diffC > maxDiffC) maxDiffC = diffC;
                    if (diffC < minDiffC) minDiffC = diffC;
                }

                upperB = maxDiffC;
                lowerB = minDiffC;

                // 若所有 Close 恰好共线 (上轨 == 下轨)，赋予极小的微通道厚度保证通道存在
                if (upperB <= lowerB)
                {
                    decimal minHeight = Math.Max(0.01m, meanY * 0.0005m);
                    upperB += minHeight / 2m;
                    lowerB -= minHeight / 2m;
                }
            }
            else if (priceMode == ConsecutiveChannelPriceMode.OpenClose)
            {
                // 2. 开收实体通道拟合：以 (Open + Close) / 2 为回归中心，以 Max(Open, Close) 和 Min(Open, Close) 为实体包络
                decimal sumX = 0m;
                decimal sumY = 0m;
                for (int idx = startIndex; idx <= endIndex; idx++)
                {
                    sumX += idx;
                    sumY += (klines[idx].Open + klines[idx].Close) / 2m;
                }
                decimal meanX = sumX / n;
                decimal meanY = sumY / n;

                decimal num = 0m;
                decimal den = 0m;
                for (int idx = startIndex; idx <= endIndex; idx++)
                {
                    decimal dx = idx - meanX;
                    decimal dy = ((klines[idx].Open + klines[idx].Close) / 2m) - meanY;
                    num += dx * dy;
                    den += dx * dx;
                }

                slopeK = den != 0m ? num / den : 0m;

                decimal maxDiffBody = decimal.MinValue;
                decimal minDiffBody = decimal.MaxValue;
                for (int idx = startIndex; idx <= endIndex; idx++)
                {
                    decimal bodyTop = Math.Max(klines[idx].Open, klines[idx].Close);
                    decimal bodyBottom = Math.Min(klines[idx].Open, klines[idx].Close);
                    decimal diffTop = bodyTop - slopeK * idx;
                    decimal diffBottom = bodyBottom - slopeK * idx;
                    if (diffTop > maxDiffBody) maxDiffBody = diffTop;
                    if (diffBottom < minDiffBody) minDiffBody = diffBottom;
                }

                upperB = maxDiffBody;
                lowerB = minDiffBody;

                // 若所有实体极值恰好共线，赋予极小的微通道厚度
                if (upperB <= lowerB)
                {
                    decimal minHeight = Math.Max(0.01m, meanY * 0.0005m);
                    upperB += minHeight / 2m;
                    lowerB -= minHeight / 2m;
                }
            }
            else
            {
                // 宽通道拟合：以 (High + Low) / 2 为回归中心，以 High 的最大偏差和 Low 的最小偏差作为外包络
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
        }

        /// <summary>
        /// 判定第 index 根 K 线是否属于上涨 K 线 (相较前一根收盘价上涨，或自身为收红阳线且不低于前收)
        /// </summary>
        private static bool IsRisingBar(IReadOnlyList<RawKline> klines, int index)
        {
            if (index < 0 || index >= klines.Count) return false;
            var curr = klines[index];
            if (index == 0) return curr.Close >= curr.Open;
            var prev = klines[index - 1];

            return curr.Close > prev.Close || curr.Close >= curr.Open;
        }

        /// <summary>
        /// 判定第 index 根 K 线是否属于下跌 K 线 (相较前一根收盘价下跌，或自身为阴线且不高于前收)
        /// </summary>
        private static bool IsFallingBar(IReadOnlyList<RawKline> klines, int index)
        {
            if (index < 0 || index >= klines.Count) return false;
            var curr = klines[index];
            if (index == 0) return curr.Close <= curr.Open;
            var prev = klines[index - 1];

            return curr.Close < prev.Close || curr.Close <= curr.Open;
        }
    }
}
