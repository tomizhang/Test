using Binance.Net.Clients;
using Binance.Net.Objects.Models.Spot;
using Binance.Net.Objects.Models.Futures;
using Common;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets;
using System;
using System.Threading;
using System.Threading.Tasks;
using Test.TickPlayback.WinForms.Models;

namespace Test.TickPlayback.WinForms.Services
{
    /// <summary>
    /// 币安实盘 WebSocket 实时逐笔成交流式订阅服务 (支持现货与 USDT 永续合约)
    /// </summary>
    public class LiveTickStreamService : IDisposable
    {
        private BinanceSocketClient? _socketClient;
        private UpdateSubscription? _currentSubscription;
        private readonly object _lock = new();

        public bool IsConnected { get; private set; }
        public string CurrentSymbol { get; private set; } = string.Empty;
        public MarketType CurrentMarket { get; private set; } = MarketType.Spot;

        public event Action<RawTick>? OnTickReceived;
        public event Action<string, bool>? OnStatusChanged; // (message, isError)

        public async Task<bool> StartStreamingAsync(string symbol, MarketType market, CancellationToken ct = default)
        {
            await StopStreamingAsync();

            symbol = symbol.Trim().ToUpperInvariant();
            CurrentSymbol = symbol;
            CurrentMarket = market;

            try
            {
                OnStatusChanged?.Invoke($"正在连接币安 {(market == MarketType.Spot ? "现货" : "USDT合约")} WebSocket 行情流: {symbol}...", false);

                _socketClient = new BinanceSocketClient();

                CallResult<UpdateSubscription> subResult;

                if (market == MarketType.Spot)
                {
                    // 订阅现货逐笔成交
                    subResult = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                        symbol,
                        data =>
                        {
                            var trade = data.Data;
                            var rawTick = new RawTick
                            {
                                TradeId = trade.Id,
                                Price = trade.Price,
                                Qty = trade.Quantity,
                                QuoteQty = trade.Price * trade.Quantity,
                                Time = ((DateTimeOffset)trade.TradeTime).ToUnixTimeMilliseconds(),
                                IsBuyerMaker = trade.BuyerIsMaker,
                                IsBestMatch = true
                            };
                            OnTickReceived?.Invoke(rawTick);
                        },
                        ct).ConfigureAwait(false);
                }
                else
                {
                    // 订阅 USDT-M 永续合约逐笔成交
                    subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                        symbol,
                        data =>
                        {
                            var trade = data.Data;
                            var rawTick = new RawTick
                            {
                                TradeId = trade.Id,
                                Price = trade.Price,
                                Qty = trade.Quantity,
                                QuoteQty = trade.Price * trade.Quantity,
                                Time = ((DateTimeOffset)trade.TradeTime).ToUnixTimeMilliseconds(),
                                IsBuyerMaker = trade.BuyerIsMaker,
                                IsBestMatch = true
                            };
                            OnTickReceived?.Invoke(rawTick);
                        },
                        ct: ct).ConfigureAwait(false);
                }

                if (!subResult.Success)
                {
                    IsConnected = false;
                    OnStatusChanged?.Invoke($"WebSocket 订阅失败: {subResult.Error?.Message}", true);
                    return false;
                }

                _currentSubscription = subResult.Data;
                _currentSubscription.ConnectionLost += () =>
                {
                    IsConnected = false;
                    OnStatusChanged?.Invoke("⚠️ WebSocket 连接丢失，正在尝试自动重连...", true);
                };
                _currentSubscription.ConnectionRestored += time =>
                {
                    IsConnected = true;
                    OnStatusChanged?.Invoke($"✅ WebSocket 已恢复连接 ({time.TotalSeconds:F1}s)", false);
                };

                IsConnected = true;
                OnStatusChanged?.Invoke($"🟢 已成功接入币安实盘实时 Tick 数据流: {symbol} ({(market == MarketType.Spot ? "现货" : "USDT-M 永续")})", false);
                return true;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                OnStatusChanged?.Invoke($"实盘行情连接异常: {ex.Message}", true);
                return false;
            }
        }

        public async Task StopStreamingAsync()
        {
            lock (_lock)
            {
                IsConnected = false;
            }

            if (_currentSubscription != null)
            {
                try
                {
                    await _currentSubscription.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略关闭异常
                }
                _currentSubscription = null;
            }

            if (_socketClient != null)
            {
                try
                {
                    _socketClient.Dispose();
                }
                catch
                {
                    // 忽略释放异常
                }
                _socketClient = null;
            }

            OnStatusChanged?.Invoke("⏹ 实盘行情已断开连接", false);
        }

        public void Dispose()
        {
            _ = StopStreamingAsync();
        }
    }
}
