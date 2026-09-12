using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 通道方向与结构确认类型 (基于三点确认机制)
    /// </summary>
    public enum ChannelDirectionType
    {
        /// <summary>
        /// 两个低点一个高点 (以两低点支撑线确定方向，对侧高点确定通道高度，标准上升/支撑通道)
        /// </summary>
        TwoLowsOneHigh = 0,

        /// <summary>
        /// 两个高点一个低点 (以两高点阻力线确定方向，对侧低点确定通道高度，标准下降/阻力通道)
        /// </summary>
        TwoHighsOneLow = 1,

        /// <summary>
        /// 经典线性回归外包络 (全量回归拟合均值斜率)
        /// </summary>
        LinearRegression = 2
    }

    /// <summary>
    /// 动态包络通道计算结果模型
    /// 默认总长度 200 根 (左侧 100 根回溯分析，右侧延长 100 根未来预测)
    /// 方向由三个点严格确认 (2低1高 或 2高1低)，上轨严格包含所有高点，下轨严格包含所有低点
    /// </summary>
    public struct DynamicChannelResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// 通道方向与结构确认类型
        /// </summary>
        public ChannelDirectionType DirectionType { get; set; }

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

        #region 三点确认核心锚点数据

        /// <summary>
        /// 基准锚点 1 索引 (若是 2低1高 则为低点1，若是 2高1低 则为高点1)
        /// </summary>
        public int BasePoint1Index { get; set; }
        public decimal BasePoint1Price { get; set; }

        /// <summary>
        /// 基准锚点 2 索引 (若是 2低1高 则为低点2，若是 2高1低 则为高点2)
        /// </summary>
        public int BasePoint2Index { get; set; }
        public decimal BasePoint2Price { get; set; }

        /// <summary>
        /// 对侧锚点 3 索引 (若是 2低1高 则为对侧高点，若是 2高1低 则为对侧低点)
        /// </summary>
        public int OppositePointIndex { get; set; }
        public decimal OppositePointPrice { get; set; }

        /// <summary>
        /// 基准两点之间的 K 线跨度
        /// </summary>
        public int BasePointsSpan => Math.Abs(BasePoint2Index - BasePoint1Index);

        #endregion

        #region 触碰极值点 (兼容与快速查询)

        /// <summary>
        /// 触碰/锚定上轨的最高 K 线索引 1
        /// </summary>
        public int TouchHighIndex { get; set; }
        public decimal TouchHighPrice { get; set; }

        /// <summary>
        /// 触碰/锚定上轨的高点索引 2 (当为 2高1低 结构时有效)
        /// </summary>
        public int TouchHighIndex2 { get; set; }
        public decimal TouchHighPrice2 { get; set; }

        /// <summary>
        /// 触碰/锚定下轨的最低 K 线索引 1
        /// </summary>
        public int TouchLowIndex { get; set; }
        public decimal TouchLowPrice { get; set; }

        /// <summary>
        /// 触碰/锚定下轨的低点索引 2 (当为 2低1高 结构时有效)
        /// </summary>
        public int TouchLowIndex2 { get; set; }
        public decimal TouchLowPrice2 { get; set; }

        #endregion

        /// <summary>
        /// 是否为严格三点确认通道
        /// </summary>
        public bool IsThreePointChannel => DirectionType == ChannelDirectionType.TwoLowsOneHigh || DirectionType == ChannelDirectionType.TwoHighsOneLow;

        /// <summary>
        /// 通道方向特征中文描述
        /// </summary>
        public string DirectionDescription
        {
            get
            {
                if (!IsValid) return "未就绪";
                return DirectionType switch
                {
                    ChannelDirectionType.TwoLowsOneHigh => $"📈 2低点1高点 (支撑基准 / 跨度{BasePointsSpan})",
                    ChannelDirectionType.TwoHighsOneLow => $"📉 2高点1低点 (阻力基准 / 跨度{BasePointsSpan})",
                    _ => "📐 线性回归包络"
                };
            }
        }

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
            return $"通道[{DirectionDescription} | 总长:{TotalLength}(左{LeftLength}+右{RightExtendLength}) | 角度:{AngleDeg:F1}° | 斜率:{SlopeK:+0.00;-0.00}$ ({SlopePct:+0.000;-0.000}%) | 高度:{ChannelHeight:F2}$ ({ChannelHeightPct:F2}%)]";
        }
    }
}
