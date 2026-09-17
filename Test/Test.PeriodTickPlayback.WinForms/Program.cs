using System;
using System.Windows.Forms;
using Test.PeriodTickPlayback.WinForms.Forms;

namespace Test.PeriodTickPlayback.WinForms
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainPeriodTickPlaybackForm());
        }
    }
}
