using System;

namespace Common.Models
{
    /// <summary>
    /// UI 界面配置记忆持久化模型 (保存用户选择的交易对、周期、起止时间、策略参数及窗体尺寸)
    /// </summary>
    public class UiSettings
    {
        // 1. 基础数据配置
        public string Coin { get; set; } = "BTCUSDT";
        public string Interval { get; set; } = "1m (1分钟)";
        public DateTime StartDate { get; set; } = new DateTime(2025, 1, 1);
        public DateTime EndDate { get; set; } = new DateTime(2025, 1, 5);

        // 2. 趋势线与开仓策略参数
        public int MaxKlinesCapacity { get; set; } = 2000;
        public int MinTrendLinesCapacity { get; set; } = 1000;
        public int LeftLen { get; set; } = 5;
        public int RightLen { get; set; } = 5;
        public int MaxSpan { get; set; } = 100;
        public int MinSignalLineX1X2 { get; set; } = 40;
        public int MinSignalLineAge { get; set; } = 4;
        public int SignalCooldownSeconds { get; set; } = 60;
        public bool StrictEnvelope { get; set; } = true;
        public bool RealtimeChart { get; set; } = true;
        public bool AutoScale { get; set; } = true;

        // 3. 窗体与布局记忆
        public int FormWidth { get; set; } = 1600;
        public int FormHeight { get; set; } = 960;
        public bool IsMaximized { get; set; } = false;
        public int SplitMainDistance { get; set; } = -1;
        public int SplitLeftDistance { get; set; } = -1;
    }
}
