using Common;
using Common.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using Test.Strategy;

namespace Common.Services
{
    /// <summary>
    /// 量化回测引擎核心服务接口 (支持 WinForms 依赖注入、事件绑定与暂停/继续控制)
    /// </summary>
    public interface IBacktestEngineService
    {
        /// <summary>
        /// 异步启动全流程量化回测执行
        /// </summary>
        Task<BacktestResult> RunBacktestAsync(
            BacktestRequest request,
            IProgress<BacktestProgress>? progress = null,
            CancellationToken ct = default);

        /// <summary>
        /// 当前回测是否处于暂停状态
        /// </summary>
        bool IsPaused { get; }

        /// <summary>
        /// 暂停回测推进
        /// </summary>
        void Pause();

        /// <summary>
        /// 继续恢复回测推进
        /// </summary>
        void Resume();

        #region WinForms / UI 实时事件订阅钩子

        /// <summary>
        /// 逐笔 Tick 推送事件 (可用于实时高频图表/盘口更新)
        /// </summary>
        event Action<RawTick>? OnTickReceived;

        /// <summary>
        /// K 线收盘切分事件 (推送收盘 K 线、全局索引与当前策略快照)
        /// </summary>
        event Action<RawKline, int, TrendLineStrategy>? OnKlineClosed;

        /// <summary>
        /// 趋势线被 Tick 实时穿透击穿事件
        /// </summary>
        event Action<TrendLine, RawTick, string>? OnTrendLinePenetrated;

        /// <summary>
        /// 趋势线触碰回弹触发的开仓交易信号事件
        /// </summary>
        event Action<TradeSignal>? OnTradeSignalGenerated;

        /// <summary>
        /// 日志与系统消息输出事件
        /// </summary>
        event Action<string>? OnLogMessage;

        /// <summary>
        /// 回测进度百分比与状态变更事件
        /// </summary>
        event Action<BacktestProgress>? OnProgressChanged;

        #endregion
    }
}
