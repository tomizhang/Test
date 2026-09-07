using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Test.MultiChart.WinForms.Models;

namespace Test.MultiChart.WinForms.Services
{
    /// <summary>
    /// 跨窗口绘图同步管理与事件广播中心 (单例/中心化服务)
    /// </summary>
    public class DrawingSyncService
    {
        private static readonly Lazy<DrawingSyncService> _instance = new(() => new DrawingSyncService());
        public static DrawingSyncService Instance => _instance.Value;

        // 存储字典：Symbol -> List<DrawingItem>
        private readonly ConcurrentDictionary<string, List<DrawingItem>> _drawingsBySymbol = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public bool AutoSyncEnabled { get; set; } = true;
        public bool CrosshairSyncEnabled { get; set; } = true;

        public event Action<DrawingItem, object?>? OnDrawingAdded;
        public event Action<DrawingItem, object?>? OnDrawingUpdated;
        public event Action<string, string, object?>? OnDrawingDeleted; // (id, symbol, sender)
        public event Action<string, object?>? OnDrawingsCleared;        // (symbol, sender)
        public event Action<string, long, double, object?>? OnCrosshairMoved; // (symbol, time, price, sender)

        private DrawingSyncService() { }

        public List<DrawingItem> GetDrawings(string symbol)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            lock (_lock)
            {
                if (_drawingsBySymbol.TryGetValue(symbol, out var list))
                {
                    return list.Select(d => d.Clone()).ToList();
                }
                return new List<DrawingItem>();
            }
        }

        public void AddDrawing(DrawingItem item, object? sender = null)
        {
            if (item == null) return;
            string symbol = item.Symbol.Trim().ToUpperInvariant();

            lock (_lock)
            {
                if (!_drawingsBySymbol.TryGetValue(symbol, out var list))
                {
                    list = new List<DrawingItem>();
                    _drawingsBySymbol[symbol] = list;
                }

                list.RemoveAll(d => d.Id == item.Id);
                list.Add(item.Clone());
            }

            if (AutoSyncEnabled)
            {
                OnDrawingAdded?.Invoke(item, sender);
            }
        }

        public void UpdateDrawing(DrawingItem item, object? sender = null)
        {
            if (item == null) return;
            string symbol = item.Symbol.Trim().ToUpperInvariant();

            lock (_lock)
            {
                if (_drawingsBySymbol.TryGetValue(symbol, out var list))
                {
                    int idx = list.FindIndex(d => d.Id == item.Id);
                    if (idx >= 0)
                    {
                        list[idx] = item.Clone();
                    }
                    else
                    {
                        list.Add(item.Clone());
                    }
                }
            }

            if (AutoSyncEnabled)
            {
                OnDrawingUpdated?.Invoke(item, sender);
            }
        }

        public void DeleteDrawing(string id, string symbol, object? sender = null)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            lock (_lock)
            {
                if (_drawingsBySymbol.TryGetValue(symbol, out var list))
                {
                    list.RemoveAll(d => d.Id == id);
                }
            }

            if (AutoSyncEnabled)
            {
                OnDrawingDeleted?.Invoke(id, symbol, sender);
            }
        }

        public void ClearDrawings(string symbol, object? sender = null)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            lock (_lock)
            {
                if (_drawingsBySymbol.TryGetValue(symbol, out var list))
                {
                    list.Clear();
                }
            }

            if (AutoSyncEnabled)
            {
                OnDrawingsCleared?.Invoke(symbol, sender);
            }
        }

        public void BroadcastCrosshair(string symbol, long timestamp, double price, object? sender = null)
        {
            if (!CrosshairSyncEnabled) return;
            OnCrosshairMoved?.Invoke(symbol, timestamp, price, sender);
        }
    }
}
