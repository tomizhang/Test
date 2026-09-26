using Common;
using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Test.PeriodTickPlayback.WinForms.Engine;
using Test.PeriodTickPlayback.WinForms.Models;

namespace Test.PeriodTickPlayback.WinForms.Helper
{
    /// <summary>
    /// 最近 300 根小周期 K 线高低点动态趋势线检测与管理结果模型
    /// </summary>
    public class RecentSubPeriodTrendLineResult
    {
        public IReadOnlyList<PeriodBucket> SubBuckets { get; set; } = Array.Empty<PeriodBucket>();
        public IReadOnlyList<RawKline> Klines { get; set; } = Array.Empty<RawKline>();
        public IReadOnlyList<PivotPoint> Peaks { get; set; } = Array.Empty<PivotPoint>();
        public IReadOnlyList<PivotPoint> Valleys { get; set; } = Array.Empty<PivotPoint>();
        public IReadOnlyList<TrendLine> ResistanceLines { get; set; } = Array.Empty<TrendLine>();
        public IReadOnlyList<TrendLine> SupportLines { get; set; } = Array.Empty<TrendLine>();
        public IReadOnlyList<TrendLine> SelectedLines { get; set; } = Array.Empty<TrendLine>();
        public IReadOnlyList<ChannelTrendLineRecord> ChannelRecords { get; set; } = Array.Empty<ChannelTrendLineRecord>();
        public DateTime RangeStartTime { get; set; }
        public DateTime RangeEndTime { get; set; }
        public TimeSpan SubPeriodSpan { get; set; }
        public bool HasLines => SelectedLines.Count > 0;
    }

    /// <summary>
    /// 每个确立通道独立关联的 300 根小周期 K 线高低点与动态趋势线持久档案
    /// </summary>
    public class ChannelTrendLineRecord
    {
        public int ChannelId { get; set; }
        public int ChannelIndex { get; set; }
        public MacroConsecutiveTrendType Type { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public int ConfirmedBarIndex { get; set; }
        public DateTime ConfirmedTime { get; set; }
        public IReadOnlyList<PivotPoint> Peaks { get; set; } = Array.Empty<PivotPoint>();
        public IReadOnlyList<PivotPoint> Valleys { get; set; } = Array.Empty<PivotPoint>();
        public IReadOnlyList<TrendLine> ResistanceLines { get; set; } = Array.Empty<TrendLine>();
        public IReadOnlyList<TrendLine> SupportLines { get; set; } = Array.Empty<TrendLine>();
        public IReadOnlyList<TrendLine> SelectedLines { get; set; } = Array.Empty<TrendLine>();
        public MacroConsecutiveTrendItem TrendItem { get; set; } = null!;
    }

