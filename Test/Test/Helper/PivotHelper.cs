using System;
using System.Collections.Generic;

namespace Common.Helper
{
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
    }
}
