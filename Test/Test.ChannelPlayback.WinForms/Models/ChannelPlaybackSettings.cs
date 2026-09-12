using System;

namespace Test.ChannelPlayback.WinForms.Models
{
    /// <summary>
    /// 动态包络通道回放系统 UI 界面与参数持久化配置模型
    /// </summary>
    public class ChannelPlaybackSettings
    {
        // 窗口尺寸与分割布局记忆
        public int WindowWidth { get; set; } = 1600;
        public int WindowHeight { get; set; } = 950;
        public int WindowLeft { get; set; } = -1;
        public int WindowTop { get; set; } = -1;
        public bool IsMaximized { get; set; } = false;
        public int SplitterMainDistance { get; set; } = -1;
        public int SplitterLeftDistance { get; set; } = -1;

        // 数据源参数
        public string Coin { get; set; } = "BTCUSDT";
        public string Interval { get; set; } = "1m (1分钟)";
        public DateTime StartDate { get; set; } = new DateTime(2024, 1, 1);
        public DateTime EndDate { get; set; } = new DateTime(2024, 1, 1);

        // 通道核心参数
        public int LeftLength { get; set; } = 100;
        public int RightExtendLength { get; set; } = 100;
        public int CalculationMode { get; set; } = 0; // 0: 三点趋势定向(向上2低1高/向下2高1低), 1: 三点紧凑自适应, 2: 强制2低1高, 3: 强制2高1低, 4: 线性回归, 5: 极小高度
        public int WindowMode { get; set; } = 0;      // 0: 滑动窗口, 1: 全量累计
        public bool AutoScale { get; set; } = true;
        public bool FollowLatest { get; set; } = true;
        public bool ShowTouchMarkers { get; set; } = true;
        public bool ShowLegend { get; set; } = false;

        // 回放控制参数
        public int SpeedIntervalMs { get; set; } = 50;
    }
}