    /// <summary>
    /// 最近 300 根小周期 K 线极值高低点与动态趋势线生成引擎
    /// 核心功能：
    /// 1. 从当前回放推进点向前截取最近 300 根小周期 K 线 (如 1m 或当前选中的微观子周期)；
    /// 2. 使用双侧分形极值算法精确识别波峰 (Peaks/高点) 与波谷 (Valleys/低点)；
    /// 3. 基于高低点拟合前向延伸的高品质阻力趋势线与支撑趋势线；
    /// 4. 具备严格的内穿校验、斜率过滤与活跃度优选，输出权威非重叠的动态趋势线集合。
    /// </summary>
    public static class RecentSubPeriodTrendLineDetector
    {
        /// <summary>
        /// 从所有已加载的大周期桶及当前播放光标中提取最近 N 根小周期 K 线，并生成高低点与动态趋势线
        /// </summary>
        public static RecentSubPeriodTrendLineResult Detect(
            IReadOnlyList<PeriodBucket>? allBuckets,
            int currentBucketIndex,
            int currentTickIndex,
            TimeSpan subPeriodSpan,
            int maxSmallBars = 300,
            int leftLen = 3,
            int rightLen = 3,
            int maxSpan = 120)
        {
            var result = new RecentSubPeriodTrendLineResult { SubPeriodSpan = subPeriodSpan };
            if (allBuckets == null || allBuckets.Count == 0 || currentBucketIndex < 0)
            {
                return result;
            }

            int curBIdx = Math.Min(currentBucketIndex, allBuckets.Count - 1);
            if (subPeriodSpan.TotalSeconds <= 0)
            {
                subPeriodSpan = TimeSpan.FromMinutes(1);
                result.SubPeriodSpan = subPeriodSpan;
            }

            // 1. 确定当前回放推进点时间
            DateTime curPlaybackTime = allBuckets[curBIdx].EndTime;
            var curBucketTicks = allBuckets[curBIdx].Ticks;
            if (curBucketTicks != null && curBucketTicks.Count > 0 && currentTickIndex > 0)
            {
                int safeTickIdx = Math.Min(currentTickIndex - 1, curBucketTicks.Count - 1);
                curPlaybackTime = DateTimeOffset.FromUnixTimeMilliseconds(curBucketTicks[safeTickIdx].Time).UtcDateTime;
            }

            // 2. 向前追溯足够的周期桶以覆盖 maxSmallBars 根小周期 K 线
            // 目标跨度 = maxSmallBars * subPeriodSpan
            long targetTicksNeeded = (long)(subPeriodSpan.Ticks * maxSmallBars * 1.15);
            DateTime targetStartTime = curPlaybackTime.AddTicks(-targetTicksNeeded);

            int startBIdx = curBIdx;
            while (startBIdx > 0 && allBuckets[startBIdx].StartTime > targetStartTime)
            {
                startBIdx--;
            }

            // 3. 收集该时间窗口内的所有已发生 Tick
            var collectedTicks = new List<RawTick>(100000);
            for (int b = startBIdx; b <= curBIdx; b++)
            {
                var bucket = allBuckets[b];
                if (bucket.Ticks == null || bucket.Ticks.Count == 0) continue;

                if (b < curBIdx)
                {
                    collectedTicks.AddRange(bucket.Ticks);
                }
                else
                {
                    // 当前正在播放的桶：仅截取已派发的 Tick
                    int count = Math.Clamp(currentTickIndex, 0, bucket.Ticks.Count);
                    for (int t = 0; t < count; t++)
                    {
                        collectedTicks.Add(bucket.Ticks[t]);
                    }
                }
            }

            if (collectedTicks.Count == 0)
            {
                return result;
            }

            return DetectFromTicks(collectedTicks, subPeriodSpan, maxSmallBars, leftLen, rightLen, maxSpan);
        }

