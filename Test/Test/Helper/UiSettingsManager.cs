using Common.Models;
using System;
using System.IO;
using System.Text.Json;

namespace Common.Helper
{
    /// <summary>
    /// UI 界面配置持久化管理器 (自动读取与保存 ui_settings.json)
    /// </summary>
    public static class UiSettingsManager
    {
        private static readonly string SettingsFileName = "ui_settings.json";

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
        /// 从本地文件加载界面配置 (若不存在或解析失败则返回默认值)
        /// </summary>
        public static UiSettings Load()
        {
            string path = GetSettingsFilePath();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<UiSettings>(json);
                    if (settings != null)
                    {
                        Logger.Log($"[UiSettingsManager] 已成功从 {path} 恢复界面配置。");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[UiSettingsManager] 读取配置异常: {ex.Message}，将使用默认配置。");
            }

            return new UiSettings();
        }

        /// <summary>
        /// 将当前界面配置持久化保存到本地 JSON 文件
        /// </summary>
        public static void Save(UiSettings settings)
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
                Logger.Log($"[UiSettingsManager] 界面配置已成功持久化至 {path}。");
            }
            catch (Exception ex)
            {
                Logger.Log($"[UiSettingsManager] 保存配置异常: {ex.Message}");
            }
        }
    }
}
