using System;

namespace Test.PercentageBar.WinForms.Models
{
    /// <summary>
    /// 连续走势方向类型
    /// </summary>
    public enum ConsecutiveTrendType
    {
        /// <summary>
        /// 连续上涨形态
        /// </summary>
        Bullish = 0,

        /// <summary>
        /// 连续下跌形态
        /// </summary>
        Bearish = 1
    }

    /// <summary>
    /// 连续上涨或连续下跌动能波段结构模型
    /// </summary>
    public class ConsecutiveTrendItem
    {
        /// <summary>
        /// 波段唯一标识
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 形态类型 (连续上涨 / 连续下跌)
        /// </summary>
        public ConsecutiveTrendType Type { get; set; }

        /// <summary>
        /// 起始 K 线索引 (包含)
        /// </summary>
        public int StartIndex { get; set; }

        /// <summary>
        /// 起始基准价格 (启动前一根收盘价，若为第0根则为开盘价)
        /// </summary>
        public decimal StartPrice { get; set; }

        /// <summary>
        /// 终止 K 线索引 (包含)
        /// </summary>
        public int EndIndex { get; set; }

        /// <summary>
        /// 终止 K 线收盘价
        /// </summary>
        public decimal EndPrice { get; set; }

        /// <summary>
        /// 累计涨跌幅度百分比 (%)
        /// </summary>
        public decimal PriceChangePct { get; set; }

        /// <summary>
        /// 首次达到门槛要求 (如 >=5根 且 >=2.5%) 的当根 K 线索引 (绝对零未来函数)
        /// </summary>
        public int ConfirmedBarIndex { get; set; }

        /// <summary>
        /// 该波段当前是否仍处于延伸活跃中 (即已连贯到达最新一根 K 线)
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// 拟合出的平行通道回归斜率 k (单位: 价格/Bar)
        /// </summary>
        public decimal SlopeK { get; set; }

        /// <summary>
        /// 通道上轨截距 b (y = k*x + upperB)
        /// </summary>
        public decimal UpperIntercept { get; set; }

        /// <summary>
        /// 通道下轨截距 b (y = k*x + lowerB)
        /// </summary>
        public decimal LowerIntercept { get; set; }

        /// <summary>
        /// 是否成功拟合平行通道
        /// </summary>
        public bool HasChannel { get; set; }

        #region 近期趋势线几何参数 (Recent Trend Lines)

        /// <summary>
        /// 是否成功生成波段自身近期主趋势线
        /// </summary>
        public bool HasTrendLine { get; set; }

        /// <summary>
        /// 近期趋势线起点 X (Bar 索引)
        /// </summary>
        public int TrendLineStartX { get; set; }

        /// <summary>
        /// 近期趋势线起点 Y (价格)
        /// </summary>
        public decimal TrendLineStartY { get; set; }

        /// <summary>
        /// 近期趋势线关键锚点 X (Bar 索引)
        /// </summary>
        public int TrendLineEndX { get; set; }

        /// <summary>
        /// 近期趋势线关键锚点 Y (价格)
        /// </summary>
        public decimal TrendLineEndY { get; set; }

        /// <summary>
        /// 近期趋势线斜率 (USDT/Bar)
        /// </summary>
        public decimal TrendLineSlope { get; set; }

        /// <summary>
        /// 是否存在前序近期结构趋势线
        /// </summary>
        public bool HasPrecedingTrendLine { get; set; }

        /// <summary>
        /// 前序近期结构趋势线起点 X
        /// </summary>
        public int PrecedingTrendStartX { get; set; }

        /// <summary>
        /// 前序近期结构趋势线起点 Y
        /// </summary>
        public decimal PrecedingTrendStartY { get; set; }

        /// <summary>
        /// 前序近期结构趋势线终点 X
        /// </summary>
        public int PrecedingTrendEndX { get; set; }

        /// <summary>
        /// 前序近期结构趋势线终点 Y
        /// </summary>
        public decimal PrecedingTrendEndY { get; set; }

        /// <summary>
        /// 前序近期结构趋势线斜率
        /// </summary>
        public decimal PrecedingTrendSlope { get; set; }

        /// <summary>
        /// 该连续形态附近识别出的完整多级近期趋势线集合 (包含支撑/阻力/中枢/前序突破/极值映射等多维结构线)
        /// </summary>
        public List<ConsecutiveRecentTrendLine> RecentTrendLines { get; set; } = new();

        /// <summary>
        /// 关联最近1000根K线的高/低点位交互绘制的宏观趋势线集合 (连涨关联高点，连跌关联低点)
        /// </summary>
        public List<ConsecutiveRecentTrendLine> Macro1000BarLines { get; set; } = new();

        #endregion

        /// <summary>
        /// 包含的连续 K 线根数
        /// </summary>
        public int BarCount => EndIndex - StartIndex + 1;

        /// <summary>
        /// 通道垂直高度 (USDT)
        /// </summary>
        public decimal ChannelHeight => UpperIntercept - LowerIntercept;

