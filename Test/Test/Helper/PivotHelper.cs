using System;
using System.Collections.Generic;

namespace Common.Helper
{
    /// <summary>
    /// 极值高低点计算算法类型
    /// </summary>
    public enum PivotAlgorithmType
    {
        /// <summary>
        /// 经典双侧分形对比法 (Fractal: 左右对称 K 线窗口对比)
        /// </summary>
        Fractal = 0,

        /// <summary>
        /// ZigZag 之字转向算法 (基于最小百分比反转回撤识别宏观波段高低点)
        /// </summary>
        ZigZag = 1
    }

    /// <summary>
    /// 极值类型 (波峰 / 波谷)
    /// </summary>
    public enum PivotType
    {
        /// <summary>
        /// 波峰 / 局部高点 (Peak，取 K 线的 High 最高价)
        /// </summary>
        Peak = 1,

        /// <summary>
        /// 波谷 / 局部低点 (Valley，取 K 线的 Low 最低价)
        /// </summary>
        Valley = 2
    }

    /// <summary>
    /// 统一极值点 (波峰/波谷) 数据结构 (原生 decimal 强类型，包含 UTC+0 时间与全局单调下标)
    /// 规则：
    /// - 若为波峰 (Peak / 高点)，Price 严格取自对应 K 线的 High (最高价)
    /// - 若为波谷 (Valley / 低点)，Price 严格取自对应 K 线的 Low (最低价)
    /// </summary>
    public struct PivotPoint
    {
        /// <summary>
        /// 在全局 K 线历史序列中的全局单调下标 (Global Bar Index，不受滑动窗口移除影响)
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 极值点发生的 K 线时间 (UTC+0)
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// 极值点发生的 Unix 毫秒时间戳
        /// </summary>
        public long TimestampMs { get; set; }

        /// <summary>
        /// 极值点发生的 Unix 秒时间戳
        /// </summary>
        public long TimestampSeconds => TimestampMs / 1000;

        /// <summary>
        /// 极值价格：如果是高点(Peak)严格取 K 线的 High，如果是低点(Valley)严格取 K 线的 Low
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 极值价格别名 (若是高点则返回该高点的 High 价格)
        /// </summary>
        public decimal High => Price;

        /// <summary>
        /// 极值价格别名 (若是低点则返回该低点的 Low 价格)
        /// </summary>
        public decimal Low => Price;

        /// <summary>
        /// 极值类型 (Peak = 高点, Valley = 低点)
        /// </summary>
        public PivotType Type { get; set; }

        /// <summary>
        /// 是否为波峰 (高点)
        /// </summary>
        public bool IsPeak => Type == PivotType.Peak;

        /// <summary>
        /// 是否为波谷 (低点)
        /// </summary>
        public bool IsValley => Type == PivotType.Valley;

        /// <summary>
        /// 是否通过了分形（左右对称 K 线）二次严格确认
        /// </summary>
        public bool IsFractalConfirmed { get; set; }

        #region UI/日志按需格式化

        public string FormattedTime => Time.ToUtc0String();

        #endregion

        public override string ToString()
        {
            string symbol = IsPeak ? "▲高点(High)" : "▼低点(Low)";
            return $"[{FormattedTime} ({TimestampMs}ms)] {symbol} #{Index} 价格:{Price:F2} 分形确认:{IsFractalConfirmed}";
        }
    }

    /// <summary>
    /// 高性能局部高低点 (波峰/波谷) 计算引擎
    /// 核心规则：高点判定与取值严格使用 K 线的 High；低点判定与取值严格使用 K 线的 Low
    /// 支持全量扫描与 O(1) 毫秒级多层增量计算
    /// </summary>
    public static class PivotHelper
    {
        #region 1. 核心: O(1) 单步增量极值判定 (Incremental Pivot Detection)

