using System;
using System.Windows.Forms;
using Test.TickPlayback.WinForms.Forms;

namespace Test.TickPlayback.WinForms
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    MessageBox.Show($"发生未捕获全局异常:\n{ex.Message}\n\n堆栈:\n{ex.StackTrace}", "全局异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            Application.Run(new MainTickPlaybackForm());
        }
    }
}
