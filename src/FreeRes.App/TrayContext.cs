using FreeRes.Core;
using FreeRes.Core.Engine;
using FreeRes.Core.Policy;
using FreeRes.Host;

namespace FreeRes.App;

/// <summary>
/// Vie de l'application : icone dans la zone de notification. La fenetre est creee a la demande et
/// detruite a la fermeture (l'application continue en arrière-plan) ; « Quitter » arrete tout.
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
    private readonly MonitorEngine _engine;
    private MainForm? _window;

    public TrayContext(LocalWebHost host, AppPaths paths, MonitorEngine engine, bool openWindow)
    {
        _host = host;
        _paths = paths;
        _engine = engine;
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
        _icon.BalloonTipClicked += (_, _) => ShowWindow();

        // Escalade decidee par le moteur (thread d'arrière-plan) -> presentee sur le thread de l'interface.
        _engine.CycleCompleted += OnCycleCompleted;

        // Signaux d'une autre instance (« afficher ») ou de la desinstallation (« quitter »).
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceNames.Default.ShowEvent);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceNames.Default.QuitEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => _ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        if (openWindow)
        {
            ShowWindow();
        }

        // Le premier cycle demarre avec le serveur, AVANT cet abonnement : s'il est deja termine, on le presente.
        if (_engine.LastReport is { } first)
        {
            OnCycleCompleted(this, first);
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

    private void OnCycleCompleted(object? sender, CycleReport report)
    {
        var pending = _engine.Queue.Pending();   // critiques d'abord
        var top = pending.Count > 0 ? pending[0].Proposal : null;
        var notice = EscalationNotice.For(report, top);
        _ui.Post(_ => Present(report, notice), null);
    }

    private void Present(CycleReport report, EscalationNotice notice)
    {
        // Un meme cycle n'est presente qu'une fois (rattrapage du premier cycle + evenement concurrent).
        if (report.At <= _lastPresented)
        {
            return;
        }

        _lastPresented = report.At;
        _icon.Text = notice.TrayText;
        switch (report.Escalation)
        {
            case EscalationMode.Window:
                ShowWindow();
                _engine.RecordEscalation(EscalationMode.Window);
                break;
            case EscalationMode.Toast when _window is { Visible: true, WindowState: not FormWindowState.Minimized }:
                // Fenetre ouverte : les decisions sont deja sous les yeux, une notification serait du bruit.
                _engine.RecordEscalation(EscalationMode.Toast);
                break;
            case EscalationMode.Toast:
                // Sous Windows 10/11, la bulle de la zone de notification s'affiche comme une notification Windows.
                _icon.ShowBalloonTip(15000, notice.Title, notice.Text, notice.Critical ? ToolTipIcon.Warning : ToolTipIcon.Info);
                _engine.RecordEscalation(EscalationMode.Toast);
                break;
        }
    }

    private DateTimeOffset _lastPresented = DateTimeOffset.MinValue;

    protected override void ExitThreadCore()
    {
        _engine.CycleCompleted -= OnCycleCompleted;
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
