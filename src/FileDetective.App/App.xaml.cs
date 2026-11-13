using System.Drawing;
using Forms = System.Windows.Forms;

namespace FileDetective.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("Menu_Exit"), null, (_, _) => Shutdown());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Strings.Get("Tray_Tooltip"),
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}