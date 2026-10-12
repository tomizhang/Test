using Common;
using System;
using System.Collections.Generic;

namespace Test.PeriodTickPlayback.WinForms.Models
{
    /// <summary>
    /// 角度趋势通道实时交易信号类型 (做空 / 做多)
    /// </summary>
    public enum ChannelSignalType
    {
        Short = -1, // 做空 (触及通道顶部且为局部高点)
        Long = 1    // 做多 (触及通道底部且为局部低点)
    }

    /// <summary>
    /// 6 秒微观聚合 K 线数据模型
    /// 用于通道观察期内实时对齐聚合、高低点对比基准
    /// </summary>
    public class SubBar6s
    {
        public long BucketMs { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public int TickCount { get; set; }

        public SubBar6s()
        {
        }

        public SubBar6s(RawTick firstTick, long bucketMs)
        {
            BucketMs = bucketMs;
            StartTime = DateTimeOffset.FromUnixTimeMilliseconds(bucketMs).UtcDateTime;
            EndTime = StartTime.AddSeconds(6);
            Open = firstTick.Price;
            High = firstTick.Price;
            Low = firstTick.Price;
            Close = firstTick.Price;
            Volume = firstTick.Qty;
            TickCount = 1;
        }

        public void UpdateWithTick(RawTick tick)
        {
            if (tick.Price > High) High = tick.Price;
            if (tick.Price < Low) Low = tick.Price;
            Close = tick.Price;
            Volume += tick.Qty;
            TickCount++;
        }
    }

    /// <summary>
    /// 通道顶底实时交易信号记录模型 (严格打点于触发信号的原始实时 Tick，零未来函数)
    /// </summary>
    public class ChannelSignalItem
    {
        public int Id { get; set; }
        public int ChannelId { get; set; }
        public ChannelSignalType Type { get; set; }

        /// <summary>
        /// 触发信号时的原始真实 Tick (包含毫秒时间、精确成交价、成交量等)
        /// </summary>
        public RawTick TriggerTick { get; set; }

        /// <summary>
        /// 触发信号时的真实价格 (严格取自 TriggerTick.Price，不可使用聚合后的高低点)
        /// </summary>
        public decimal TriggerPrice { get; set; }

        /// <summary>
        /// 触发毫秒时间戳 (与原始 Tick.Time 一致)
        /// </summary>
        public long TriggerTimeMs { get; set; }

        /// <summary>
        /// 初次触发信号的毫秒时间戳 (用于严格约束波段吸附精炼窗口)
        /// </summary>
        public long InitialTriggerTimeMs { get; set; }

        /// <summary>
        /// 触发时间 (UTC 时间，与周期桶对齐)
        /// </summary>
        public DateTime TriggerTime { get; set; }

        /// <summary>
        /// 触发时归属的大周期 K 线桶索引
        /// </summary>
        public int MacroBarIndex { get; set; }

        /// <summary>
        /// 触发时在当前大周期桶内的进度比例 [0.0, 1.0]
        /// </summary>
        public double MacroProgress { get; set; }

        /// <summary>
        /// 大周期图表精准浮点横坐标 X = MacroBarIndex + MacroProgress
        /// </summary>
        public double MacroX => MacroBarIndex + Math.Clamp(MacroProgress, 0.0, 0.999);

        /// <summary>
        /// 触发时在当前周期桶内的 Tick 原始索引
        /// </summary>
        public int TickIndexInBucket { get; set; }

        /// <summary>
        /// 触发瞬时通道上轨价格
        /// </summary>
        public decimal ChannelUpper { get; set; }

        /// <summary>
        /// 触发瞬时通道下轨价格
        /// </summary>
        public decimal ChannelLower { get; set; }

        /// <summary>
        /// 通道斜率 K
        /// </summary>
        public decimal ChannelSlope { get; set; }

        /// <summary>
        /// 信号触发附加说明 (如触顶做空、触底做多)
        /// </summary>
        public string Note { get; set; } = string.Empty;

        public bool IsShort => Type == ChannelSignalType.Short;
        public bool IsLong => Type == ChannelSignalType.Long;

        public override string ToString()
        {
            string dirStr = IsShort ? "【做空信号 ▼】" : "【做多信号 ▲】";
            return $"{dirStr} {TriggerTime:yyyy-MM-dd HH:mm:ss} 价格:{TriggerPrice:F2} (上轨:{ChannelUpper:F2}, 下轨:{ChannelLower:F2}) Tick#{TickIndexInBucket}";
        }
    }

    /// <summary>
    /// 通道观察期上下文运行状态机
    /// 包含历史 10,000 笔预聚合基准与实时 6 秒聚合流
    /// </summary>
    public class ChannelObservationContext
    {
        public MacroConsecutiveTrendItem Channel { get; set; } = null!;
        public int ConfirmedMacroBarIndex { get; set; }
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 进入观察期时向前提取的 10,000 笔已发生 Tick 预聚合的 6 秒柱基准序列
        /// </summary>
        public List<SubBar6s> PreWarmed6sBars { get; set; } = new();

        /// <summary>
        /// 观察期内实时生成的 6 秒柱已定型列表
        /// </summary>
        public List<SubBar6s> Observation6sBars { get; set; } = new();

        /// <summary>
        /// 当前正在演变中的 6 秒柱
        /// </summary>
        public SubBar6s? Current6sBar { get; set; }

        /// <summary>
        /// 当前波段做空信号触发状态 (触顶触发后冷却，待回落至中轨重置)
        /// </summary>
        public bool ShortCooldown { get; set; } = false;

        /// <summary>
        /// 当前波段做多信号触发状态 (触底触发后冷却，待反弹至中轨重置)
        /// </summary>
        public bool LongCooldown { get; set; } = false;

        /// <summary>
        /// 最近一次产生信号的 6 秒桶时间戳 (杜绝同一 6 秒聚合柱内多次触发)
        /// </summary>
        public long LastSignalBucketMs { get; set; } = 0;

        /// <summary>
        /// 最近一次产生交易信号的 Tick 毫秒时间戳，用于确保信号之间至少有最小安全时间间隔
        /// </summary>
        public long LastSignalTimeMs { get; set; } = 0;

        /// <summary>
        /// 价格是否曾跌破通道下轨 (若跌破进入外部下方，则由下往上的反抽触碰下轨绝不做单，须重返中轨后方可重置)
        /// </summary>
        public bool PriceWasBelowLower { get; set; } = false;

        /// <summary>
        /// 价格是否曾冲破通道上轨 (若冲破进入外部上方，则由上往下的回踩触碰上轨绝不做单，须重返中轨后方可重置)
        /// </summary>
        public bool PriceWasAboveUpper { get; set; } = false;

        /// <summary>
        /// 前一笔实时 Tick 价格 (用于辅助判定即时动量变动方向)
        /// </summary>
        public decimal LastTickPrice { get; set; } = 0m;

        /// <summary>
        /// 过去所有已完成 6s 柱的最大 High 与最小 Low 动态缓存 (提高局部极值对比效率)
        /// </summary>
        public decimal RecentMaxHigh { get; set; } = decimal.MinValue;
        public decimal RecentMinLow { get; set; } = decimal.MaxValue;
    }
}
