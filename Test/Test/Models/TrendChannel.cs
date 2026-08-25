using Common.Helper;
using System;

namespace Common.Models
{
    /// <summary>
    /// 统一趋势通道 (Trend Channel) 数据结构
    /// 由一对相互平行的阻力趋势线 (UpperLine 上轨) 与支撑趋势线 (LowerLine 下轨) 加工生成
    /// </summary>
    public struct TrendChannel
    {
        /// <summary>
        /// 通道唯一标识序号
        /// </summary>
        public int ChannelId { get; set; }

        /// <summary>
        /// 通道上轨：阻力趋势线 (由波峰高点生成)
        /// </summary>
        public TrendLine UpperLine { get; set; }

        /// <summary>
        /// 通道下轨：支撑趋势线 (由波谷低点生成)
        /// </summary>
        public TrendLine LowerLine { get; set; }

        /// <summary>
        /// 通道有效起始 K 线索引 (两轨共同可见的最早起点)
        /// </summary>
        public int StartX => Math.Max(UpperLine.X1, LowerLine.X1);

        /// <summary>
        /// 通道有效终止 K 线索引
        /// </summary>
        public int EndX
        {
            get
            {
                int endUpper = UpperLine.CollidedKlineIndex >= 0 ? UpperLine.CollidedKlineIndex : (UpperLine.X2 + UpperLine.LineAge);
                int endLower = LowerLine.CollidedKlineIndex >= 0 ? LowerLine.CollidedKlineIndex : (LowerLine.X2 + LowerLine.LineAge);
                return Math.Max(StartX, Math.Min(endUpper, endLower));
            }
        }

        /// <summary>
        /// 通道重叠有效 K 线跨度
        /// </summary>
        public int Span => Math.Max(0, EndX - StartX);

        /// <summary>
        /// 通道平均归一化百分比斜率 (%/bar)
        /// </summary>
        public decimal SlopeK => (UpperLine.K + LowerLine.K) / 2m;

        /// <summary>
        /// 通道平均原始价格斜率 ($/bar)
        /// </summary>
        public decimal RawK => (UpperLine.RawK + LowerLine.RawK) / 2m;

        /// <summary>
        /// 通道基准点整体百分比斜率 (%)
        /// </summary>
        public decimal ChannelSlopePct => (UpperLine.OverallSlopePct + LowerLine.OverallSlopePct) / 2m;

        /// <summary>
        /// 两轨斜率差异 (%/bar)
        /// </summary>
        public decimal SlopeDifference => Math.Abs(UpperLine.K - LowerLine.K);

        /// <summary>
        /// 是否为双轨均未被击穿的活跃通道
        /// </summary>
        public bool IsActive => UpperLine.CollidedKlineIndex == -1 && LowerLine.CollidedKlineIndex == -1;

        /// <summary>
        /// 获取指定 K 线索引 x 处的通道高度/价格宽度 (Upper - Lower)
        /// </summary>
        public decimal GetWidthAt(int x)
        {
            return UpperLine.GetPriceAt(x) - LowerLine.GetPriceAt(x);
        }

        /// <summary>
        /// 获取指定 K 线索引 x 处的通道百分比宽度: (Upper - Lower) / Lower * 100%
        /// </summary>
        public decimal GetWidthPctAt(int x)
        {
            decimal lowerPrice = LowerLine.GetPriceAt(x);
            if (lowerPrice <= 0m) return 0m;
            return (UpperLine.GetPriceAt(x) - lowerPrice) / lowerPrice * 100m;
        }

        /// <summary>
        /// 获取通道在起始点与终止点的平均百分比宽度 (%)
        /// </summary>
        public decimal AverageWidthPct
        {
            get
            {
                int s = StartX;
                int e = EndX;
                if (e <= s) return GetWidthPctAt(s);
                return (GetWidthPctAt(s) + GetWidthPctAt(e)) / 2m;
            }
        }

        public override string ToString()
        {
            string status = IsActive ? "活跃" : "历史/已穿透";
            return $"[趋势通道 #{ChannelId}] 上轨:#{UpperLine.X1}->#{UpperLine.X2} | 下轨:#{LowerLine.X1}->#{LowerLine.X2} | " +
                   $"重叠跨度:{Span} bars, 斜率:{SlopeK:F4}%/bar (整体:{ChannelSlopePct:+0.00;-0.00;0.00}%), 均宽:{AverageWidthPct:F2}%, 状态:{status}";
        }
    }
}
