using System.Text.Json;

namespace FreeRes.Core.Logging;

public enum LogLevel
{
    Info,
    Action,
    Warn,
    Error,
}

/// <summary>Une ligne du journal (une ligne JSON par evenement).</summary>
public sealed record LogEntry(DateTimeOffset Ts, LogLevel Level, string Source, string Message, IReadOnlyDictionary<string, object?>? Data = null);

/// <summary>
/// Journal JSONL mensuel (events-AAAA-MM.jsonl) : lisible a l'oeil, exploitable par l'interface et par
/// un support technique. Ecriture serialisee (verrou) : plusieurs threads du moteur ecrivent.
/// </summary>
public sealed class EventLog(string directory, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public string Directory { get; } = directory;

    public void Write(LogLevel level, string source, string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        var entry = new LogEntry(time.GetLocalNow(), level, source, message, data);
        var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(PathFor(entry.Ts), line);
        }
    }

    public string PathFor(DateTimeOffset ts) => Path.Combine(Directory, $"events-{ts:yyyy-MM}.jsonl");

    /// <summary>
    /// Evenements les plus recents (ordre chronologique), lignes illisibles ignorees. <paramref name="filter"/> :
    /// seuls les evenements retenus comptent dans <paramref name="max"/> (ex. actions et decisions seulement).
    /// </summary>
    public IReadOnlyList<LogEntry> ReadRecent(int max, Func<LogEntry, bool>? filter = null)
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            var result = new List<LogEntry>();
            foreach (var file in System.IO.Directory.GetFiles(Directory, "events-*.jsonl").OrderDescending())
            {
                foreach (var line in File.ReadLines(file).Reverse())
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<LogEntry>(line, Json) is { } e && (filter is null || filter(e)))
                        {
                            result.Add(e);
                        }
                    }
                    catch (JsonException)
                    {
                        // ligne tronquee (arret brutal) : ignoree
                    }

                    if (result.Count >= max)
                    {
                        result.Reverse();
                        return result;
                    }
                }
            }

            result.Reverse();
            return result;
        }
    }

    /// <summary>Supprime les journaux mensuels dont la derniere ecriture dépassé la duree de conservation.</summary>
    public int Purge(int keepDays)
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return 0;
            }

            var cut = time.GetUtcNow().UtcDateTime.AddDays(-keepDays);
            var removed = 0;
            foreach (var file in System.IO.Directory.GetFiles(Directory, "events-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) < cut)
                {
                    File.Delete(file);
                    removed++;
                }
            }

            return removed;
        }
    }
}
