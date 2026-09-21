using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 连续走势形态方向
    /// </summary>
    public enum ConsecutiveTrendType
    {
        /// <summary>
        /// 连续上涨形态 (连阳/多头暴冲)
        /// </summary>
        Bullish = 0,

        /// <summary>
        /// 连续下跌形态 (连阴/空头倾泻)
        /// </summary>
        Bearish = 1
    }

    /// <summary>
    /// 连续平行通道取值拟合模式
    /// </summary>
    public enum ConsecutiveChannelPriceMode
    {
        /// <summary>
        /// 收盘价拟合 (窄通道，排除上下影线噪声)
        /// </summary>
        Close = 0,

        /// <summary>
        /// 最高最低价拟合 (宽通道，包含全部极值外包络)
        /// </summary>
        HighLow = 1
    }

    /// <summary>
    /// 通道动态更新模式
    /// </summary>
    public enum ChannelUpdateMode
    {
        /// <summary>
        /// 滚动最新 N 根模式 (默认：使用最新的 MinBars 根 K 线重新拟合绘制新通道)
        /// </summary>
        Rolling = 0,

        /// <summary>
        /// 扩展全波段模式 (从启动点延伸至最新 K 线全量重新拟合)
        /// </summary>
        Expanding = 1
    }

    /// <summary>
    /// 历史通道快照记录 (记录更新前的旧通道几何参数与范围)
    /// </summary>
    public class ChannelSnapshot
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public decimal SlopeK { get; set; }
        public decimal UpperIntercept { get; set; }
        public decimal LowerIntercept { get; set; }
        public int TriggerBarIndex { get; set; }
        public decimal TriggerPrice { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 连续上涨 / 连续下跌动能波段模型
    /// </summary>
    public class ConsecutiveTrendItem
    {
        /// <summary>
        /// 唯一标识编号
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 趋势方向类型 (上涨 / 下跌)
        /// </summary>
        public ConsecutiveTrendType Type { get; set; }

        /// <summary>
        /// 连涨/连跌起始 K 线索引
        /// </summary>
        public int StartIndex { get; set; }

        /// <summary>
        /// 启动基准价格 (上涨取第一根开盘价或前一根收盘价，下跌同理)
        /// </summary>
        public decimal StartPrice { get; set; }

        /// <summary>
        /// 连涨/连跌当前或终止 K 线索引
        /// </summary>
        public int EndIndex { get; set; }

        /// <summary>
        /// 终止/当前收盘价
        /// </summary>
        public decimal EndPrice { get; set; }

        /// <summary>
        /// 连续 K 线数量 (EndIndex - StartIndex + 1)
        /// </summary>
        public int BarCount => EndIndex - StartIndex + 1;

        /// <summary>
        /// 累计涨跌幅百分比 (%)，上涨为正值，下跌为负值
        /// </summary>
        public decimal PriceChangePct { get; set; }

        /// <summary>
        /// 首次达到 K 线数门槛 (≥5) 且达到涨跌幅门槛 (≥2.5%) 时的确认 K 线索引
        /// </summary>
        public int ConfirmedBarIndex { get; set; }

        /// <summary>
        /// 该连续行情是否仍在当前最新帧继续连涨/连跌中
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 形态名称描述
        /// </summary>
        public string TypeName => Type == ConsecutiveTrendType.Bullish ? "连续上涨形态" : "连续下跌形态";

        /// <summary>
        /// 紧凑标签，例如: "🔥连涨 6根 (+3.2%)" 或 "❄️连跌 5根 (-2.8%)"
        /// </summary>
        public string ShortLabel
        {
            get
            {
                string icon = Type == ConsecutiveTrendType.Bullish ? "🔥连涨" : "❄️连跌";
                string sign = PriceChangePct >= 0m ? "+" : "";
                return $"{icon} {BarCount}根 ({sign}{PriceChangePct:F1}%)";
            }
        }

        #region 平行通道属性 (绿色 0.8f)

        /// <summary>
        /// 平行通道拟合取值模式 (Close 收盘价窄通道 / HighLow 极值宽通道)
        /// </summary>
        public ConsecutiveChannelPriceMode PriceMode { get; set; } = ConsecutiveChannelPriceMode.Close;

        /// <summary>
        /// 平行通道斜率 k
        /// </summary>
        public decimal SlopeK { get; set; }

        /// <summary>
        /// 上通道线截距 b_upper
        /// </summary>
        public decimal UpperIntercept { get; set; }

        /// <summary>
        /// 下通道线截距 b_lower
        /// </summary>
        public decimal LowerIntercept { get; set; }

        /// <summary>
        /// 中轨截距 (50% 高度)
        /// </summary>
        public decimal MidIntercept => (UpperIntercept + LowerIntercept) / 2m;

        /// <summary>
        /// 25% 通道高度分界线截距 (四等分第 1 分界线)
        /// </summary>
        public decimal Quarter25Intercept => LowerIntercept + ChannelHeight * 0.25m;

        /// <summary>
        /// 75% 通道高度分界线截距 (四等分第 3 分界线)
        /// </summary>
        public decimal Quarter75Intercept => LowerIntercept + ChannelHeight * 0.75m;

        /// <summary>
        /// 通道绝对高度
        /// </summary>
        public decimal ChannelHeight => UpperIntercept - LowerIntercept;

        /// <summary>
        /// 是否已成功拟合平行通道
        /// </summary>
        public bool HasChannel { get; set; }

        /// <summary>
        /// 计算给定 X 坐标处上轨价格 (100% 高度)
        /// </summary>
        public decimal GetUpperPrice(double x) => SlopeK * (decimal)x + UpperIntercept;

        /// <summary>
        /// 计算给定 X 坐标处下轨价格 (0% 高度)
        /// </summary>
        public decimal GetLowerPrice(double x) => SlopeK * (decimal)x + LowerIntercept;

        /// <summary>
        /// 计算给定 X 坐标处中轨价格 (50% 高度)
        /// </summary>
        public decimal GetMidPrice(double x) => SlopeK * (decimal)x + MidIntercept;

        /// <summary>
        /// 计算给定 X 坐标处 25% 通道高度价格
        /// </summary>
        public decimal GetQuarter25Price(double x) => SlopeK * (decimal)x + Quarter25Intercept;

        /// <summary>
        /// 计算给定 X 坐标处 75% 通道高度价格
        /// </summary>
        public decimal GetQuarter75Price(double x) => SlopeK * (decimal)x + Quarter75Intercept;

        /// <summary>
        /// 计算给定 X 坐标处指定比例高度的价格 (ratio: 0.0 ~ 1.0)
        /// </summary>
        public decimal GetPriceAtRatio(double x, decimal ratio) => SlopeK * (decimal)x + LowerIntercept + ChannelHeight * ratio;

        /// <summary>
        /// 当前活跃通道在图表上的起始 K 线索引 (更新后为新通道起点，未更新前等于 StartIndex)
        /// </summary>
        public int ChannelStartIndex { get; set; }

        /// <summary>
        /// 当前活跃通道在图表上的确认/截止 K 线索引 (更新后为触发更新的 K 线，未更新前等于 ConfirmedBarIndex)
        /// </summary>
        public int ChannelEndIndex { get; set; }

        /// <summary>
        /// 该通道是否曾因为 K 线超出且无交易信号而自动更新绘制过新通道
        /// </summary>
        public bool IsChannelUpdated { get; set; }

        /// <summary>
        /// 通道累计自动更新次数
        /// </summary>
        public int ChannelUpdateCount { get; set; }

        /// <summary>
        /// 历史被替换的旧通道快照列表
        /// </summary>
        public List<ChannelSnapshot> PreviousChannels { get; } = new();

        #endregion

        #region 观察阶段属性 (绿色通道顶部/底部极值区)

        /// <summary>
        /// 进入观察阶段的 K 线索引 (首次达到绿色通道顶部或底部时的 Bar 索引，未达到时为 -1)
        /// </summary>
        public int ObservationEntryBarIndex { get; set; } = -1;

        /// <summary>
        /// 进入观察阶段的价格 (达到通道顶部时的 High 或达到底部时的 Low)
        /// </summary>
        public decimal ObservationEntryPrice { get; set; } = 0m;

        /// <summary>
        /// 进入观察阶段的 K 线收盘时间戳 (作为第 1 个观察期的起点 T0)
        /// </summary>
        public long ObservationEntryTime { get; set; } = 0L;

        /// <summary>
        /// 是否已满足条件进入观察阶段
        /// </summary>
        public bool IsObservationEntered => ObservationEntryBarIndex >= 0;

        /// <summary>
        /// 历史走势中是否曾有高点达到通道顶部(上涨)或低点达到通道底部(下跌)
        /// </summary>
        public bool HasHistoricalReachedExtreme { get; set; }

        /// <summary>
        /// 历史走势中首次达到通道极值区域的 K 线索引
        /// </summary>
        public int HistoricalExtremeBarIndex { get; set; } = -1;

        /// <summary>
        /// 历史 K 线实体部分是否曾超过通道 (上涨实体突破上轨 / 下跌实体跌破下轨)
        /// </summary>
        public bool HasBodyExceededChannel { get; set; }

        /// <summary>
        /// 实体首次超过通道的 K 线索引
        /// </summary>
        public int BreakoutBarIndex { get; set; } = -1;

        #endregion

        #region 微观 Tick 突破/跌破标记属性

        /// <summary>
        /// 是否触发微观 Tick 跌破/突破通道 (上涨通道跌破下轨 / 下跌通道突破上轨)
        /// </summary>
        public bool HasTickBreakthrough { get; set; }

        /// <summary>
        /// 首次触发微观突破/跌破的 K 线索引
        /// </summary>
        public int TickBreakthroughBarIndex { get; set; } = -1;

        /// <summary>
        /// 首次突破/跌破时的 Tick 价格
        /// </summary>
        public decimal TickBreakthroughPrice { get; set; }

        /// <summary>
        /// 首次突破/跌破时的 Tick 时间戳
        /// </summary>
        public long TickBreakthroughTime { get; set; }

        /// <summary>
        /// 突破/跌破在所属 K 线内 Tick 序列中的序号 (0-indexed)
        /// </summary>
        public int TickBreakthroughTickIndex { get; set; } = -1;

        /// <summary>
        /// 突破类型描述 ("⚡上涨跌破下轨" 或 "⚡下跌突破上轨")
        /// </summary>
        public string TickBreakthroughType { get; set; } = "";

        #endregion

        public override string ToString()
        {
            return $"[{TypeName}] #{StartIndex}..#{EndIndex} (共{BarCount}根), 幅度:{PriceChangePct:+0.00;-0.00}% (确认于#{ConfirmedBarIndex})";
        }
    }
}
