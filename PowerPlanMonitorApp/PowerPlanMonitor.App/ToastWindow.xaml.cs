using System.Windows;
using System.Windows.Threading;

namespace PowerPlanMonitor.App;

public partial class ToastWindow : Window
{
    public ToastWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 28;
            Top = area.Bottom - Height - 34;
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }
}
