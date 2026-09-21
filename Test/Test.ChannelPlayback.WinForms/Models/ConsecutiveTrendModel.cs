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
        /// 中轨截距
        /// </summary>
        public decimal MidIntercept => (UpperIntercept + LowerIntercept) / 2m;

        /// <summary>
        /// 通道绝对高度
        /// </summary>
        public decimal ChannelHeight => UpperIntercept - LowerIntercept;

        /// <summary>
        /// 是否已成功拟合平行通道
        /// </summary>
        public bool HasChannel { get; set; }

        /// <summary>
        /// 计算给定 X 坐标处上轨价格
        /// </summary>
        public decimal GetUpperPrice(double x) => SlopeK * (decimal)x + UpperIntercept;

        /// <summary>
        /// 计算给定 X 坐标处下轨价格
        /// </summary>
        public decimal GetLowerPrice(double x) => SlopeK * (decimal)x + LowerIntercept;

        /// <summary>
        /// 计算给定 X 坐标处中轨价格
        /// </summary>
        public decimal GetMidPrice(double x) => SlopeK * (decimal)x + MidIntercept;

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

        public override string ToString()
        {
            return $"[{TypeName}] #{StartIndex}..#{EndIndex} (共{BarCount}根), 幅度:{PriceChangePct:+0.00;-0.00}% (确认于#{ConfirmedBarIndex})";
        }
    }
}
