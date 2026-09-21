using Common;
using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 18 种 K 线量化形态分类枚举
    /// </summary>
    public enum CandlestickPatternType
    {
        None = 0,

        // 一、 K线状态饱满影线很短 (body / total_range >= 0.70 且 upper_shadow/range <= 0.15 且 lower_shadow/range <= 0.15)
        [System.ComponentModel.Description("[1] 实体长 + 上涨 (大阳线 / 巨阳破位)")]
        FullBodyLongBullish = 1,

        [System.ComponentModel.Description("[2] 实体长 + 下跌 (大阴线 / 巨阴杀跌)")]
        FullBodyLongBearish = 2,

        [System.ComponentModel.Description("[3] 实体中 + 上涨 (中阳线 / 稳步推升)")]
        FullBodyMediumBullish = 3,

        [System.ComponentModel.Description("[4] 实体中 + 下跌 (中阴线 / 稳步阴跌)")]
        FullBodyMediumBearish = 4,

        [System.ComponentModel.Description("[5] 实体短 + 上涨 (微幅寸劲阳 / 窄幅破位阳)")]
        FullBodyShortBullish = 5,

        [System.ComponentModel.Description("[6] 实体短 + 下跌 (微幅寸劲阴 / 窄幅阴跌)")]
        FullBodyShortBearish = 6,

        // 二、 上影线很长 (upper_shadow / total_range >= 0.40，高位强供给/价格拒绝)
        [System.ComponentModel.Description("[7] 实体长 + 上涨 (巨幅放量滞涨阳 / 冲高遭遇沉重阻击)")]
        UpperShadowLongBullish = 7,

        [System.ComponentModel.Description("[8] 实体长 + 下跌 (冲高反杀断头阴 / 假突破全面崩盘)")]
        UpperShadowLongBearish = 8,

        [System.ComponentModel.Description("[9] 实体中 + 上涨 (冲高回落中阳 / 阻力探测线)")]
        UpperShadowMediumBullish = 9,

        [System.ComponentModel.Description("[10] 实体中 + 下跌 (常规冲高杀跌阴 / 射击之星中阴)")]
        UpperShadowMediumBearish = 10,

        [System.ComponentModel.Description("[11] 实体短 + 上涨 (倒锤子阳 / 墓碑倒锤阳)")]
        UpperShadowShortBullish = 11,

        [System.ComponentModel.Description("[12] 实体短 + 下跌 (标准流星线 Shooting Star / 墓碑十字阴)")]
        UpperShadowShortBearish = 12,

        // 三、 下影线很长 (lower_shadow / total_range >= 0.40，低位强需求/流动性吸收)
        [System.ComponentModel.Description("[13] 实体长 + 上涨 (深水反包巨阳 / 惊天 V 转)")]
        LowerShadowLongBullish = 13,

        [System.ComponentModel.Description("[14] 实体长 + 下跌 (宽幅杀跌抵抗阴 / 深水反弹未果阴)")]
        LowerShadowLongBearish = 14,

        [System.ComponentModel.Description("[15] 实体中 + 上涨 (探底回升中阳 / 稳固托底线)")]
        LowerShadowMediumBullish = 15,

        [System.ComponentModel.Description("[16] 实体中 + 下跌 (抵抗下跌中阴 / 减速探底阴)")]
        LowerShadowMediumBearish = 16,

        [System.ComponentModel.Description("[17] 实体短 + 上涨 (经典锤子阳 Hammer / 蜻蜓阳线)")]
        LowerShadowShortBullish = 17,

        [System.ComponentModel.Description("[18] 实体短 + 下跌 (吊颈线 Hanging Man / 蜻蜓阴线)")]
        LowerShadowShortBearish = 18
    }

    /// <summary>
    /// K 线结构大类
    /// </summary>
    public enum CandlestickCategory
    {
        None = 0,
        /// <summary>
        /// 一、 K线状态饱满影线很短
        /// </summary>
        FullBodyShortShadow = 1,
        /// <summary>
        /// 二、 上影线很长 (高位强供给/价格拒绝)
        /// </summary>
        LongUpperShadow = 2,
        /// <summary>
        /// 三、 下影线很长 (低位强需求/流动性吸收)
        /// </summary>
        LongLowerShadow = 3
    }

    /// <summary>
    /// 做单信号买卖方向
    /// </summary>
    public enum OrderSignalDirection
    {
        None = 0,
        /// <summary>
        /// 做多 (Buy / Long)
        /// </summary>
        Buy = 1,
        /// <summary>
        /// 做空 (Sell / Short)
        /// </summary>
        Sell = 2
    }

    /// <summary>
    /// K 线量化形态判定结果模型
    /// </summary>
    public class CandlestickPatternResult
    {
        public CandlestickPatternType PatternType { get; set; } = CandlestickPatternType.None;
        public int PatternId => (int)PatternType;
        public string PatternName { get; set; } = "未分类";
        public CandlestickCategory Category { get; set; } = CandlestickCategory.None;
        public string CategoryName { get; set; } = "未知类别";

        // 前置计算基础数值
        public decimal TotalRange { get; set; }
        public decimal Body { get; set; }
        public decimal UpperShadow { get; set; }
        public decimal LowerShadow { get; set; }
        public decimal BodyPct { get; set; }

        // 比率
        public decimal BodyRangeRatio { get; set; }
        public decimal UpperShadowRangeRatio { get; set; }
        public decimal LowerShadowRangeRatio { get; set; }

        public bool IsBullish { get; set; }
        public string SemanticLogic { get; set; } = "";

        // 反转做单属性
        public bool IsReversalSignal { get; set; }
        public OrderSignalDirection SuggestedDirection { get; set; } = OrderSignalDirection.None;

        public override string ToString() => $"[{PatternId}] {PatternName} ({CategoryName})";
    }

    /// <summary>
    /// 反转做单信号模型
    /// </summary>
    public class ReversalOrderSignal
    {
        public string SignalId { get; set; } = Guid.NewGuid().ToString("N");
        public int TrendId { get; set; }
        public ConsecutiveTrendType PriorTrendType { get; set; }
        public int ObservationCycleIndex { get; set; } = 1;

        public long TriggerTime { get; set; }
        public DateTime TriggerDateTime => DateTimeOffset.FromUnixTimeMilliseconds(TriggerTime).LocalDateTime;
        public decimal Price { get; set; }
        public decimal StopLossPrice { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public decimal HighPointPrice { get; set; }
        public decimal LowPointPrice { get; set; }
        public decimal PeakTroughPrice { get; set; }
        public decimal PullbackPct { get; set; }
        public bool IsTickStreamTriggered { get; set; } = true;
        /// <summary>
        /// 是否属于历史K线实体超出通道时的顺势做单 (连涨实体突破上轨->顺势低多，连跌实体跌破下轨->顺势高空)
        /// </summary>
        public bool IsBreakoutTrendFollowing { get; set; } = false;
        public OrderSignalDirection Direction { get; set; }
        public string DirectionText => IsBreakoutTrendFollowing
            ? (Direction == OrderSignalDirection.Buy ? "🟢 顺势低多 (Buy)" : (Direction == OrderSignalDirection.Sell ? "🔴 顺势高空 (Sell)" : "无"))
            : (Direction == OrderSignalDirection.Buy ? "🟢 做多 (Buy)" : (Direction == OrderSignalDirection.Sell ? "🔴 做空 (Sell)" : "无"));

        public CandlestickPatternResult Pattern { get; set; } = new();
        public int BigBarIndex { get; set; }
        public RawKline SmallKline { get; set; } = new();

        public long ObservationStartTime { get; set; }
        public long ObservationEndTime { get; set; }

        /// <summary>
        /// 触发该信号瞬间的原始逐笔成交 Tick 数据 (含 TradeId, Qty, QuoteQty, IsBuyerMaker 等)
        /// </summary>
        public RawTick? TriggerTick { get; set; }

        /// <summary>
        /// 触发 Tick 在当前观察期中的索引位置 (从 0 计数)
        /// </summary>
        public int TriggerTickIndex { get; set; } = -1;

        /// <summary>
        /// 触发时观察期累计接收的 Tick 总数
        /// </summary>
        public int CycleTotalTicks { get; set; } = 0;

        /// <summary>
        /// 极值点 (波峰/波谷) 发生的时间戳 (毫秒)
        /// </summary>
        public long PeakTroughTime { get; set; }

        /// <summary>
        /// 反应的通道高度线条描述 (例如 "50% 通道中线", "75% 通道高度线", "100% 通道上轨", "25% 通道高度线", "0% 通道下轨")
        /// </summary>
        public string ChannelLineReaction { get; set; } = "";

        /// <summary>
        /// 反应线条对应的通道基准价格
        /// </summary>
        public decimal ChannelLinePrice { get; set; } = 0m;

        public override string ToString()
        {
            string tickInfo = "";
            if (IsTickStreamTriggered && TriggerTick.HasValue)
            {
                var t = TriggerTick.Value;
                string side = t.IsBuyerMaker ? "主动卖出(Taker Sell)" : "主动买入(Taker Buy)";
                tickInfo = $" [Tick #{TriggerTickIndex + 1}/{CycleTotalTicks}: {t.Price:F2} USDT, 量:{t.Qty:F4}, 额:{t.QuoteQty:F2}, {side}, TradeId:{t.TradeId}, 极值:{PeakTroughPrice:F2} 回落/反弹:{PullbackPct:F2}%]";
            }
            else if (IsTickStreamTriggered)
            {
                tickInfo = $" [Tick流 极值:{PeakTroughPrice:F2} 回落/反弹:{PullbackPct:F2}%]";
            }
            return $"[信号 #{ObservationCycleIndex}] {DirectionText} @ {Price:F2}{tickInfo} (止损:{StopLossPrice:F2}, 止盈:{TakeProfitPrice:F2}) | 形态: [{Pattern.PatternId}] {Pattern.PatternName} ({TriggerDateTime:HH:mm:ss})";
        }
    }

    /// <summary>
    /// 三分钟观察期循环状态
    /// </summary>
    public class ReversalObservationCycle
    {
        public int CycleIndex { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public int TrendId { get; set; }
        public ConsecutiveTrendType PriorTrendType { get; set; }

        public bool IsCompleted { get; set; }
        public bool IsSignalTriggered { get; set; }

        public RawKline? ResultKline { get; set; }
        public CandlestickPatternResult? PatternResult { get; set; }
        public ReversalOrderSignal? Signal { get; set; }
    }
}
