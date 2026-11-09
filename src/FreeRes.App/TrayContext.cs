using FreeRes.Core;
using FreeRes.Host;

namespace FreeRes.App;

/// <summary>
/// Vie de l'application : icone dans la zone de notification. La fenetre est creee a la demande et
/// detruite a la fermeture (l'application continue en arriere-plan) ; « Quitter » arrete tout.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly LocalWebHost _host;
    private readonly AppPaths _paths;
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _quitEvent;
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _quitWait;
    private MainForm? _window;

    public TrayContext(LocalWebHost host, AppPaths paths, bool openWindow)
    {
        _host = host;
        _paths = paths;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Ouvrir {AppInfo.Name}", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = $"{AppInfo.Name} {AppInfo.Version}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        // Signaux d'une autre instance (« afficher ») ou de la desinstallation (« quitter »).
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceNames.Default.ShowEvent);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceNames.Default.QuitEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => _ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        if (openWindow)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainForm(_host, _paths);
            _window.FormClosed += (_, _) => _window = null;
            _window.Show();
            return;
        }

        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = FormWindowState.Normal;
        }

        _window.Activate();
    }

    protected override void ExitThreadCore()
    {
        _window?.Close();
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _quitWait.Unregister(null);
            _showEvent.Dispose();
            _quitEvent.Dispose();
            _icon.Dispose();
            _window?.Dispose();
        }

        base.Dispose(disposing);
    }
}
