using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// V 形态反转类型
    /// </summary>
    public enum VPatternType
    {
        /// <summary>
        /// V 底反转形态 (快速下跌后剧烈反弹)
        /// </summary>
        VBottom = 0,

        /// <summary>
        /// 倒 V 顶反转形态 (快速冲高后剧烈回落)
        /// </summary>
        InvertedVTop = 1
    }

    /// <summary>
    /// V 形态 / 倒 V 形态实体模型
    /// </summary>
    public class VPatternItem
    {
        /// <summary>
        /// 唯一标识编号
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 形态类型 (V底 / 倒V顶)
        /// </summary>
        public VPatternType Type { get; set; }

        /// <summary>
        /// 左翼起点 K 线索引
        /// </summary>
        public int LeftIndex { get; set; }

        /// <summary>
        /// 左翼起点价格 (V底为起跌高点，倒V顶为起涨低点)
        /// </summary>
        public decimal LeftPrice { get; set; }

        /// <summary>
        /// V 型尖端顶点 K 线索引 (V底为谷底低点，倒V顶为峰顶高点)
        /// </summary>
        public int VertexIndex { get; set; }

        /// <summary>
        /// V 型尖端顶点价格
        /// </summary>
        public decimal VertexPrice { get; set; }

        /// <summary>
        /// 兼容属性：V底谷底 K 线索引
        /// </summary>
        public int BottomIndex => VertexIndex;

        /// <summary>
        /// 兼容属性：V底谷底价格
        /// </summary>
        public decimal BottomPrice => VertexPrice;

        /// <summary>
        /// 兼容属性：倒V顶峰 K 线索引
        /// </summary>
        public int PeakIndex => VertexIndex;

        /// <summary>
        /// 兼容属性：倒V顶峰价格
        /// </summary>
        public decimal PeakPrice => VertexPrice;

        /// <summary>
        /// 右翼终点 K 线索引 (当前或已完成的右侧拐点)
        /// </summary>
        public int RightIndex { get; set; }

        /// <summary>
        /// 右翼终点价格 (V底为反弹高点，倒V顶为回落低点)
        /// </summary>
        public decimal RightPrice { get; set; }

        /// <summary>
        /// 达到价差门槛首次确认形态时的 K 线索引
        /// </summary>
        public int ConfirmedBarIndex { get; set; }

        /// <summary>
        /// 左臂价差变动幅度 (%)
        /// </summary>
        public decimal LeftSpanPct { get; set; }

        /// <summary>
        /// 右臂价差变动幅度 (%)
        /// </summary>
        public decimal RightSpanPct { get; set; }

        /// <summary>
        /// 综合有效价差百分比 (%)，即两侧翼展满足门槛的有效幅度
        /// </summary>
        public decimal PriceDiffPct => Math.Min(LeftSpanPct, RightSpanPct);

        /// <summary>
        /// 最大价差百分比 (%)
        /// </summary>
        public decimal MaxSpanPct => Math.Max(LeftSpanPct, RightSpanPct);

        /// <summary>
        /// 右翼是否仍在随最新 K 线推进而延伸扩张中
        /// </summary>
        public bool IsRightWingOpen { get; set; } = true;

        /// <summary>
        /// 形态名称简述
        /// </summary>
        public string TypeName => Type == VPatternType.VBottom ? "V形态 (V底反转)" : "倒V形态 (倒V顶反转)";

        /// <summary>
        /// 标记显示短名称
        /// </summary>
        public string ShortName => Type == VPatternType.VBottom ? "V底" : "倒V顶";

        public override string ToString()
        {
            string dir = Type == VPatternType.VBottom ? "V底" : "倒V顶";
            return $"[{dir}] 顶点#{VertexIndex} (价:{VertexPrice:F2}) 左#{LeftIndex}->右#{RightIndex}, 价差:{PriceDiffPct:F2}% (确认于#{ConfirmedBarIndex})";
        }
    }
}
