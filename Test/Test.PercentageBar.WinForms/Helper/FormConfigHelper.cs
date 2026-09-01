using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
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
    /// 2. 多路径双重容灾持久化 (AppData 永久目录 + AppBase 本地目录)，项目重新编译/Clean 绝不丢失配置
    /// 3. 支持实时变更事件绑定 (BindAutoSave)，修改即存，无惧异常关闭或调试停止
    /// 4. 多轮次依赖感知恢复 (先 ComboBox 模式、后 NumericUpDown/CheckBox)，彻底杜绝级联重置覆盖
    /// 5. 零配置侵入：后续增加任何控件，无需编写任何保存/加载代码，自动完全接管！
    /// </summary>
    public static class FormConfigHelper
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static bool _isLoading = false;
        private static System.Threading.Timer? _debounceTimer;
        private static Form? _activeForm;
        private static readonly object _lock = new object();

        /// <summary>
        /// 获取主配置文件路径 (优先 AppData 永久存储，防止 dotnet clean / rebuild 清空 bin 目录导致配置丢失)
        /// </summary>
        public static string GetPrimaryConfigPath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "PercentageBarBacktest");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return Path.Combine(dir, "percent_bar_config.json");
            }
            catch
            {
                return GetFallbackConfigPath();
            }
        }

        /// <summary>
        /// 获取本地运行目录备用配置文件路径
        /// </summary>
        public static string GetFallbackConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "percent_bar_config.json");
        }

        /// <summary>
        /// 利用反射将 Form 的全部可配置控件状态保存到 JSON 文件 (同时同步到 AppData 与本地目录)
        /// </summary>
        public static bool SaveFormConfig(Form form, string? customPath = null)
        {
            if (form == null || _isLoading) return false;

            try
            {
                var configDict = new Dictionary<string, object>();

                // 1. 反射获取 Form 上的所有字段 (包括 private/protected/public)
                var fields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                foreach (var field in fields)
                {
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

                string json = JsonSerializer.Serialize(configDict, JsonOptions);

                if (!string.IsNullOrEmpty(customPath))
                {
                    File.WriteAllText(customPath, json);
                }
                else
                {
                    // 同时写入 AppData 永久路径与本地路径
                    string primaryPath = GetPrimaryConfigPath();
                    File.WriteAllText(primaryPath, json);

                    try
                    {
                        string fallbackPath = GetFallbackConfigPath();
                        File.WriteAllText(fallbackPath, json);
                    }
                    catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FormConfigHelper] 保存配置异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 利用反射从 JSON 文件读取并自动回填恢复 Form 的全部控件状态 (多轮次依赖保护)
        /// </summary>
        public static bool LoadFormConfig(Form form, string? customPath = null)
        {
            if (form == null) return false;

            _isLoading = true;
            try
            {
                string filePath = customPath ?? string.Empty;
                if (string.IsNullOrEmpty(filePath))
                {
                    string primary = GetPrimaryConfigPath();
                    string fallback = GetFallbackConfigPath();
                    if (File.Exists(primary)) filePath = primary;
                    else if (File.Exists(fallback)) filePath = fallback;
                    else return false;
                }

                if (!File.Exists(filePath)) return false;

                string json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return false;

                var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (configDict == null || configDict.Count == 0) return false;

                var fields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                // =========================================================================
                // 第 1 轮：优先恢复基础 ComboBox (如切分方式 cboSliceUnit, 交易对 cboCoin 等)
                // 这样能先建立正确的模式和 NumericUpDown 上下限，避免后续数值被事件级联冲刷
                // =========================================================================
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
                    string key = field.Name;
                    if (!configDict.TryGetValue(key, out JsonElement element)) continue;

                    object? ctrlObj = field.GetValue(form);
                    if (ctrlObj is ComboBox cbo)
                    {
                        RestoreComboBox(cbo, element);
                    }
                }

                // =========================================================================
                // 第 2 轮：恢复全部 NumericUpDown, CheckBox, DateTimePicker, TextBox
                // 此时 NumericUpDown 的 Min/Max 已由第 1 轮正确设置，恢复不会越界
                // =========================================================================
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
                    string key = field.Name;
                    if (!configDict.TryGetValue(key, out JsonElement element)) continue;

                    object? ctrlObj = field.GetValue(form);
                    if (ctrlObj is Control ctrl && !(ctrl is ComboBox))
                    {
                        RestoreNonComboControl(ctrl, element);
                    }
                }

                // =========================================================================
                // 第 3 轮：再次校准从属 ComboBox (如 cboBarMode / cboPlaybackSpeed)
                // 确保没有因第 1 轮事件重新生成选项而失真
                // =========================================================================
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
                    string key = field.Name;
                    if (!configDict.TryGetValue(key, out JsonElement element)) continue;

                    object? ctrlObj = field.GetValue(form);
                    if (ctrlObj is ComboBox cbo)
                    {
                        RestoreComboBox(cbo, element);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FormConfigHelper] 加载配置异常: {ex.Message}");
                return false;
            }
            finally
            {
                _isLoading = false;
            }
        }

        private static void RestoreComboBox(ComboBox cbo, JsonElement element)
        {
            try
            {
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
            }
            catch { }
        }

        private static void RestoreNonComboControl(Control ctrl, JsonElement element)
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
                        else if (numVal < num.Minimum)
                        {
                            num.Value = num.Minimum;
                        }
                        else if (numVal > num.Maximum)
                        {
                            num.Value = num.Maximum;
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
            catch { }
        }

        /// <summary>
        /// 🚀 自动绑定全窗体控件变更监听 (修改任意控件即时防抖自动保存至磁盘，无须手动保存，无惧意外退出)
        /// </summary>
        public static void BindAutoSave(Form form)
        {
            if (form == null) return;
            _activeForm = form;

            // 1. 反射抓取所有输入控件字段
            var fields = form.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
                object? val = field.GetValue(form);
                if (val is Control ctrl)
                {
                    AttachControlChangeEvents(ctrl);
                }
            }

            // 2. 递归遍历 Form 的全部控件层级作为双保险
            AttachHierarchyChangeEvents(form);
        }

        private static void AttachHierarchyChangeEvents(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                AttachControlChangeEvents(child);
                if (child.HasChildren)
                {
                    AttachHierarchyChangeEvents(child);
                }
            }
        }

        private static void AttachControlChangeEvents(Control ctrl)
        {
            switch (ctrl)
            {
                case CheckBox chk:
                    chk.CheckedChanged += (s, e) => TriggerDebouncedSave();
                    break;
                case NumericUpDown num:
                    num.ValueChanged += (s, e) => TriggerDebouncedSave();
                    break;
                case ComboBox cbo:
                    cbo.SelectedIndexChanged += (s, e) => TriggerDebouncedSave();
                    break;
                case DateTimePicker dtp:
                    dtp.ValueChanged += (s, e) => TriggerDebouncedSave();
                    break;
                case TextBox txt:
                    txt.TextChanged += (s, e) => TriggerDebouncedSave();
                    break;
                case RadioButton rdo:
                    rdo.CheckedChanged += (s, e) => TriggerDebouncedSave();
                    break;
            }
        }

        private static void TriggerDebouncedSave()
        {
            if (_isLoading || _activeForm == null) return;

            lock (_lock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (_activeForm != null && !_activeForm.IsDisposed)
                        {
                            SaveFormConfig(_activeForm);
                        }
                    }
                    catch { }
                }, null, 300, Timeout.Infinite);
            }
        }

        private class ComboBoxState
        {
            public int SelectedIndex { get; set; } = -1;
            public string? Text { get; set; }
        }
    }
}