        /// <summary>
        /// 形态名称描述
        /// </summary>
        public string TypeName => Type == ConsecutiveTrendType.Bullish ? "连续上涨形态" : "连续下跌形态";

        /// <summary>
        /// 格式化摘要描述
        /// </summary>
        public override string ToString()
        {
            string icon = Type == ConsecutiveTrendType.Bullish ? "🔥连涨" : "❄️连跌";
            string sign = PriceChangePct >= 0 ? "+" : "";
            return $"[{icon}] #{StartIndex}~#{EndIndex} (共{BarCount}根) 幅度:{sign}{PriceChangePct:F2}% (基准价:{StartPrice:F2}->现价:{EndPrice:F2}) 斜率:{SlopeK:F4}";
        }
    }

    /// <summary>
    /// 连续波段附近近期趋势线角色类型
    /// </summary>
    public enum ConsecutiveRecentTrendLineType
    {
        /// <summary>
        /// 波段下轨主支撑线 (连涨支撑 / 连跌底线)
        /// </summary>
        StreakSupport = 0,

        /// <summary>
        /// 波段上轨阻力/加速线 (连涨加速天花板 / 连跌压制)
        /// </summary>
        StreakResistance = 1,

        /// <summary>
        /// 波段实体中心动能向量轴
        /// </summary>
        StreakCenter = 2,

        /// <summary>
        /// 前序波峰阻力突破线 (历史高点连线穿透)
        /// </summary>
        PrecedingResistance = 3,

        /// <summary>
        /// 前序波谷结构托底线 (历史低点连线延伸)
        /// </summary>
        PrecedingSupport = 4,

        /// <summary>
        /// 前序微型收敛三角形/楔形切线
        /// </summary>
        PrecedingConvergence = 5,

        /// <summary>
        /// 近端跨波段全局极值映射线
        /// </summary>
        MajorSwingProjection = 6,

        /// <summary>
        /// 连续上涨关联千根高点位交互趋势线
        /// </summary>
        Interactive1000BarHigh = 7,

        /// <summary>
        /// 连续下跌关联千根低点位交互趋势线
        /// </summary>
        Interactive1000BarLow = 8,

        /// <summary>
        /// 千根宏观趋势线向前推演至最新K线的投射目标阻力/支撑位
        /// </summary>
        Projected1000BarTarget = 9
    }

    /// <summary>
    /// 连续形态附近单条近期趋势线实体
    /// </summary>
    public class ConsecutiveRecentTrendLine
    {
        /// <summary>
        /// 趋势线类型
        /// </summary>
        public ConsecutiveRecentTrendLineType LineType { get; set; }

        /// <summary>
        /// 趋势线中文名称说明
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 实体线起点 X (Bar 索引)
        /// </summary>
        public int StartX { get; set; }

        /// <summary>
        /// 实体线起点 Y (价格)
        /// </summary>
        public decimal StartY { get; set; }

        /// <summary>
        /// 实体线终点 X (Bar 索引，严格 <= ConfirmedBarIndex)
        /// </summary>
        public int EndX { get; set; }

        /// <summary>
        /// 实体线终点 Y (价格)
        /// </summary>
        public decimal EndY { get; set; }

        /// <summary>
        /// 趋势线斜率 (USDT/Bar)
        /// </summary>
        public decimal Slope { get; set; }

        /// <summary>
        /// 虚线射线延伸终点 X (Bar 索引)
        /// </summary>
        public int RayEndX { get; set; }

        /// <summary>
        /// 虚线射线延伸终点 Y (价格)
        /// </summary>
        public decimal RayEndY { get; set; }

        /// <summary>
        /// 视觉颜色十六进制
        /// </summary>
        public string ColorHex { get; set; } = "#38bdf8";

        /// <summary>
        /// 实体线宽 (f)
        /// </summary>
        public float LineWidth { get; set; } = 1.0f;

        /// <summary>
        /// 端点气泡标牌文本 (为空则不渲染端点标签以保持简洁)
        /// </summary>
        public string TagText { get; set; } = string.Empty;

        /// <summary>
        /// 是否为主干核心趋势线 (优先展示标签与加粗显示)
        /// </summary>
        public bool IsPrimary { get; set; } = true;

        /// <summary>
        /// 是否有第 3 个点位落于该趋势线容差阈值附近 (3点共线确认，满足时显示为粉红色)
        /// </summary>
        public bool HasThirdPointTouch { get; set; } = false;

        /// <summary>
        /// 满足第 3 点落于趋势线容差范围内的 K 线索引
        /// </summary>
        public int ThirdPointBarIndex { get; set; } = -1;

        /// <summary>
        /// 满足第 3 点落于趋势线容差范围内的价格
        /// </summary>
        public decimal ThirdPointPrice { get; set; } = 0m;

        /// <summary>
        /// 第 3 点与趋势线预测价格的相对偏离百分比 (|Price - LinePrice| / LinePrice * 100%)
        /// </summary>
        public decimal ThirdPointDistancePct { get; set; } = 0m;
    }
}
