using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 动态包络通道计算结果模型
    /// 默认总长度 200 根 (左侧 100 根回溯分析，右侧延长 100 根未来预测)
    /// 上轨严格包含所有高点，下轨严格包含所有低点，斜率与高度随新增 K 线实时动态演变
    /// </summary>
    public struct DynamicChannelResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// 通道计算窗口起始 K 线索引 (左端点)
        /// </summary>
        public int StartX { get; set; }

        /// <summary>
        /// 当前最新 K 线索引 (中心基准点，即左侧窗口终点、右侧延伸起点)
        /// </summary>
        public int CurrentX { get; set; }

        /// <summary>
        /// 通道向右延伸终止索引 (右端点)
        /// </summary>
        public int EndX { get; set; }

        /// <summary>
        /// 左侧计算 K 线跨度 (默认 100)
        /// </summary>
        public int LeftLength => Math.Max(1, CurrentX - StartX + 1);

        /// <summary>
        /// 右侧延伸 K 线跨度 (默认 100)
        /// </summary>
        public int RightExtendLength => Math.Max(0, EndX - CurrentX);

        /// <summary>
        /// 通道总跨度 (默认 200)
        /// </summary>
        public int TotalLength => Math.Max(0, EndX - StartX);

        /// <summary>
        /// 通道斜率 k ($/bar 价格斜率)
        /// </summary>
        public decimal SlopeK { get; set; }

        /// <summary>
        /// 归一化百分比斜率 (%/bar)
        /// </summary>
        public decimal SlopePct { get; set; }

        /// <summary>
        /// 通道倾斜角度 (估算角度值，单位：度)
        /// </summary>
        public double AngleDeg { get; set; }

        /// <summary>
        /// 上轨截距 b_upper
        /// </summary>
        public decimal UpperIntercept { get; set; }

        /// <summary>
        /// 下轨截距 b_lower
        /// </summary>
        public decimal LowerIntercept { get; set; }

        /// <summary>
        /// 中轨截距 b_center
        /// </summary>
        public decimal CenterIntercept => (UpperIntercept + LowerIntercept) / 2m;

        /// <summary>
        /// 通道垂直价格高度 (Upper - Lower，单位：USDT)
        /// </summary>
        public decimal ChannelHeight => UpperIntercept - LowerIntercept;

        /// <summary>
        /// 通道相对当前价格的百分比高度 (%)
        /// </summary>
        public decimal ChannelHeightPct { get; set; }

        /// <summary>
        /// 触碰/锚定上轨的最高 K 线索引
        /// </summary>
        public int TouchHighIndex { get; set; }

        /// <summary>
        /// 触碰/锚定上轨的最高价格
        /// </summary>
        public decimal TouchHighPrice { get; set; }

        /// <summary>
        /// 触碰/锚定下轨的最低 K 线索引
        /// </summary>
        public int TouchLowIndex { get; set; }

        /// <summary>
        /// 触碰/锚定下轨的最低价格
        /// </summary>
        public decimal TouchLowPrice { get; set; }

        /// <summary>
        /// 计算指定 X 坐标处的上轨价格: y = k * x + b_upper
        /// </summary>
        public decimal GetUpperPrice(double x) => (decimal)x * SlopeK + UpperIntercept;

        /// <summary>
        /// 计算指定 X 坐标处的下轨价格: y = k * x + b_lower
        /// </summary>
        public decimal GetLowerPrice(double x) => (decimal)x * SlopeK + LowerIntercept;

        /// <summary>
        /// 计算指定 X 坐标处的中轨价格: y = k * x + b_center
        /// </summary>
        public decimal GetCenterPrice(double x) => (decimal)x * SlopeK + CenterIntercept;

        public override string ToString()
        {
            if (!IsValid) return "通道未就绪 (K线不足)";
            return $"通道[总长:{TotalLength}(左{LeftLength}+右{RightExtendLength}) | 角度:{AngleDeg:F1}° | 斜率:{SlopeK:+0.00;-0.00}$ ({SlopePct:+0.000;-0.000}%) | 高度:{ChannelHeight:F2}$ ({ChannelHeightPct:F2}%)]";
        }
    }
}
