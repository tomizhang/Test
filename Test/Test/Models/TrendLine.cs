using Common.Helper;
using System;

namespace Common.Models
{
    /// <summary>
    /// 统一趋势线数据结构与核心特征指标 (原生 decimal 强类型，UTC+0 时间与时间戳)
    /// </summary>
    public struct TrendLine
    {
        /// <summary>
        /// 起始点 K 线数组索引 (x1)
        /// </summary>
        public int X1 { get; set; }

        /// <summary>
        /// 起始点价格 (y1)
        /// </summary>
        public decimal Y1 { get; set; }

        /// <summary>
        /// 起始点时间 (UTC+0)
        /// </summary>
        public DateTime Time1 { get; set; }

        /// <summary>
        /// 起始点 Unix 毫秒时间戳
        /// </summary>
        public long TimestampMs1 { get; set; }

        /// <summary>
        /// 终止点 K 线数组索引 (x2)
        /// </summary>
        public int X2 { get; set; }

        /// <summary>
        /// 终止点价格 (y2)
        /// </summary>
        public decimal Y2 { get; set; }

        /// <summary>
        /// 终止点时间 (UTC+0)
        /// </summary>
        public DateTime Time2 { get; set; }

        /// <summary>
        /// 终止点 Unix 毫秒时间戳
        /// </summary>
        public long TimestampMs2 { get; set; }

        /// <summary>
        /// 原始绝对价格斜率: (y2 - y1) / (x2 - x1)
        /// </summary>
        public decimal RawK { get; set; }

        /// <summary>
        /// 归一化百分比斜率 (%/bar): ((y2 - y1) / y1) / (x2 - x1) * 100
        /// 解决不同币种价格量级不同导致的斜率失真与不可比问题
        /// </summary>
        public decimal K { get; set; }

        /// <summary>
        /// 趋势线基准极值点间的整体百分比斜率 (%): (y2 - y1) / y1 * 100
        /// </summary>
        public decimal OverallSlopePct => Y1 > 0m ? (Y2 - Y1) / Y1 * 100m : 0m;

        /// <summary>
        /// 趋势线延伸至当前缓存价格的全局总整体百分比斜率 (%): (CachedCurrentPrice - y1) / y1 * 100
        /// </summary>
        public decimal TotalOverallSlopePct => Y1 > 0m ? (CachedCurrentPrice - Y1) / Y1 * 100m : 0m;

        /// <summary>
        /// x1 到 x2 的 K 线跨度差值 (line_x1_x2 = x2 - x1)
        /// </summary>
        public int LineX1X2 => X2 - X1;

        /// <summary>
        /// x2 到最新 K 线点的差值 (line_age = latestIndex - x2)
        /// </summary>
        public int LineAge { get; set; }

        /// <summary>
        /// 🌟 起点 x1 到最新 K 线点的总跨度长度 (total_age = latestIndex - x1 = LineX1X2 + LineAge)
        /// </summary>
        public int TotalAge
        {
            get => LineX1X2 + LineAge;
            set => LineAge = value - LineX1X2;
        }

        /// <summary>
        /// 🌟 小写别名 totalage (记录 x1 到最新 K 线的长度)
        /// </summary>
        public int totalage
        {
            get => TotalAge;
            set => TotalAge = value;
        }

        /// <summary>
        /// 趋势线向后延伸穿过/碰撞第一根 K 线的范围 (line_extension_range = x_collide - x2)。
        /// 若从 x2 向后延伸未穿透任何 K 线，则值为从 x2 延伸至最新 K 线的距离。
        /// </summary>
        public int LineExtensionRange { get; set; }

        /// <summary>
        /// 首次穿越/碰撞的 K 线索引 (若未碰撞则为 -1)
        /// </summary>
        public int CollidedKlineIndex { get; set; }

        /// <summary>
        /// 🌟 是否已触发开仓交易信号 (触发成功后图表渲染为绿色)
        /// </summary>
        public bool IsTriggered { get; set; }

        /// <summary>
        /// 🌟 是否已连接/触碰确认第 3 个点 (价格在趋势线 0.1% 附近，三点共线强趋势线不删除且凸显金黄色)
        /// </summary>
        public bool IsThreePointConfirmed { get; set; }

        /// <summary>
        /// 第 3 个触碰确认点的 K 线索引 (若无则为 -1)
        /// </summary>
        public int X3 { get; set; }

        /// <summary>
        /// 第 3 个触碰确认点的价格 (若无则为 0)
        /// </summary>
        public decimal Y3 { get; set; }

        /// <summary>
        /// 触碰/连接的极值点总数 (默认 2，触碰第 3 个点后为 3)
        /// </summary>
        public int TouchCount { get; set; }

        /// <summary>
        /// ⚡ 当前 K 线索引下的延伸线缓存价格 (在 OnKline 时计算一次，OnTick 零运算极速比较)
        /// </summary>
        public decimal CachedCurrentPrice { get; set; }

        /// <summary>
        /// 趋势线类型 (高点阻力线 / 低点支撑线)
        /// </summary>
        public PivotType Type { get; set; }

        /// <summary>
        /// 是否为阻力趋势线 (由波峰构成)
        /// </summary>
        public bool IsResistance => Type == PivotType.Peak;

        /// <summary>
        /// 是否为支撑趋势线 (由波谷构成)
        /// </summary>
        public bool IsSupport => Type == PivotType.Valley;

        /// <summary>
        /// 趋势线是否有效
        /// </summary>
        public bool IsValid => X2 > X1 && Y1 > 0m && Y2 > 0m;

        /// <summary>
        /// 根据趋势线方程推算指定 K 线索引 x 处的延长线价格: y(x) = y1 + RawK * (x - x1)
        /// </summary>
        public decimal GetPriceAt(int x)
        {
            return Y1 + RawK * (x - X1);
        }

        /// <summary>
        /// 基于 K 线开盘时间戳推算延伸线价格 (兼容在线实盘 LiveStream 模式与窗口滑动，全 decimal 精度)
        /// </summary>
        public decimal GetPriceAtTime(DateTime targetTime)
        {
            if (Time2 <= Time1 || Time1 == DateTime.MinValue || targetTime == DateTime.MinValue)
            {
                return Y2;
            }

            decimal totalSeconds = (decimal)(Time2 - Time1).TotalSeconds;
            if (totalSeconds <= 0m) return Y2;

            decimal elapsedSeconds = (decimal)(targetTime - Time1).TotalSeconds;
            decimal priceDelta = Y2 - Y1;

            return Y1 + priceDelta * (elapsedSeconds / totalSeconds);
        }

        #region UI/日志按需格式化

        public string FormattedTime1 => Time1.ToUtc0String();
        public string FormattedTime2 => Time2.ToUtc0String();

        #endregion

        public override string ToString()
        {
            string lineTypeStr = IsResistance ? "阻力趋势线(Peak)" : "支撑趋势线(Valley)";
            return $"[{lineTypeStr}] ({X1}, {Y1:F2} @ {FormattedTime1}) -> ({X2}, {Y2:F2} @ {FormattedTime2}) | 归一化斜率: {K:F4}%/bar, 跨度: {LineX1X2}, 寿命: {LineAge}, 总长(X1->最新): {TotalAge}, 延伸: {LineExtensionRange}";
        }
    }
}
