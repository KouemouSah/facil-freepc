using System.Diagnostics;
using FreeRes.Core;
using FreeRes.Host;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FreeRes.App;

/// <summary>
/// Fenetre de l'interface : WebView2 (moteur Edge integre a Windows) affichant le serveur local.
/// Le jeton est pose en cookie HttpOnly dans un profil WebView2 dedie : il n'apparait ni dans l'URL
/// ni dans aucun navigateur de l'utilisateur.
/// </summary>
internal sealed class MainForm : Form
{
    private const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly LocalWebHost _host;
    private readonly AppPaths _paths;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };

    public MainForm(LocalWebHost host, AppPaths paths)
    {
        _host = host;
        _paths = paths;
        Text = AppInfo.Name;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        var scale = DeviceDpi / 96.0;
        Size = FitToScreen(Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720), scale);
        MinimumSize = new Size(Math.Min((int)(900 * scale), Size.Width), Math.Min((int)(560 * scale), Size.Height));
        Controls.Add(_view);
    }

    /// <summary>
    /// Taille confortable de 1280x800 unites LOGIQUES (multipliees par l'echelle d'affichage), sans jamais
    /// dépassér 92 % de la zone utile (en pixels physiques, l'application etant « PerMonitorV2 »).
    /// Constate sur un portable a 150 % : zone utile 1920x1008 px -> 1766x927 px (1177x618 logiques).
    /// </summary>
    internal static Size FitToScreen(Rectangle workingArea, double scale)
    {
        var width = Math.Min((int)(1280 * scale), (int)(workingArea.Width * 0.92));
        var height = Math.Min((int)(800 * scale), (int)(workingArea.Height * 0.92));
        return new Size(width, height);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await InitializeAsync().ConfigureAwait(true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            OfferRuntimeDownload();
            Close();
        }
    }

    private async Task InitializeAsync()
    {
        // Leve WebView2RuntimeNotFoundException si le moteur n'est pas installe (rare sur Windows 10/11 a jour).
        _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebViewDirectory).ConfigureAwait(true);
        await _view.EnsureCoreWebView2Async(env).ConfigureAwait(true);

        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        var cookie = core.CookieManager.CreateCookie(LocalWebHostOptions.TokenCookieName, _host.Options.Token, LocalWebHostOptions.LoopbackHost, "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        core.CookieManager.AddOrUpdateCookie(cookie);

        // Toute navigation hors de l'interface locale part dans le navigateur par defaut, jamais dans la fenetre.
        core.NavigationStarting += (_, args) =>
        {
            if (!IsLocal(args.Uri))
            {
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };

        core.Navigate(_host.BaseAddress.ToString());
    }

    private bool IsLocal(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && Uri.Compare(u, _host.BaseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        {
            using var p = Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private static void OfferRuntimeDownload()
    {
        var answer = MessageBox.Show(
            $"{AppInfo.Name} a besoin du moteur Microsoft Edge WebView2, absent de cet ordinateur.\n\nOuvrir la page de téléchargement officielle de Microsoft ?",
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes)
        {
            OpenExternal(RuntimeDownloadUrl);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _view.Dispose();
        }

        base.Dispose(disposing);
    }
}
