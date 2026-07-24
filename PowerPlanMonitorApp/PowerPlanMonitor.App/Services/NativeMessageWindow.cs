using System.Windows.Interop;

namespace PowerPlanMonitor.App.Services;

public sealed class NativeMessageWindow : IDisposable
{
    private const int WsExToolWindow = 0x00000080;
    private readonly HwndSource _source;

    public NativeMessageWindow()
    {
        var parameters = new HwndSourceParameters("PowerPlanMonitor.MessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow
        };

        _source = new HwndSource(parameters);
    }

    public IntPtr Handle => _source.Handle;

    public void Dispose() => _source.Dispose();
}