        /// <summary>
        /// 增量极值判定：当新 K 线抵达时，仅对刚刚完成右侧确认窗口的那一根候选 K 线进行 O(1) 判定
        /// 规则：
        /// - 高点 (Peak)：取该候选 K 线的 High，与左右两翼 K 线的 High 进行对比判定
        /// - 低点 (Valley)：取该候选 K 线的 Low，与左右两翼 K 线的 Low 进行对比判定
        /// </summary>
        /// <param name="klines">当前 K 线滑动窗口</param>
        /// <param name="candidateGlobalIndex">候选 K 线的全局单调下标</param>
        /// <param name="leftLen">左侧需低于/高于当前极值的 K 线根数 (默认 5)</param>
        /// <param name="rightLen">右侧需低于/高于当前极值的 K 线根数 (默认 5)</param>
        /// <param name="newPeak">若确认为新高点，返回 Price 为 High 的波峰结构体</param>
        /// <param name="newValley">若确认为新低点，返回 Price 为 Low 的波谷结构体</param>
        /// <returns>是否有新极值点确认</returns>
        public static (bool hasPeak, bool hasValley, PivotPoint peak, PivotPoint valley) TryDetectIncrementalPivot(
            IReadOnlyList<RawKline> klines,
            int candidateGlobalIndex,
            int leftLen = 5,
            int rightLen = 5)
        {
            PivotPoint peak = default;
            PivotPoint valley = default;
            bool hasPeak = false;
            bool hasValley = false;

            if (klines == null || klines.Count < leftLen + rightLen + 1)
            {
                return (false, false, peak, valley);
            }

            int candidateLocalIdx = klines.Count - 1 - rightLen;
            if (candidateLocalIdx < leftLen)
            {
                return (false, false, peak, valley);
            }

            // 1. 高点判断：严格取候选 K 线的 High 最高价
            decimal candidateHigh = klines[candidateLocalIdx].High;

            // 2. 低点判断：严格取候选 K 线的 Low 最低价
            decimal candidateLow = klines[candidateLocalIdx].Low;

            bool isPeak = true;
            bool isValley = true;

            int startIdx = candidateLocalIdx - leftLen;
            int endIdx = candidateLocalIdx + rightLen;

            for (int j = startIdx; j <= endIdx; j++)
            {
                if (j == candidateLocalIdx) continue;

                // 高点对比：左/右两翼的 High 是否存在大于等于候选 High
                if (isPeak && klines[j].High >= candidateHigh)
                    isPeak = false;

                // 低点对比：左/右两翼的 Low 是否存在小于等于候选 Low
                if (isValley && klines[j].Low <= candidateLow)
                    isValley = false;

                if (!isPeak && !isValley)
                    break;
            }

            // 若确认为高点，Price 严格存入 candidateHigh
            if (isPeak)
            {
                hasPeak = true;
                peak = new PivotPoint
                {
                    Index = candidateGlobalIndex,
                    Time = TimeHelper.FromUnixTimeMilliseconds(klines[candidateLocalIdx].OpenTime),
                    TimestampMs = klines[candidateLocalIdx].OpenTime,
                    Price = candidateHigh, // 高点取 High
                    Type = PivotType.Peak,
                    IsFractalConfirmed = true
                };
            }

            // 若确认为低点，Price 严格存入 candidateLow
            if (isValley)
            {
                hasValley = true;
                valley = new PivotPoint
                {
                    Index = candidateGlobalIndex,
                    Time = TimeHelper.FromUnixTimeMilliseconds(klines[candidateLocalIdx].OpenTime),
                    TimestampMs = klines[candidateLocalIdx].OpenTime,
                    Price = candidateLow, // 低点取 Low
                    Type = PivotType.Valley,
                    IsFractalConfirmed = true
                };
            }

            return (hasPeak, hasValley, peak, valley);
        }

        #endregion

        #region 2. 批量全量扫描方法 (Batch Analysis Overloads)

        /// <summary>
        /// 经典双侧分形全量计算波峰与波谷 (高点严格取 High，低点严格取 Low)
        /// </summary>
        public static (List<PivotPoint> Peaks, List<PivotPoint> Valleys) CalculatePeaks(
            IReadOnlyList<RawKline> klines,
            int leftLen = 5,
            int rightLen = 5,
            int startGlobalIndex = 0)
        {
            var peaks = new List<PivotPoint>();
            var valleys = new List<PivotPoint>();

            if (klines == null || klines.Count <= leftLen + rightLen)
            {
                return (peaks, valleys);
            }

            int length = klines.Count;
            for (int i = leftLen; i < length - rightLen; i++)
            {
                bool isPeak = true;
                bool isValley = true;
                decimal currentHigh = klines[i].High;
                decimal currentLow = klines[i].Low;

                for (int j = i - leftLen; j <= i + rightLen; j++)
                {
                    if (j == i) continue;

                    // 高点判断取 High
                    if (isPeak && klines[j].High >= currentHigh)
                        isPeak = false;

                    // 低点判断取 Low
                    if (isValley && klines[j].Low <= currentLow)
                        isValley = false;

                    if (!isPeak && !isValley)
                        break;
                }

                if (isPeak)
                {
                    peaks.Add(new PivotPoint
                    {
                        Index = startGlobalIndex + i,
                        Time = TimeHelper.FromUnixTimeMilliseconds(klines[i].OpenTime),
                        TimestampMs = klines[i].OpenTime,
                        Price = currentHigh, // 高点取 High
                        Type = PivotType.Peak,
                        IsFractalConfirmed = true
                    });
                }

                if (isValley)
                {
                    valleys.Add(new PivotPoint
                    {
                        Index = startGlobalIndex + i,
                        Time = TimeHelper.FromUnixTimeMilliseconds(klines[i].OpenTime),
                        TimestampMs = klines[i].OpenTime,
                        Price = currentLow, // 低点取 Low
                        Type = PivotType.Valley,
                        IsFractalConfirmed = true
                    });
                }
            }

            return (peaks, valleys);
        }