        /// <summary>
        /// 从给定的 Tick 集合中切片为小周期 K 线，提取最近 300 根的高低点并生成动态趋势线
        /// </summary>
        public static RecentSubPeriodTrendLineResult DetectFromTicks(
            IReadOnlyList<RawTick> ticks,
            TimeSpan subPeriodSpan,
            int maxSmallBars = 300,
            int leftLen = 3,
            int rightLen = 3,
            int maxSpan = 120)
        {
            var result = new RecentSubPeriodTrendLineResult { SubPeriodSpan = subPeriodSpan };
            if (ticks == null || ticks.Count == 0)
            {
                return result;
            }

            if (subPeriodSpan.TotalSeconds <= 0)
            {
                subPeriodSpan = TimeSpan.FromMinutes(1);
                result.SubPeriodSpan = subPeriodSpan;
            }

            // 1. 切片聚合为小周期桶
            var tickList = ticks as List<RawTick> ?? ticks.ToList();
            var subBuckets = PeriodBucketLoader.SliceTicksIntoBuckets(tickList, subPeriodSpan);
            if (subBuckets == null || subBuckets.Count == 0)
            {
                return result;
            }

            // 2. 截取最近 maxSmallBars 根 (默认 300 根)
            if (subBuckets.Count > maxSmallBars)
            {
                subBuckets = subBuckets.Skip(subBuckets.Count - maxSmallBars).ToList();
            }

            result.SubBuckets = subBuckets;
            result.RangeStartTime = subBuckets[0].StartTime;
            result.RangeEndTime = subBuckets[^1].EndTime;

            // 3. 构建 RawKline 列表
            var klines = new List<RawKline>(subBuckets.Count);
            for (int i = 0; i < subBuckets.Count; i++)
            {
                var b = subBuckets[i];
                var fk = b.FinalKline;
                decimal op = fk?.Open ?? (b.Ticks.Count > 0 ? b.Ticks[0].Price : 0m);
                decimal cl = fk?.Close ?? (b.Ticks.Count > 0 ? b.Ticks[^1].Price : op);
                decimal hi = fk?.High ?? (b.Ticks.Count > 0 ? b.Ticks.Max(t => t.Price) : Math.Max(op, cl));
                decimal lo = fk?.Low ?? (b.Ticks.Count > 0 ? b.Ticks.Min(t => t.Price) : Math.Min(op, cl));
                decimal vol = fk?.Volume ?? (b.Ticks.Count > 0 ? b.Ticks.Sum(t => t.Qty) : 0m);

                long openMs = ((DateTimeOffset)b.StartTime).ToUnixTimeMilliseconds();
                long closeMs = ((DateTimeOffset)b.EndTime).ToUnixTimeMilliseconds();

                klines.Add(new RawKline
                {
                    OpenTime = openMs,
                    CloseTime = closeMs,
                    Open = op,
                    High = hi,
                    Low = lo,
                    Close = cl,
                    Volume = vol,
                    TradeCount = b.Ticks?.Count ?? 0
                });
            }

            result.Klines = klines;

            // 4. 双侧分形高低点计算 (满足门槛：需满足 leftLen + rightLen + 1 根)
            if (klines.Count < leftLen + rightLen + 1)
            {
                return result;
            }

            var (peaks, valleys) = PivotHelper.CalculatePeaks(klines, leftLen, rightLen, startGlobalIndex: 0);
            result.Peaks = peaks;
            result.Valleys = valleys;

            if (peaks.Count < 2 && valleys.Count < 2)
            {
                return result;
            }

            // 5. 趋势线全量配对生成 (严格校验无内部 K 线穿透与斜率合理性)
            var (resLines, supLines) = TrendLineHelper.GenerateTrendLines(
                klines,
                peaks,
                valleys,
                maxSpan: maxSpan,
                allowInternalPenetration: false,
                currentGlobalIndex: klines.Count - 1);

            result.ResistanceLines = resLines;
            result.SupportLines = supLines;

            // 6. 权威趋势线精选过滤 (挑选最具代表性、跨度足够且未被击穿或最新活跃的阻力线与支撑线各 2~3 条)
            var selected = new List<TrendLine>();

            // 6.1 精选阻力线 (优先取未被碰撞破位的活跃线，按跨度降序，最多取 3 条)
            var activeRes = resLines.Where(l => l.CollidedKlineIndex == -1 && l.LineX1X2 >= 5)
                                    .OrderByDescending(l => l.LineX1X2)
                                    .Take(3)
                                    .ToList();
            if (activeRes.Count < 2)
            {
                // 若活跃线不足，补充近期未过深击穿的线
                var recentTestedRes = resLines.Where(l => l.CollidedKlineIndex >= klines.Count - 25 && l.LineX1X2 >= 5)
                                              .OrderByDescending(l => l.LineX1X2)
                                              .Take(2 - activeRes.Count);
                activeRes.AddRange(recentTestedRes);
            }
            selected.AddRange(activeRes);

            // 6.2 精选支撑线 (优先取未被碰撞破位的活跃线，按跨度降序，最多取 3 条)
            var activeSup = supLines.Where(l => l.CollidedKlineIndex == -1 && l.LineX1X2 >= 5)
                                    .OrderByDescending(l => l.LineX1X2)
                                    .Take(3)
                                    .ToList();
            if (activeSup.Count < 2)
            {
                var recentTestedSup = supLines.Where(l => l.CollidedKlineIndex >= klines.Count - 25 && l.LineX1X2 >= 5)
                                              .OrderByDescending(l => l.LineX1X2)
                                              .Take(2 - activeSup.Count);
                activeSup.AddRange(recentTestedSup);
            }
            selected.AddRange(activeSup);

            result.SelectedLines = selected;
            return result;
        }

