using Common;
using System;
using System.IO;
using System.Text.Json;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    /// <summary>
    /// 参数配置自动持久化管理器 (自动读取与保存 channel_playback_settings.json)
    /// </summary>
    public static class ChannelPlaybackSettingsManager
    {
        private static readonly string SettingsFileName = "channel_playback_settings.json";

        public static string GetSettingsFilePath()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, SettingsFileName);
            }
            catch
            {
                return SettingsFileName;
            }
        }

        /// <summary>
        /// 从本地加载配置 (若不存在或解析失败则返回默认值)
        /// </summary>
        public static ChannelPlaybackSettings Load()
        {
            string path = GetSettingsFilePath();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<ChannelPlaybackSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChannelPlaybackSettingsManager] 加载配置异常: {ex.Message}，使用默认配置。");
            }

            return new ChannelPlaybackSettings();
        }

        /// <summary>
        /// 将当前配置保存到本地 JSON 文件
        /// </summary>
        public static void Save(ChannelPlaybackSettings settings)
        {
            if (settings == null) return;

            string path = GetSettingsFilePath();
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChannelPlaybackSettingsManager] 保存配置异常: {ex.Message}");
            }
        }
    }
}
