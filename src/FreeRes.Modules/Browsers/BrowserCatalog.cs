namespace FreeRes.Modules.Browsers;

/// <summary>Navigateurs pris en charge et leurs emplacements standard (variables d'environnement developpees).</summary>
public static class BrowserCatalog
{
    /// <summary>Identifiant de l'extension compagnon Firefox (GUID : n'implique aucun nom de domaine). Ne jamais changer.</summary>
    public const string FirefoxExtensionId = "{cff36c27-7139-4e1a-8f80-5519fa80aa1d}";

    public static IReadOnlyList<BrowserDefinition> All { get; } =
    [
        new("chrome", "Google Chrome", BrowserEngine.Chromium, "chrome.exe",
            [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"],
            @"%LOCALAPPDATA%\Google\Chrome\User Data", RestartValidated: true, "chrome://settings/performance", "chrome://settings/onStartup"),
        new("edge", "Microsoft Edge", BrowserEngine.Chromium, "msedge.exe",
            [@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"],
            @"%LOCALAPPDATA%\Microsoft\Edge\User Data", RestartValidated: true, null, "edge://settings/onStartup"),
        new("brave", "Brave", BrowserEngine.Chromium, "brave.exe",
            [@"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe"],
            @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data", RestartValidated: false, null, null),
        new("opera", "Opera", BrowserEngine.Chromium, "opera.exe",
            [@"%LOCALAPPDATA%\Programs\Opera\opera.exe", @"%ProgramFiles%\Opera\opera.exe"],
            null, RestartValidated: false, null, null),
        new("vivaldi", "Vivaldi", BrowserEngine.Chromium, "vivaldi.exe",
            [@"%LOCALAPPDATA%\Vivaldi\Application\vivaldi.exe", @"%ProgramFiles%\Vivaldi\Application\vivaldi.exe"],
            @"%LOCALAPPDATA%\Vivaldi\User Data", RestartValidated: false, null, null),
        new("firefox", "Mozilla Firefox", BrowserEngine.Gecko, "firefox.exe",
            [@"%ProgramFiles%\Mozilla Firefox\firefox.exe", @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe"],
            null, RestartValidated: true, null, "about:preferences#general"),
        new("firefox-dev", "Firefox Developer Edition", BrowserEngine.Gecko, "firefox.exe",
            [@"%ProgramFiles%\Firefox Developer Edition\firefox.exe"],
            null, RestartValidated: true, null, "about:preferences#general"),
        new("firefox-nightly", "Firefox Nightly", BrowserEngine.Gecko, "firefox.exe",
            [@"%ProgramFiles%\Firefox Nightly\firefox.exe"],
            null, RestartValidated: true, null, "about:preferences#general"),
    ];

    public static BrowserDefinition? Find(string id) => All.FirstOrDefault(b => b.Id == id);

    public static string FirefoxProfilesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
}
