using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 突破判定依据规则
    /// </summary>
    public enum BreakoutRule
    {
        /// <summary>
        /// K 线收盘价突破 (Close 突破轨线，过滤虚假影线毛刺，行业标准稳健模式)
        /// </summary>
        ClosePrice = 0,

        /// <summary>
        /// 最高/最低价实时穿透 (High 穿透上轨 或 Low 穿透下轨，激进敏感模式)
        /// </summary>
        ExtremePrice = 1
    }

    /// <summary>
    /// 相对极值保留通道数据模型
    /// 当下降通道上轨向后延展无 K 线碰撞确认相对高点后，或上升通道下轨无碰撞确认相对低点后，
    /// 该通道被锁定保留并持续向前延展，专门用于识别后续 K 线的反转突破/跌破。
    /// </summary>
    public class RetainedChannel
    {
        /// <summary>
        /// 锁定保留时的基准动态通道计算快照
        /// </summary>
        public DynamicChannelResult BaseChannel { get; set; }

        /// <summary>
        /// 通道方向结构类型
        /// </summary>
        public ChannelDirectionType DirectionType => BaseChannel.DirectionType;

        /// <summary>
        /// 是否为下降通道 (斜率 k < 0，上轨为阻力线，等待向上突破)
        /// </summary>
        public bool IsDownward => BaseChannel.SlopeK < 0m;

        /// <summary>
        /// 是否为上升通道 (斜率 k > 0，下轨为支撑线，等待向下跌破)
        /// </summary>
        public bool IsUpward => BaseChannel.SlopeK > 0m;

        /// <summary>
        /// 通道斜率 k
        /// </summary>
        public decimal SlopeK => BaseChannel.SlopeK;

        /// <summary>
        /// 通道上轨截距
        /// </summary>
        public decimal UpperIntercept => BaseChannel.UpperIntercept;

        /// <summary>
        /// 通道下轨截距
        /// </summary>
        public decimal LowerIntercept => BaseChannel.LowerIntercept;

        /// <summary>
        /// 通道垂直高度
        /// </summary>
        public decimal ChannelHeight => BaseChannel.ChannelHeight;

        #region 相对极值确认特征

        /// <summary>
        /// 相对极值锚点 K 线索引 (若是下降通道则为相对高点，若是上升通道则为相对低点)
        /// </summary>
        public int AnchorExtremeIndex { get; set; }

        /// <summary>
        /// 相对极值锚点价格
        /// </summary>
        public decimal AnchorExtremePrice { get; set; }

        /// <summary>
        /// 确认无碰撞达标时的当前 K 线索引
        /// </summary>
        public int ConfirmedBarIndex { get; set; }

        /// <summary>
        /// 要求的最小无碰撞延展 K 线数
        /// </summary>
        public int RequiredNoCollisionBars { get; set; } = 3;

        /// <summary>
        /// 是否为强趋势大跨度特殊保留通道 (0.8f 紫色永久保留通道，不被删除)
        /// </summary>
        public bool IsSpecialStrongTrend { get; set; } = false;

        #endregion

        #region 突破跟踪生命周期

        /// <summary>
        /// 当前是否已被后续 K 线有效突破
        /// </summary>
        public bool IsBrokenOut { get; set; } = false;

        /// <summary>
        /// 突破发生时的 K 线索引 (-1 表示尚未突破，仍在监控中)
        /// </summary>
        public int BreakoutBarIndex { get; set; } = -1;

        /// <summary>
        /// 突破时的实际成交价格
        /// </summary>
        public decimal BreakoutPrice { get; set; } = 0m;

        /// <summary>
        /// 突破时刻对应轨线点位价格
        /// </summary>
        public decimal BoundaryPriceAtBreakout { get; set; } = 0m;

        /// <summary>
        /// 突破超越幅度百分比 (%)
        /// </summary>
        public decimal BreakoutPct { get; set; } = 0m;

        /// <summary>
        /// 当前跟踪延展到的最新 K 线索引
        /// </summary>
        public int EndTrackIndex { get; set; }

        #endregion

        /// <summary>
        /// 计算指定 X 坐标处的保留上轨价格: y = k * x + b_upper
        /// </summary>
        public decimal GetUpperPrice(double x) => (decimal)x * SlopeK + UpperIntercept;

        /// <summary>
        /// 计算指定 X 坐标处的保留下轨价格: y = k * x + b_lower
        /// </summary>
        public decimal GetLowerPrice(double x) => (decimal)x * SlopeK + LowerIntercept;

        /// <summary>
        /// 计算指定 X 坐标处的保留中轨价格: y = k * x + (b_upper + b_lower) / 2
        /// </summary>
        public decimal GetCenterPrice(double x) => (decimal)x * SlopeK + (UpperIntercept + LowerIntercept) / 2m;

        /// <summary>
        /// 获取突破关键边界线点位 (下降通道关注上轨阻力，上升通道关注下轨支撑)
        /// </summary>
        public decimal GetBreakoutBoundaryPrice(double x) => IsDownward ? GetUpperPrice(x) : GetLowerPrice(x);

        /// <summary>
        /// 状态简要描述
        /// </summary>
        public string StatusSummary
        {
            get
            {
                string dirStr = IsDownward ? "📉 下降保留通道" : "📈 上升保留通道";
                string extremeStr = IsDownward ? $"相对高点 #{AnchorExtremeIndex} (${AnchorExtremePrice:F2})" : $"相对低点 #{AnchorExtremeIndex} (${AnchorExtremePrice:F2})";

                if (IsBrokenOut)
                {
                    string bType = IsDownward ? "🚀 向上突破" : "💥 向下跌破";
                    return $"{dirStr} [{bType}] 于 K线 #{BreakoutBarIndex} (价: {BreakoutPrice:F2}, 幅度: {BreakoutPct:+0.00;-0.00}%)";
                }
                else
                {
                    return $"{dirStr} [⏳ 监控突破中] 基准{extremeStr} (跟踪至 #{EndTrackIndex})";
                }
            }
        }
    }
}
