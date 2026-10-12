using Common;
using System;

namespace Test.PeriodTickPlayback.WinForms.Models
{
    /// <summary>
    /// 连续上涨反转做空状态机运行状态
    /// </summary>
    public enum ConsecutiveReversalState
    {
        /// <summary>
        /// 未激活 / 空闲态
        /// </summary>
        None = 0,

        /// <summary>
        /// 连续上涨形态确立，正在聚合第 1 个小周期，等待小周期结束确认是否收阴反转
        /// </summary>
        PendingSubPeriod1 = 1,

        /// <summary>
        /// 第 1 个小周期收阴/收阳确认反转，正式进入观察期！实时监测是否处于高点后回落(做空)或处于低点后回升(做多)
        /// </summary>
        ObservingSubPeriod2 = 2,

        /// <summary>
        /// 观察期进行中别名 (支持多轮顺延观察期)
        /// </summary>
        Observing = 2,

        /// <summary>
        /// 观察期内冲高回落或探底回升，成功触发交易信号 (严格永久铆钉于触发实时 Tick)
        /// </summary>
        Triggered = 3,

        /// <summary>
        /// 策略完整执行完成，信号已锁定定型
        /// </summary>
        Completed = 4,

        /// <summary>
        /// 形态失效注销 (第 1 个小周期未反转，或多轮观察期结束仍未出现明显信号，或价格破位)
        /// </summary>
        Expired = 5
    }

    /// <summary>
    /// 反转小周期轻量聚合 K 线模型 (默认 3 分钟，参数可调)
    /// </summary>
    public class SubPeriodBar
    {
        public long StartTimeMs { get; set; }
        public long EndTimeMs { get; set; }
        public DateTime StartTime => DateTimeOffset.FromUnixTimeMilliseconds(StartTimeMs).UtcDateTime;
        public DateTime EndTime => DateTimeOffset.FromUnixTimeMilliseconds(EndTimeMs).UtcDateTime;

        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public int TickCount { get; set; }

        public bool HasTicks => TickCount > 0;

        /// <summary>
        /// 是否收阴线 (收盘价低于开盘价，多头衰竭反转确认)
        /// </summary>
        public bool IsBearish => HasTicks && Close < Open;

        /// <summary>
        /// 是否收阳线 (收盘价高于开盘价)
        /// </summary>
        public bool IsBullish => HasTicks && Close > Open;

        public decimal ChangePrice => HasTicks ? Close - Open : 0m;
        public decimal ChangePct => (HasTicks && Open > 0m) ? ((Close - Open) / Open) * 100m : 0m;

        public SubPeriodBar()
        {
        }

        public SubPeriodBar(long startMs, long endMs)
        {
            StartTimeMs = startMs;
            EndTimeMs = endMs;
        }

        public void UpdateWithTick(RawTick tick)
        {
            if (TickCount == 0)
            {
                Open = tick.Price;
                High = tick.Price;
                Low = tick.Price;
                Close = tick.Price;
                Volume = tick.Qty;
                TickCount = 1;
            }
            else
            {
                if (tick.Price > High) High = tick.Price;
                if (tick.Price < Low) Low = tick.Price;
                Close = tick.Price;
                Volume += tick.Qty;
                TickCount++;
            }
        }

        public void Reset()
        {
            StartTimeMs = 0;
            EndTimeMs = 0;
            Open = 0;
            High = 0;
            Low = 0;
            Close = 0;
            Volume = 0;
            TickCount = 0;
        }
    }

    /// <summary>
    /// 连续形态反转策略交易方向
    /// </summary>
    public enum ConsecutiveReversalDirection
    {
        /// <summary>
        /// 连阳反转做空 (连续上涨N根 -> 首个小周期收阴反转 -> 观察期二次冲高回落 -> 触发做空 ▼)
        /// </summary>
        Short = 0,

        /// <summary>
        /// 连阴反转做多 (连续下跌N根 -> 首个小周期收阳反转 -> 观察期二次探底回升 -> 触发做多 ▲)
        /// </summary>
        Long = 1
    }

    /// <summary>
    /// 连续形态反转交易策略观察期状态机运行上下文 (支持做多与做空双向对称逻辑及多轮顺延观察期)
    /// </summary>
    public class ConsecutiveReversalContext
    {
        public int Id { get; set; }