        #endregion

        #region 3. 连续内存 ReadOnlySpan<decimal> 极速底层重载

        /// <summary>
        /// 极速版计算局部高低点 (零内存分配 + Span 连续内存访问，高点取 High，低点取 Low)
        /// </summary>
        public static void CalculatePeaksFast(
            ReadOnlySpan<decimal> highs,
            ReadOnlySpan<decimal> lows,
            List<int> peaksBuffer,
            List<int> valleysBuffer,
            int leftLen = 5,
            int rightLen = 5)
        {
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            int length = highs.Length;
            if (length == 0 || lows.Length != length || length <= leftLen + rightLen)
            {
                return;
            }

            for (int i = leftLen; i < length - rightLen; i++)
            {
                bool isPeak = true;
                bool isValley = true;
                decimal currentHigh = highs[i];
                decimal currentLow = lows[i];

                int startIdx = i - leftLen;
                int endIdx = i + rightLen;

                for (int j = startIdx; j <= endIdx; j++)
                {
                    if (j == i) continue;

                    // 高点取 High
                    if (isPeak && highs[j] >= currentHigh)
                        isPeak = false;

                    // 低点取 Low
                    if (isValley && lows[j] <= currentLow)
                        isValley = false;

                    if (!isPeak && !isValley)
                        break;
                }

                if (isPeak) peaksBuffer.Add(i);
                if (isValley) valleysBuffer.Add(i);
            }
        }

        #endregion

        #region 4. ZigZag 之字转向算法 (增量流式识别与全量批处理)

        /// <summary>
        /// ZigZag 之字转向全量计算波峰与波谷 (基于最小百分比反转回撤，高点严格取 High，低点严格取 Low)
        /// </summary>
        /// <param name="klines">K 线序列</param>
        /// <param name="deviationPct">最小反转百分比 (默认 1.0%)</param>
        /// <param name="depth">最小 K 线间隔深度 (默认 5)</param>
        /// <param name="startGlobalIndex">起始全局索引</param>
        public static (List<PivotPoint> Peaks, List<PivotPoint> Valleys) CalculateZigZagPeaks(
            IReadOnlyList<RawKline> klines,
            decimal deviationPct = 1.0m,
            int depth = 5,
            int startGlobalIndex = 0)
        {
            var peaks = new List<PivotPoint>();
            var valleys = new List<PivotPoint>();

            if (klines == null || klines.Count == 0)
            {
                return (peaks, valleys);
            }

            var tracker = new ZigZagTracker();
            for (int i = 0; i < klines.Count; i++)
            {
                var (hasPeak, hasValley, peak, valley) = tracker.ProcessKline(klines[i], startGlobalIndex + i, deviationPct, depth);
                if (hasPeak) peaks.Add(peak);
                if (hasValley) valleys.Add(valley);
            }

            return (peaks, valleys);
        }

        #endregion
    }

    /// <summary>
    /// 高性能增量式 ZigZag 极值追踪状态机 (O(1) 逐根 K 线流式运算，零内存分配)
    /// 核心机制：
    /// - 上升浪阶段：持续追踪最高 High，若创更高价则动态更新候选波峰；若自候选高点回撤超过 Deviation% 且 K 线数达到 Depth，则确立波峰并转向下跌浪。
    /// - 下跌浪阶段：持续追踪最低 Low，若创更低价则动态更新候选波谷；若自候选低点反弹超过 Deviation% 且 K 线数达到 Depth，则确立波谷并转向上升浪。
    /// </summary>
    public class ZigZagTracker
    {
        private int _direction = 0; // +1: 向上寻找波峰中; -1: 向下寻找波谷中; 0: 未初始化
        private PivotPoint _candidate;
        private int _barsSinceCandidate = 0;

        /// <summary>
        /// 当前确认方向 (+1: 上升浪; -1: 下跌浪)
        /// </summary>
        public int Direction => _direction;

        /// <summary>
        /// 当前正在追踪的候选极值点
        /// </summary>
        public PivotPoint CandidatePivot => _candidate;

