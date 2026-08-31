using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;

namespace Test.PercentageBar.WinForms.Helper
{
    /// <summary>
    /// 可忽略自动配置保存特性的标记特性
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ConfigIgnoreAttribute : Attribute
    {
    }

    /// <summary>
    /// 基于反射的 WinForms 界面配置自动持久化引擎：
    /// 1. 自动利用反射扫描 Form 中所有输入控件 (CheckBox, NumericUpDown, ComboBox, TextBox, DateTimePicker, RadioButton 等)
    /// 2. 后续无论新增任何控件，无需编写任何额外配置代码，均由反射自动识别、自动持久化至 JSON 并自动回填恢复
    /// 3. 支持容错保护与类型安全转换
    /// </summary>
    public static class FormConfigHelper
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string GetDefaultConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "percent_bar_config.json");
        }

        /// <summary>
        /// 利用反射将 Form 的全部可配置控件状态保存到 JSON 文件
        /// </summary>
        public static bool SaveFormConfig(Form form, string? customPath = null)
        {
            if (form == null) return false;

            try
            {
                string filePath = customPath ?? GetDefaultConfigPath();
                var configDict = new Dictionary<string, object>();

                // 1. 反射获取 Form 上的所有字段 (包括 private/protected/public)
                var fields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                foreach (var field in fields)
                {
                    // 检查是否标记为忽略
                    if (field.GetCustomAttribute<ConfigIgnoreAttribute>() != null)
                        continue;

                    object? val = field.GetValue(form);
                    if (val is Control ctrl)
                    {
                        string key = field.Name;
                        if (string.IsNullOrWhiteSpace(key)) continue;

                        switch (ctrl)
                        {
                            case CheckBox chk:
                                configDict[key] = chk.Checked;
                                break;

                            case NumericUpDown num:
                                configDict[key] = num.Value;
                                break;

                            case ComboBox cbo:
                                configDict[key] = new ComboBoxState
                                {
                                    SelectedIndex = cbo.SelectedIndex,
                                    Text = cbo.Text
                                };
                                break;

                            case TextBox txt:
                                configDict[key] = txt.Text;
                                break;

                            case DateTimePicker dtp:
                                configDict[key] = dtp.Value.ToString("yyyy-MM-dd HH:mm:ss");
                                break;

                            case RadioButton rdo:
                                configDict[key] = rdo.Checked;
                                break;

                            case TrackBar trk:
                                configDict[key] = trk.Value;
                                break;
                        }
                    }
                }

                // 2. 写入 JSON 文件
                string json = JsonSerializer.Serialize(configDict, JsonOptions);
                File.WriteAllText(filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FormConfigHelper] 保存配置异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 利用反射从 JSON 文件读取并自动回填恢复 Form 的全部控件状态
        /// </summary>
        public static bool LoadFormConfig(Form form, string? customPath = null)
        {
            if (form == null) return false;

            try
            {
                string filePath = customPath ?? GetDefaultConfigPath();
                if (!File.Exists(filePath)) return false;

                string json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return false;

                var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (configDict == null || configDict.Count == 0) return false;

                // 1. 反射获取 Form 上的所有字段
                var fields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<ConfigIgnoreAttribute>() != null)
                        continue;

                    string key = field.Name;
                    if (!configDict.TryGetValue(key, out JsonElement element))
                        continue;

                    object? ctrlObj = field.GetValue(form);
                    if (ctrlObj is Control ctrl)
                    {
                        try
                        {
                            switch (ctrl)
                            {
                                case CheckBox chk when element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False:
                                    chk.Checked = element.GetBoolean();
                                    break;

                                case NumericUpDown num when element.ValueKind == JsonValueKind.Number:
                                    decimal numVal = element.GetDecimal();
                                    if (numVal >= num.Minimum && numVal <= num.Maximum)
                                    {
                                        num.Value = numVal;
                                    }
                                    break;

                                case ComboBox cbo:
                                    if (element.ValueKind == JsonValueKind.Object)
                                    {
                                        var state = JsonSerializer.Deserialize<ComboBoxState>(element.GetRawText());
                                        if (state != null)
                                        {
                                            if (state.SelectedIndex >= 0 && state.SelectedIndex < cbo.Items.Count)
                                            {
                                                cbo.SelectedIndex = state.SelectedIndex;
                                            }
                                            else if (!string.IsNullOrEmpty(state.Text))
                                            {
                                                int idx = cbo.FindStringExact(state.Text);
                                                if (idx >= 0) cbo.SelectedIndex = idx;
                                                else cbo.Text = state.Text;
                                            }
                                        }
                                    }
                                    else if (element.ValueKind == JsonValueKind.Number)
                                    {
                                        int idx = element.GetInt32();
                                        if (idx >= 0 && idx < cbo.Items.Count) cbo.SelectedIndex = idx;
                                    }
                                    else if (element.ValueKind == JsonValueKind.String)
                                    {
                                        string? textVal = element.GetString();
                                        if (!string.IsNullOrEmpty(textVal))
                                        {
                                            int idx = cbo.FindStringExact(textVal);
                                            if (idx >= 0) cbo.SelectedIndex = idx;
                                            else cbo.Text = textVal;
                                        }
                                    }
                                    break;

                                case TextBox txt when element.ValueKind == JsonValueKind.String:
                                    txt.Text = element.GetString() ?? string.Empty;
                                    break;

                                case DateTimePicker dtp when element.ValueKind == JsonValueKind.String:
                                    if (DateTime.TryParse(element.GetString(), out DateTime dt))
                                    {
                                        dtp.Value = dt;
                                    }
                                    break;

                                case RadioButton rdo when element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False:
                                    rdo.Checked = element.GetBoolean();
                                    break;

                                case TrackBar trk when element.ValueKind == JsonValueKind.Number:
                                    int trkVal = element.GetInt32();
                                    if (trkVal >= trk.Minimum && trkVal <= trk.Maximum)
                                    {
                                        trk.Value = trkVal;
                                    }
                                    break;
                            }
                        }
                        catch
                        {
                            // 单个控件解析容错，不阻断其他控件恢复
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FormConfigHelper] 加载配置异常: {ex.Message}");
                return false;
            }
        }

        private class ComboBoxState
        {
            public int SelectedIndex { get; set; } = -1;
            public string? Text { get; set; }
        }
    }
}
