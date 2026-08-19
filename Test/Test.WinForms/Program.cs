using Common.Extensions;
using Common.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows.Forms;
using Test.WinForms.Forms;

namespace Test.WinForms
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点 (WinForms + 依赖注入启动)
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // 1. 构建依赖注入容器
            var services = new ServiceCollection();
            services.AddBacktestEngine();       // 注册回测核心引擎服务 IBacktestEngineService
            services.AddTransient<MainForm>();  // 注册主窗体

            var serviceProvider = services.BuildServiceProvider();

            // 2. 从 DI 容器中解析主窗体并启动运行
            var mainForm = serviceProvider.GetRequiredService<MainForm>();
            Application.Run(mainForm);
        }
    }
}
