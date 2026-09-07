using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Test.MultiChart.WinForms.Models;

namespace Test.MultiChart.WinForms.Services
{
    /// <summary>
    /// 多窗口工作台界面配置与分屏记忆持久化管理器 (保存于 MultiChartSettings.json)
    /// </summary>
    public static class MultiChartSettingsManager
    {
        private static readonly string SettingsFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "MultiChartSettings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static MultiChartSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<MultiChartSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // 忽略异常，回退默认配置
            }

            return new MultiChartSettings();
        }

        public static void Save(MultiChartSettings settings)
        {
            if (settings == null) return;
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // 忽略保存异常
            }
        }
    }
}
