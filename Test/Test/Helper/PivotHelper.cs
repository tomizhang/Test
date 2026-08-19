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
        /// 波峰 / 局部高点 (Peak)
        /// </summary>
        Peak = 1,

        /// <summary>
        /// 波谷 / 局部低点 (Valley)
        /// </summary>
        Valley = 2
    }

    /// <summary>
    /// 统一极值点 (波峰/波谷) 数据结构 (原生 decimal 强类型，包含 UTC+0 时间与 Unix 毫秒时间戳)
    /// </summary>
    public struct PivotPoint
    {
        /// <summary>
        /// 在 K 线历史序列中的索引下标
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
        /// 极值价格 (采用 decimal 精度，波峰对应最高价 High，波谷对应最低价 Low)
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 极值类型
        /// </summary>
        public PivotType Type { get; set; }

        /// <summary>
        /// 是否为波峰
        /// </summary>
        public bool IsPeak => Type == PivotType.Peak;

        /// <summary>
        /// 是否为波谷
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
            string symbol = IsPeak ? "▲波峰" : "▼波谷";
            return $"[{FormattedTime} ({TimestampMs}ms)] {symbol} #{Index} 价格:{Price:F2} 分形确认:{IsFractalConfirmed}";
        }
    }

    /// <summary>
    /// 高性能局部高低点 (波峰/波谷) 计算引擎
    /// 适配项目标准 RawKline 与 Span 连续内存视图，全面采用 decimal 高精度
    /// </summary>
    public static class PivotHelper
    {
        #region 1. 适配 IReadOnlyList<RawKline> 策略接口的高层重载

        /// <summary>
        /// 经典双侧分形计算波峰与波谷 (直接接收 IReadOnlyList&lt;RawKline&gt;)
        /// </summary>
        /// <param name="klines">K线历史数据序列</param>
        /// <param name="leftLen">左侧需低于/高于当前极值的 K 线根数</param>
        /// <param name="rightLen">右侧需低于/高于当前极值的 K 线根数</param>
        /// <returns>返回识别出的波峰和波谷点列表</returns>
        public static (List<PivotPoint> Peaks, List<PivotPoint> Valleys) CalculatePeaks(
            IReadOnlyList<RawKline> klines,
            int leftLen = 5,
            int rightLen = 5)
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

                    if (isPeak && klines[j].High >= currentHigh)
                        isPeak = false;

                    if (isValley && klines[j].Low <= currentLow)
                        isValley = false;

                    if (!isPeak && !isValley)
                        break;
                }

                if (isPeak)
                {
                    peaks.Add(new PivotPoint
                    {
                        Index = i,
                        Time = TimeHelper.FromUnixTimeMilliseconds(klines[i].OpenTime),
                        TimestampMs = klines[i].OpenTime,
                        Price = currentHigh,
                        Type = PivotType.Peak,
                        IsFractalConfirmed = true
                    });
                }

                if (isValley)
                {
                    valleys.Add(new PivotPoint
                    {
                        Index = i,
                        Time = TimeHelper.FromUnixTimeMilliseconds(klines[i].OpenTime),
                        TimestampMs = klines[i].OpenTime,
                        Price = currentLow,
                        Type = PivotType.Valley,
                        IsFractalConfirmed = true
                    });
                }
            }

            return (peaks, valleys);
        }

        /// <summary>
        /// 高性能状态机计算波峰与波谷 (直接接收 IReadOnlyList&lt;RawKline&gt;，支持复用外部缓冲区拒绝 GC 分配)
        /// </summary>
        /// <param name="klines">K线历史数据序列</param>
        /// <param name="peaksBuffer">用于装载波峰结果的复用缓冲区</param>
        /// <param name="valleysBuffer">用于装载波谷结果的复用缓冲区</param>
        /// <param name="reversalBars">确认极值反转所需的右侧 K 线根数 (默认 3)</param>
        public static void CalculatePeaksFastReversal(
            IReadOnlyList<RawKline> klines,
            List<PivotPoint> peaksBuffer,
            List<PivotPoint> valleysBuffer,
            int reversalBars = 3)
        {
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            if (klines == null || klines.Count < reversalBars + 1)
            {
                return;
            }

            int length = klines.Count;
            int state = 0; // 0: 寻找波峰中, 1: 寻找波谷中
            int candidateIdx = 0;
            decimal candidatePrice = klines[0].High;
            int barsSinceExtreme = 0;

            for (int i = 1; i < length; i++)
            {
                if (state == 0) // 寻找波峰
                {
                    decimal currentHigh = klines[i].High;
                    if (currentHigh >= candidatePrice)
                    {
                        candidateIdx = i;
                        candidatePrice = currentHigh;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            peaksBuffer.Add(new PivotPoint
                            {
                                Index = candidateIdx,
                                Time = TimeHelper.FromUnixTimeMilliseconds(klines[candidateIdx].OpenTime),
                                TimestampMs = klines[candidateIdx].OpenTime,
                                Price = candidatePrice,
                                Type = PivotType.Peak,
                                IsFractalConfirmed = true
                            });

                            state = 1;
                            candidateIdx = i;
                            candidatePrice = klines[i].Low;
                            barsSinceExtreme = 0;
                        }
                    }
                }
                else // 寻找波谷
                {
                    decimal currentLow = klines[i].Low;
                    if (currentLow <= candidatePrice)
                    {
                        candidateIdx = i;
                        candidatePrice = currentLow;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            valleysBuffer.Add(new PivotPoint
                            {
                                Index = candidateIdx,
                                Time = TimeHelper.FromUnixTimeMilliseconds(klines[candidateIdx].OpenTime),
                                TimestampMs = klines[candidateIdx].OpenTime,
                                Price = candidatePrice,
                                Type = PivotType.Valley,
                                IsFractalConfirmed = true
                            });

                            state = 0;
                            candidateIdx = i;
                            candidatePrice = klines[i].High;
                            barsSinceExtreme = 0;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 结合零滞后状态机与分形二次校验的高性能极值计算 (适配 IReadOnlyList&lt;RawKline&gt;)
        /// </summary>
        public static void CalculatePeaksCombinedFast(
            IReadOnlyList<RawKline> klines,
            List<PivotPoint> peaksBuffer,
            List<PivotPoint> valleysBuffer,
            int reversalBars = 2,
            int fractalArm = 2)
        {
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            if (klines == null || klines.Count < reversalBars + fractalArm + 1) return;

            int length = klines.Count;
            int state = 0; // 0: 寻找波峰, 1: 寻找波谷
            int candidateIdx = 0;
            decimal candidatePrice = klines[0].High;
            int barsSinceExtreme = 0;

            for (int i = 1; i < length; i++)
            {
                if (state == 0) // 寻找波峰中
                {
                    if (klines[i].High >= candidatePrice)
                    {
                        candidateIdx = i;
                        candidatePrice = klines[i].High;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            bool isFractal = ValidateFractalHigh(klines, candidateIdx, fractalArm);
                            peaksBuffer.Add(new PivotPoint
                            {
                                Index = candidateIdx,
                                Time = TimeHelper.FromUnixTimeMilliseconds(klines[candidateIdx].OpenTime),
                                TimestampMs = klines[candidateIdx].OpenTime,
                                Price = candidatePrice,
                                Type = PivotType.Peak,
                                IsFractalConfirmed = isFractal
                            });

                            state = 1;
                            candidateIdx = i;
                            candidatePrice = klines[i].Low;
                            barsSinceExtreme = 0;
                        }
                    }
                }
                else // 寻找波谷中
                {
                    if (klines[i].Low <= candidatePrice)
                    {
                        candidateIdx = i;
                        candidatePrice = klines[i].Low;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            bool isFractal = ValidateFractalLow(klines, candidateIdx, fractalArm);
                            valleysBuffer.Add(new PivotPoint
                            {
                                Index = candidateIdx,
                                Time = TimeHelper.FromUnixTimeMilliseconds(klines[candidateIdx].OpenTime),
                                TimestampMs = klines[candidateIdx].OpenTime,
                                Price = candidatePrice,
                                Type = PivotType.Valley,
                                IsFractalConfirmed = isFractal
                            });

                            state = 0;
                            candidateIdx = i;
                            candidatePrice = klines[i].High;
                            barsSinceExtreme = 0;
                        }
                    }
                }
            }
        }

        #endregion

        #region 2. 连续内存 ReadOnlySpan<decimal> 极速底层重载

        /// <summary>
        /// 极速版计算局部高低点 (零内存分配 + Span 连续内存访问，decimal 高精度)
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

                    if (isPeak && highs[j] >= currentHigh)
                        isPeak = false;

                    if (isValley && lows[j] <= currentLow)
                        isValley = false;

                    if (!isPeak && !isValley)
                        break;
                }

                if (isPeak) peaksBuffer.Add(i);
                if (isValley) valleysBuffer.Add(i);
            }
        }

        /// <summary>
        /// 基于 ReadOnlySpan 的状态机极值计算 (decimal 高精度)
        /// </summary>
        public static void CalculatePeaksFastReversal(
            ReadOnlySpan<decimal> highs,
            ReadOnlySpan<decimal> lows,
            List<int> peaksBuffer,
            List<int> valleysBuffer,
            int reversalBars = 3)
        {
            peaksBuffer.Clear();
            valleysBuffer.Clear();

            int length = highs.Length;
            if (length < reversalBars + 1 || lows.Length < length)
            {
                return;
            }

            int state = 0;
            int currentCandidateIdx = 0;
            decimal currentCandidatePrice = highs[0];
            int barsSinceExtreme = 0;

            for (int i = 1; i < length; i++)
            {
                if (state == 0)
                {
                    decimal currentHigh = highs[i];
                    if (currentHigh >= currentCandidatePrice)
                    {
                        currentCandidateIdx = i;
                        currentCandidatePrice = currentHigh;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            peaksBuffer.Add(currentCandidateIdx);

                            state = 1;
                            currentCandidateIdx = i;
                            currentCandidatePrice = lows[i];
                            barsSinceExtreme = 0;
                        }
                    }
                }
                else
                {
                    decimal currentLow = lows[i];
                    if (currentLow <= currentCandidatePrice)
                    {
                        currentCandidateIdx = i;
                        currentCandidatePrice = currentLow;
                        barsSinceExtreme = 0;
                    }
                    else
                    {
                        barsSinceExtreme++;
                        if (barsSinceExtreme >= reversalBars)
                        {
                            valleysBuffer.Add(currentCandidateIdx);

                            state = 0;
                            currentCandidateIdx = i;
                            currentCandidatePrice = highs[i];
                            barsSinceExtreme = 0;
                        }
                    }
                }
            }
        }

        #endregion

        #region 内部私有分形辅助校验

        private static bool ValidateFractalHigh(IReadOnlyList<RawKline> klines, int centerIdx, int arm)
        {
            if (centerIdx - arm < 0 || centerIdx + arm >= klines.Count) return false;

            decimal target = klines[centerIdx].High;
            for (int j = centerIdx - arm; j <= centerIdx + arm; j++)
            {
                if (j == centerIdx) continue;
                if (klines[j].High >= target) return false;
            }
            return true;
        }

        private static bool ValidateFractalLow(IReadOnlyList<RawKline> klines, int centerIdx, int arm)
        {
            if (centerIdx - arm < 0 || centerIdx + arm >= klines.Count) return false;

            decimal target = klines[centerIdx].Low;
            for (int j = centerIdx - arm; j <= centerIdx + arm; j++)
            {
                if (j == centerIdx) continue;
                if (klines[j].Low <= target) return false;
            }
            return true;
        }

        #endregion
    }
}
