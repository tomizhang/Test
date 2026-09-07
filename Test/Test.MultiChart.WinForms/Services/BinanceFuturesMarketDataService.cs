using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Common;
using Common.Helper;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Test.MultiChart.WinForms.Models;

namespace Test.MultiChart.WinForms.Services
{
    /// <summary>
    /// 币安 USDT 本位永续合约行情数据中心 (逐笔实时驱动各周期 K 线毫秒级跳动与抖动)
    /// </summary>
    public class BinanceFuturesMarketDataService : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly BinanceSocketClient _socketClient;

        private readonly ConcurrentDictionary<string, UpdateSubscription> _subscriptions = new();
        private readonly List<string> _cachedSymbols = new();
        private readonly SemaphoreSlim _symbolLock = new(1, 1);

        public event Action<string, bool>? OnStatusLog; // (msg, isError)

        public BinanceFuturesMarketDataService()
        {
            _restClient = new BinanceRestClient();
            _socketClient = new BinanceSocketClient();
        }

        /// <summary>
        /// 异步获取币安所有活跃的 USDT 本位永续合约交易对列表
        /// </summary>
        public async Task<List<string>> GetUsdtFuturesSymbolsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cachedSymbols.Count > 0)
            {
                return new List<string>(_cachedSymbols);
            }

            await _symbolLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && _cachedSymbols.Count > 0)
                {
                    return new List<string>(_cachedSymbols);
                }

                OnStatusLog?.Invoke("正在从币安合约接口拉取全部 USDT 永续合约交易对列表...", false);

                var exchangeInfo = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct).ConfigureAwait(false);
                if (!exchangeInfo.Success || exchangeInfo.Data == null)
                {
                    OnStatusLog?.Invoke($"拉取合约交易对失败: {exchangeInfo.Error?.Message}", true);
                    return GetFallbackSymbols();
                }

                var list = exchangeInfo.Data.Symbols
                    .Where(s => s.QuoteAsset.Equals("USDT", StringComparison.OrdinalIgnoreCase) &&
                                s.Status == SymbolStatus.Trading &&
                                s.ContractType == ContractType.Perpetual)
                    .Select(s => s.Name.ToUpperInvariant())
                    .OrderBy(s => s)
                    .ToList();

                _cachedSymbols.Clear();
                _cachedSymbols.AddRange(list);

                OnStatusLog?.Invoke($"✅ 成功获取 {_cachedSymbols.Count} 个活跃 USDT 永续合约交易对！", false);
                return new List<string>(_cachedSymbols);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"拉取合约列表异常: {ex.Message}", true);
                return GetFallbackSymbols();
            }
            finally
            {
                _symbolLock.Release();
            }
        }

        private static List<string> GetFallbackSymbols()
        {
            return new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "DOGEUSDT", "BNBUSDT", "XRPUSDT", "HEMIUSDT", "1000PEPEUSDT" };
        }

        /// <summary>
        /// 获取指定合约与周期的历史行情数据 (支持 Tick 逐笔、1s 秒级与各标准周期 K 线)
        /// </summary>
        public async Task<List<RawKline>> GetHistoricalKlinesAsync(string symbol, ChartInterval interval, int limit = 500, CancellationToken ct = default)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            limit = Math.Clamp(limit, 10, 1500);

            // 1. Tick 逐笔周期：拉取最近 1000 笔真实逐笔成交
            if (interval == ChartInterval.Tick)
            {
                return await GetHistoricalTicksAsync(symbol, limit, ct).ConfigureAwait(false);
            }

            // 2. 1s 周期：拉取最近成交并在本地聚合为 1 秒 K 线
            if (interval == ChartInterval.OneSecond)
            {
                return await GetHistorical1sKlinesFromAggTradesAsync(symbol, ct).ConfigureAwait(false);
            }

            // 3. 标准 K 线周期 (1m, 3m, 5m, 15m, 30m, 1h, 4h, 1d)
            try
            {
                var klineInterval = interval.ToKlineInterval();
                var klineResult = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    klineInterval,
                    limit: limit,
                    ct: ct).ConfigureAwait(false);

                if (!klineResult.Success || klineResult.Data == null)
                {
                    OnStatusLog?.Invoke($"[{symbol} {interval.ToDisplayString()}] 加载历史 K 线失败: {klineResult.Error?.Message}", true);
                    return new List<RawKline>();
                }

                long intervalMs = TimeHelper.GetIntervalMilliseconds(interval.ToDisplayString());

                var list = new List<RawKline>(klineResult.Data.Count());
                foreach (var k in klineResult.Data)
                {
                    long openTimeMs = ((DateTimeOffset)DateTime.SpecifyKind(k.OpenTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    openTimeMs = (openTimeMs / intervalMs) * intervalMs;

                    list.Add(new RawKline
                    {
                        OpenTime = openTimeMs,
                        Open = k.OpenPrice,
                        High = k.HighPrice,
                        Low = k.LowPrice,
                        Close = k.ClosePrice,
                        Volume = k.Volume,
                        CloseTime = openTimeMs + intervalMs - 1,
                        QuoteVolume = k.QuoteVolume,
                        TradeCount = k.TradeCount,
                        TakerBuyVolume = k.TakerBuyBaseVolume,
                        TakerBuyQuoteVolume = k.TakerBuyQuoteVolume
                    });
                }

                return list;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"[{symbol} {interval.ToDisplayString()}] 历史 K 线加载异常: {ex.Message}", true);
                return new List<RawKline>();
            }
        }

        private async Task<List<RawKline>> GetHistoricalTicksAsync(string symbol, int limit, CancellationToken ct)
        {
            try
            {
                OnStatusLog?.Invoke($"[{symbol} Tick] 正在拉取最近 {limit} 笔实盘逐笔成交...", false);
                var tradesResult = await _restClient.UsdFuturesApi.ExchangeData.GetRecentTradesAsync(symbol, limit: Math.Min(limit, 1000), ct: ct).ConfigureAwait(false);
                if (!tradesResult.Success || tradesResult.Data == null || !tradesResult.Data.Any())
                {
                    OnStatusLog?.Invoke($"[{symbol} Tick] 逐笔交易数据为空", true);
                    return new List<RawKline>();
                }

                var list = new List<RawKline>(tradesResult.Data.Count());
                foreach (var t in tradesResult.Data)
                {
                    long tMs = ((DateTimeOffset)DateTime.SpecifyKind(t.TradeTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    list.Add(new RawKline
                    {
                        OpenTime = tMs,
                        CloseTime = tMs,
                        Open = t.Price,
                        High = t.Price,
                        Low = t.Price,
                        Close = t.Price,
                        Volume = t.BaseQuantity,
                        QuoteVolume = t.Price * t.BaseQuantity,
                        TradeCount = 1,
                        TakerBuyVolume = !t.BuyerIsMaker ? t.BaseQuantity : 0m,
                        TakerBuyQuoteVolume = !t.BuyerIsMaker ? t.Price * t.BaseQuantity : 0m
                    });
                }

                OnStatusLog?.Invoke($"[{symbol} Tick] 成功载入 {list.Count} 笔逐笔 Tick 数据！", false);
                return list;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"[{symbol} Tick] 历史逐笔数据加载异常: {ex.Message}", true);
                return new List<RawKline>();
            }
        }

        private async Task<List<RawKline>> GetHistorical1sKlinesFromAggTradesAsync(string symbol, CancellationToken ct)
        {
            try
            {
                OnStatusLog?.Invoke($"[{symbol} 1s] 正在拉取最近 1000 笔成交以秒级聚合 1s K线...", false);
                var aggTradesResult = await _restClient.UsdFuturesApi.ExchangeData.GetRecentTradesAsync(symbol, limit: 1000, ct: ct).ConfigureAwait(false);
                if (!aggTradesResult.Success || aggTradesResult.Data == null || !aggTradesResult.Data.Any())
                {
                    OnStatusLog?.Invoke($"[{symbol} 1s] 聚合交易数据为空", true);
                    return new List<RawKline>();
                }

                var grouped = aggTradesResult.Data
                    .GroupBy(t => ((DateTimeOffset)DateTime.SpecifyKind(t.TradeTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000L * 1000L)
                    .OrderBy(g => g.Key)
                    .ToList();

                var list = new List<RawKline>(grouped.Count);
                foreach (var g in grouped)
                {
                    var trades = g.ToList();
                    decimal open = trades[0].Price;
                    decimal high = trades.Max(t => t.Price);
                    decimal low = trades.Min(t => t.Price);
                    decimal close = trades[^1].Price;
                    decimal vol = trades.Sum(t => t.BaseQuantity);
                    decimal quoteVol = trades.Sum(t => t.Price * t.BaseQuantity);
                    decimal takerBuyVol = trades.Where(t => !t.BuyerIsMaker).Sum(t => t.BaseQuantity);
                    decimal takerBuyQuoteVol = trades.Where(t => !t.BuyerIsMaker).Sum(t => t.Price * t.BaseQuantity);

                    list.Add(new RawKline
                    {
                        OpenTime = g.Key,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = vol,
                        CloseTime = g.Key + 999,
                        QuoteVolume = quoteVol,
                        TradeCount = trades.Count,
                        TakerBuyVolume = takerBuyVol,
                        TakerBuyQuoteVolume = takerBuyQuoteVol
                    });
                }

                OnStatusLog?.Invoke($"[{symbol} 1s] 成功聚合生成 {list.Count} 根 1秒 K线！", false);
                return list;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"[{symbol} 1s] 历史秒级聚合异常: {ex.Message}", true);
                return new List<RawKline>();
            }
        }

        /// <summary>
        /// 订阅指定合约与周期的 WebSocket 实时行情推送 (逐笔实时驱动所有周期 K 线即时跳动与抖动)
        /// </summary>
        public async Task<bool> SubscribeKlineUpdatesAsync(string symbol, ChartInterval interval, Action<RawKline> onKlineUpdate, CancellationToken ct = default)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            string subKey = $"{symbol}_{interval.ToDisplayString()}";

            await UnsubscribeKlineUpdatesAsync(symbol, interval).ConfigureAwait(false);

            // 1. Tick 逐笔实时推送
            if (interval == ChartInterval.Tick)
            {
                return await SubscribeTickStreamAsync(symbol, onKlineUpdate, ct).ConfigureAwait(false);
            }

            // 2. 针对 1s, 1m, 3m, 5m, 15m, 30m, 1h, 4h, 1d 所有周期：
            // 采用逐笔成交流实时驱动 K 线，确保每一笔成交都实时带动当前 K 线的价格跳动、影线延伸与实时抖动！
            return await SubscribeTradeDrivenKlineAsync(symbol, interval, onKlineUpdate, ct).ConfigureAwait(false);
        }

        private async Task<bool> SubscribeTickStreamAsync(string symbol, Action<RawKline> onKlineUpdate, CancellationToken ct)
        {
            string subKey = $"{symbol}_Tick";
            try
            {
                var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                    symbol,
                    data =>
                    {
                        var t = data.Data;
                        long tMs = ((DateTimeOffset)DateTime.SpecifyKind(t.TradeTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                        var rawTickKline = new RawKline
                        {
                            OpenTime = tMs,
                            CloseTime = tMs,
                            Open = t.Price,
                            High = t.Price,
                            Low = t.Price,
                            Close = t.Price,
                            Volume = t.Quantity,
                            QuoteVolume = t.Price * t.Quantity,
                            TradeCount = 1,
                            TakerBuyVolume = !t.BuyerIsMaker ? t.Quantity : 0m,
                            TakerBuyQuoteVolume = !t.BuyerIsMaker ? t.Price * t.Quantity : 0m
                        };
                        onKlineUpdate(rawTickKline);
                    },
                    ct: ct).ConfigureAwait(false);

                if (!subResult.Success)
                {
                    OnStatusLog?.Invoke($"[{subKey}] 逐笔 Tick WebSocket 订阅失败: {subResult.Error?.Message}", true);
                    return false;
                }

                _subscriptions[subKey] = subResult.Data;
                OnStatusLog?.Invoke($"🟢 [{subKey}] 已成功建立实时逐笔 Tick 数据流", false);
                return true;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"[{subKey}] 逐笔 Tick 订阅异常: {ex.Message}", true);
                return false;
            }
        }

        private async Task<bool> SubscribeTradeDrivenKlineAsync(string symbol, ChartInterval interval, Action<RawKline> onKlineUpdate, CancellationToken ct)
        {
            string subKey = $"{symbol}_{interval.ToDisplayString()}";
            long intervalMs = TimeHelper.GetIntervalMilliseconds(interval.ToDisplayString());

            RawKline currentBar = default;
            object barLock = new();

            try
            {
                var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                    symbol,
                    data =>
                    {
                        var trade = data.Data;
                        long tradeTimeMs = ((DateTimeOffset)DateTime.SpecifyKind(trade.TradeTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                        long barOpenTime = (tradeTimeMs / intervalMs) * intervalMs;
                        long barCloseTime = barOpenTime + intervalMs - 1;

                        RawKline updatedBar;
                        lock (barLock)
                        {
                            if (currentBar.OpenTime == barOpenTime)
                            {
                                currentBar = new RawKline
                                {
                                    OpenTime = barOpenTime,
                                    CloseTime = barCloseTime,
                                    Open = currentBar.Open > 0 ? currentBar.Open : trade.Price,
                                    High = Math.Max(currentBar.High, trade.Price),
                                    Low = currentBar.Low > 0 ? Math.Min(currentBar.Low, trade.Price) : trade.Price,
                                    Close = trade.Price,
                                    Volume = currentBar.Volume + trade.Quantity,
                                    QuoteVolume = currentBar.QuoteVolume + trade.Price * trade.Quantity,
                                    TradeCount = currentBar.TradeCount + 1,
                                    TakerBuyVolume = currentBar.TakerBuyVolume + (!trade.BuyerIsMaker ? trade.Quantity : 0m),
                                    TakerBuyQuoteVolume = currentBar.TakerBuyQuoteVolume + (!trade.BuyerIsMaker ? trade.Price * trade.Quantity : 0m)
                                };
                            }
                            else
                            {
                                currentBar = new RawKline
                                {
                                    OpenTime = barOpenTime,
                                    CloseTime = barCloseTime,
                                    Open = trade.Price,
                                    High = trade.Price,
                                    Low = trade.Price,
                                    Close = trade.Price,
                                    Volume = trade.Quantity,
                                    QuoteVolume = trade.Price * trade.Quantity,
                                    TradeCount = 1,
                                    TakerBuyVolume = !trade.BuyerIsMaker ? trade.Quantity : 0m,
                                    TakerBuyQuoteVolume = !trade.BuyerIsMaker ? trade.Price * trade.Quantity : 0m
                                };
                            }
                            updatedBar = currentBar;
                        }

                        onKlineUpdate(updatedBar);
                    },
                    ct: ct).ConfigureAwait(false);

                if (!subResult.Success)
                {
                    OnStatusLog?.Invoke($"[{subKey}] 实盘逐笔驱动订阅失败: {subResult.Error?.Message}", true);
                    return false;
                }

                _subscriptions[subKey] = subResult.Data;
                OnStatusLog?.Invoke($"🟢 [{subKey}] 已接入逐笔驱动实时 K 线跳动行情流", false);
                return true;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"[{subKey}] 行情流订阅异常: {ex.Message}", true);
                return false;
            }
        }

        /// <summary>
        /// 取消订阅指定合约与周期的 WebSocket 推送
        /// </summary>
        public async Task UnsubscribeKlineUpdatesAsync(string symbol, ChartInterval interval)
        {
            string subKey = $"{symbol.Trim().ToUpperInvariant()}_{interval.ToDisplayString()}";
            if (_subscriptions.TryRemove(subKey, out var sub))
            {
                try
                {
                    await sub.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略关闭异常
                }
            }
        }

        public void Dispose()
        {
            foreach (var kvp in _subscriptions)
            {
                _ = kvp.Value.CloseAsync();
            }
            _subscriptions.Clear();

            _restClient.Dispose();
            _socketClient.Dispose();
            _symbolLock.Dispose();
        }
    }
}
