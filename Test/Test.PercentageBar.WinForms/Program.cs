using System;
using System.Windows.Forms;
using Test.PercentageBar.WinForms.Forms;

namespace Test.PercentageBar.WinForms
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
            Application.Run(new MainPercentBarForm());
        }
    }
}
