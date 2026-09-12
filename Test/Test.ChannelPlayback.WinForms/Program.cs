using System;
using System.Windows.Forms;
using Test.ChannelPlayback.WinForms.Forms;

namespace Test.ChannelPlayback.WinForms
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
            Application.Run(new MainChannelPlaybackForm());
        }
    }
}
