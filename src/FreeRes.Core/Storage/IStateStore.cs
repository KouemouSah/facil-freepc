namespace FreeRes.Core.Storage;

/// <summary>Etat persistant cle/valeur (dates de derniere action, mesures memorisees des modules...).</summary>
public interface IStateStore
{
    string? GetState(string key);

    void SetState(string key, string? value);
}

/// <summary>Etat d'un module : cles prefixees par son nom (« module.disk.lastDailyCleanAt »).</summary>
public sealed class ModuleState(IStateStore store, string module)
{
    private string Key(string key) => $"module.{module}.{key}";

    public string? Get(string key) => store.GetState(Key(key));

    public void Set(string key, string? value) => store.SetState(Key(key), value);

    public DateTimeOffset? GetDate(string key) => Get(key) is { } v ? FreeResDatabase.Parse(v) : null;

    public void SetDate(string key, DateTimeOffset? value) => Set(key, value is { } d ? FreeResDatabase.Iso(d) : null);
}