        /// <summary>
        /// 策略交易方向 (做空 / 做多)
        /// </summary>
        public ConsecutiveReversalDirection Direction { get; set; } = ConsecutiveReversalDirection.Short;

        public bool IsShort => Direction == ConsecutiveReversalDirection.Short;
        public bool IsLong => Direction == ConsecutiveReversalDirection.Long;

        /// <summary>
        /// 连续形态确立的大周期最后一根 K 线索引
        /// </summary>
        public int MacroBarIndex { get; set; }

        /// <summary>
        /// 大周期连续形态结束毫秒时间戳 T0 (小周期起点)
        /// </summary>
        public long MacroEndMs { get; set; }

        /// <summary>
        /// 连续 K 线根数 (例如 4 根)
        /// </summary>
        public int ConsecutiveBarCount { get; set; }

        /// <summary>
        /// 向后兼容属性：连阳根数
        /// </summary>
        public int ConsecutiveBullishCount { get => ConsecutiveBarCount; set => ConsecutiveBarCount = value; }

        /// <summary>
        /// 连续形态基准价格：做空为区间最高价 Phigh，做多为区间最低价 Plow
        /// </summary>
        public decimal BenchmarkPrice { get; set; }

        /// <summary>
        /// 向后兼容属性：前高价格
        /// </summary>
        public decimal ConsecutiveHighPrice { get => BenchmarkPrice; set => BenchmarkPrice = value; }

        /// <summary>
        /// 向后兼容属性：前低价格
        /// </summary>
        public decimal ConsecutiveLowPrice { get => BenchmarkPrice; set => BenchmarkPrice = value; }

        /// <summary>
        /// 小周期跨度毫秒数 (SubPeriodMinutes * 60 * 1000)
        /// </summary>
        public long SubPeriodMs { get; set; }

        /// <summary>
        /// 当前状态机生命周期状态
        /// </summary>
        public ConsecutiveReversalState State { get; set; } = ConsecutiveReversalState.None;

        /// <summary>
        /// 当前观察期轮数 (1-based，第1轮观察期即为大周期结束后的第2个小周期)
        /// </summary>
        public int CurrentObservationPeriodIndex { get; set; } = 1;

        /// <summary>
        /// 当前观察期的起始时间戳
        /// </summary>
        public long CurrentObservationStartMs { get; set; }

        /// <summary>
        /// 当前观察期的结束时间戳
        /// </summary>
        public long CurrentObservationEndMs { get; set; }

        /// <summary>
        /// 当前观察期聚合 K 线
        /// </summary>
        public SubPeriodBar CurrentObservationBar { get; set; } = new();

        /// <summary>
        /// 当前观察期内价格是否曾到达基准高点/低点区域 (处于高点 / 处于低点)
        /// </summary>
        public bool HasReachedBenchmark { get; set; } = false;

        /// <summary>
        /// 当前观察期触及高点区域后录得的局部最高峰值 (用于判断冲高后回落)
        /// </summary>
        public decimal ObservedPeakPrice { get; set; } = decimal.MinValue;

        /// <summary>
        /// 当前观察期触及低点区域后录得的局部最低谷值 (用于判断探底后回升)
        /// </summary>
        public decimal ObservedTroughPrice { get; set; } = decimal.MaxValue;

        /// <summary>
        /// 极值触发 Tick
        /// </summary>
        public RawTick? PeakOrTroughTick { get; set; }

        /// <summary>
        /// 第 1 个小周期 [T0, T0 + SubPeriodMs) (反转确认期)
        /// </summary>
        public SubPeriodBar SubPeriod1 { get; set; } = new();

        /// <summary>
        /// 向后兼容属性：第 2 个小周期 (即第 1 轮观察期)
        /// </summary>
        public SubPeriodBar SubPeriod2
        {
            get => CurrentObservationBar;
            set => CurrentObservationBar = value;
        }

        /// <summary>
        /// 触发生成的交易信号记录 (打点于真实 Tick)
        /// </summary>
        public ChannelSignalItem? GeneratedSignal { get; set; }
    }
}