        /// <summary>
        /// 重置状态机
        /// </summary>
        public void Reset()
        {
            _direction = 0;
            _candidate = default;
            _barsSinceCandidate = 0;
        }

        /// <summary>
        /// 处理单根新 K 线输入，判断是否触发了极值点确认
        /// </summary>
        /// <param name="kline">当前 K 线</param>
        /// <param name="globalIndex">当前 K 线的全局单调下标</param>
        /// <param name="deviationPct">最小反转幅度百分比 (如 1.0 代表 1.0%)</param>
        /// <param name="depth">最小 K 线跨度深度 (如 5 根)</param>
        /// <returns>是否有确认的高点或低点产生</returns>
        public (bool hasPeak, bool hasValley, PivotPoint peak, PivotPoint valley) ProcessKline(
            RawKline kline,
            int globalIndex,
            decimal deviationPct = 1.0m,
            int depth = 5)
        {
            PivotPoint peak = default;
            PivotPoint valley = default;
            bool hasPeak = false;
            bool hasValley = false;

            if (_direction == 0)
            {
                // 初始化第一个候选点 (默认以第一根 K 线的 High 作为起始波峰候选)
                _candidate = new PivotPoint
                {
                    Index = globalIndex,
                    Price = kline.High,
                    Time = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime),
                    TimestampMs = kline.OpenTime,
                    Type = PivotType.Peak,
                    IsFractalConfirmed = true
                };
                _direction = 1; // 初始假设向上搜寻波峰
                _barsSinceCandidate = 0;
                return (false, false, peak, valley);
            }

            _barsSinceCandidate++;

            if (_direction == 1) // 向上寻找波峰阶段
            {
                // 1. 若当前 K 线创出更高的高点 -> 动态向上迁移候选波峰
                if (kline.High >= _candidate.Price)
                {
                    _candidate = new PivotPoint
                    {
                        Index = globalIndex,
                        Price = kline.High,
                        Time = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime),
                        TimestampMs = kline.OpenTime,
                        Type = PivotType.Peak,
                        IsFractalConfirmed = true
                    };
                    _barsSinceCandidate = 0;
                }
                else
                {
                    // 2. 检查自候选高点的回撤幅度是否达到反转阈值 (Deviation %)
                    decimal pullbackPct = _candidate.Price > 0m ? (_candidate.Price - kline.Low) / _candidate.Price * 100m : 0m;
                    if (pullbackPct >= deviationPct && _barsSinceCandidate >= depth)
                    {
                        // 🌟 确立波峰 (Peak)!
                        hasPeak = true;
                        peak = _candidate;

                        // 转向：开始向下寻找波谷 (Valley)，以当前 K 线的 Low 初始化波谷候选
                        _direction = -1;
                        _candidate = new PivotPoint
                        {
                            Index = globalIndex,
                            Price = kline.Low,
                            Time = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime),
                            TimestampMs = kline.OpenTime,
                            Type = PivotType.Valley,
                            IsFractalConfirmed = true
                        };
                        _barsSinceCandidate = 0;
                    }
                }
            }
            else if (_direction == -1) // 向下寻找波谷阶段
            {
                // 1. 若当前 K 线创出更低的低点 -> 动态向下迁移候选波谷
                if (kline.Low <= _candidate.Price)
                {
                    _candidate = new PivotPoint
                    {
                        Index = globalIndex,
                        Price = kline.Low,
                        Time = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime),
                        TimestampMs = kline.OpenTime,
                        Type = PivotType.Valley,
                        IsFractalConfirmed = true
                    };
                    _barsSinceCandidate = 0;
                }
                else
                {
                    // 2. 检查自候选低点的反弹幅度是否达到反转阈值 (Deviation %)
                    decimal reboundPct = _candidate.Price > 0m ? (kline.High - _candidate.Price) / _candidate.Price * 100m : 0m;
                    if (reboundPct >= deviationPct && _barsSinceCandidate >= depth)
                    {
                        // 🌟 确立波谷 (Valley)!
                        hasValley = true;
                        valley = _candidate;

                        // 转向：开始向上寻找波峰 (Peak)，以当前 K 线的 High 初始化波峰候选
                        _direction = 1;
                        _candidate = new PivotPoint
                        {
                            Index = globalIndex,
                            Price = kline.High,
                            Time = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime),
                            TimestampMs = kline.OpenTime,
                            Type = PivotType.Peak,
                            IsFractalConfirmed = true
                        };
                        _barsSinceCandidate = 0;
                    }
                }
            }

            return (hasPeak, hasValley, peak, valley);
        }
    }
}
