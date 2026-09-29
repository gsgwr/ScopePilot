using System.Configuration;
using System.Data;
using System.Windows;

namespace ScopePilot;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Inno Setup checks this mutex before replacing application files.
    private readonly System.Threading.Mutex installerMutex = new(false, @"Local\ScopePilot.Application");

    protected override void OnExit(ExitEventArgs e)
    {
        installerMutex.Dispose();
        base.OnExit(e);
    }
}

