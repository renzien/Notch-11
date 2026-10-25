using System.Windows;
using DynamicIsland.Native;

namespace DynamicIsland;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Win32.SetCurrentProcessExplicitAppUserModelID("DynamicIsland.App.Notch");
        }
        catch { }
    }
}
