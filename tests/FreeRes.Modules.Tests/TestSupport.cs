using System.Diagnostics;
using System.Text;
using FreeRes.Core;
using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Core.Policy;
using FreeRes.Core.Storage;

namespace FreeRes.Modules.Tests;

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Etat en memoire (le moteur reel utilise la base SQLite).</summary>
internal sealed class MemoryState : IStateStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? GetState(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void SetState(string key, string? value)
    {
        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
    }
}

/// <summary>Programmes externes simules : enregistre les appels, ne lance RIEN (ni shutdown, ni DISM).</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

    public Func<string, IReadOnlyList<string>, ProcessResult> Respond { get; set; } = (_, _) => new ProcessResult(0, string.Empty, string.Empty, false);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, Encoding? outputEncoding = null)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Respond(fileName, arguments));
    }
}

internal sealed class NullProbe : ISystemProbe
{
    public SystemSnapshot Capture() => throw new UnreachableException("non utilise par les modules testes");
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeres-mod-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // jonction de test deja retiree ou fichier encore ouvert
        }
    }
}

internal static class Fixtures
{
    public static readonly DateTimeOffset Start = new(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(1));

    public static ModuleServices Services(TempDir dir, TimeProvider time, IProcessRunner? processes = null, IStateStore? state = null) =>
        new(new AppPaths(dir.Sub("data")), new Core.Logging.EventLog(dir.Sub("logs"), time), time, new NullProbe(), processes ?? new FakeProcessRunner(), state ?? new MemoryState());

    /// <summary>Contexte de cycle : machine de reference, espace disque libre parametrable.</summary>
    public static EngineContext Context(TimeProvider time, double diskFreeGB = 40, bool rebootPending = false)
    {
        var snapshot = new SystemSnapshot(time.GetLocalNow(), 15.69, 6, 60, 23.69, 12, 50, "C:", 235.48, diskFreeGB, 1, rebootPending, []);
        var profile = new MachineProfile("PC", "Dell", "Latitude", "Windows 11", "25H2", 26200, 15.69, 12, "C:", 235.48);
        return new EngineContext(snapshot, profile, new Thresholds(3.0, 1.5, 85, 95, 30, 12), new AppSettings());
    }

    /// <summary>Cree un fichier de taille donnee, date (creation ET modification) dans le passe.</summary>
    public static string AgedFile(string path, int daysOld, int sizeKB = 4, DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeKB * 1024]);
        var when = (now ?? DateTimeOffset.Now).LocalDateTime.AddDays(-daysOld);
        File.SetCreationTime(path, when);
        File.SetLastWriteTime(path, when);
        return path;
    }

    public static void Age(string dir, int daysOld, DateTimeOffset? now = null)
    {
        var when = (now ?? DateTimeOffset.Now).LocalDateTime.AddDays(-daysOld);
        Directory.SetCreationTime(dir, when);
        Directory.SetLastWriteTime(dir, when);
    }

    /// <summary>Jonction NTFS (aucun droit administrateur requis), comme celles qui piegent les nettoyeurs naifs.</summary>
    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new InvalidOperationException("Creation de la jonction de test impossible : " + p.StandardError.ReadToEnd());
        }
    }
}