        /// <summary>
        /// 专为指定的通道 (MacroConsecutiveTrendItem) 提取最近 300 根小周期 K 线的高低极值点与动态趋势线
        /// 当通道在图表中确立时触发生成，所生成的趋势线打上通道归属标记并在回放中持久保留
        /// </summary>
        public static ChannelTrendLineRecord? GenerateForChannel(
            MacroConsecutiveTrendItem tr,
            int channelIndex,
            IReadOnlyList<PeriodBucket>? allBuckets,
            int currentBucketIndex,
            int currentTickIndex,
            TimeSpan subPeriodSpan,
            int maxSmallBars = 300)
        {
            if (tr == null || allBuckets == null || allBuckets.Count == 0)
            {
                return null;
            }

            if (subPeriodSpan.TotalSeconds <= 0)
            {
                subPeriodSpan = TimeSpan.FromMinutes(1);
            }

            int targetBIdx = Math.Min(tr.EndIndex, Math.Min(currentBucketIndex, allBuckets.Count - 1));
            if (targetBIdx < 0) return null;

            // 1. 确定该通道采样截止时间
            var targetBucket = allBuckets[targetBIdx];
            DateTime targetEndTime = targetBucket.EndTime;
            if (targetBIdx == currentBucketIndex && currentTickIndex > 0 && targetBucket.Ticks != null && targetBucket.Ticks.Count > 0)
            {
                int safeTickIdx = Math.Min(currentTickIndex - 1, targetBucket.Ticks.Count - 1);
                targetEndTime = DateTimeOffset.FromUnixTimeMilliseconds(targetBucket.Ticks[safeTickIdx].Time).UtcDateTime;
            }

            // 2. 向前追溯足够的周期桶以覆盖 maxSmallBars 根小周期 K 线
            long targetTicksNeeded = (long)(subPeriodSpan.Ticks * maxSmallBars * 1.15);
            DateTime targetStartTime = targetEndTime.AddTicks(-targetTicksNeeded);

            int startBIdx = targetBIdx;
            while (startBIdx > 0 && allBuckets[startBIdx].StartTime > targetStartTime)
            {
                startBIdx--;
            }

            // 3. 收集该时间窗口内的所有已发生 Tick
            var collectedTicks = new List<RawTick>(100000);
            for (int b = startBIdx; b <= targetBIdx; b++)
            {
                var bucket = allBuckets[b];
                if (bucket.Ticks == null || bucket.Ticks.Count == 0) continue;

                if (b < targetBIdx || b < currentBucketIndex)
                {
                    collectedTicks.AddRange(bucket.Ticks);
                }
                else
                {
                    // 当前正在播放的桶：仅截取已派发的 Tick
                    int count = Math.Clamp(currentTickIndex, 0, bucket.Ticks.Count);
                    for (int t = 0; t < count; t++)
                    {
                        collectedTicks.Add(bucket.Ticks[t]);
                    }
                }
            }

            if (collectedTicks.Count == 0)
            {
                return null;
            }

            var detect = DetectFromTicks(collectedTicks, subPeriodSpan, maxSmallBars);

            // 4. 为该通道精选的趋势线打上 ChannelId 归属标记与通道信息
            var taggedLines = new List<TrendLine>(detect.SelectedLines.Count);
            foreach (var line in detect.SelectedLines)
            {
                var copy = line;
                copy.ChannelId = tr.Id;
                copy.IsInChannel = true;
                taggedLines.Add(copy);
            }

            DateTime confTime = targetBucket.EndTime;
            int cIdx = tr.ConfirmedBarIndex;
            if (cIdx >= 0 && cIdx < allBuckets.Count)
            {
                confTime = allBuckets[cIdx].EndTime;
            }

            return new ChannelTrendLineRecord
            {
                ChannelId = tr.Id,
                ChannelIndex = channelIndex,
                Type = tr.Type,
                StartIndex = tr.StartIndex,
                EndIndex = tr.EndIndex,
                ConfirmedBarIndex = tr.ConfirmedBarIndex,
                ConfirmedTime = confTime,
                Peaks = detect.Peaks,
                Valleys = detect.Valleys,
                ResistanceLines = detect.ResistanceLines,
                SupportLines = detect.SupportLines,
                SelectedLines = taggedLines,
                TrendItem = tr
            };
        }
    }
}
