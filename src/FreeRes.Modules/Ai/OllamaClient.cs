using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeRes.Modules.Ai;

/// <summary>Modele Ollama charge en memoire (GET /api/ps). Tailles en octets.</summary>
public sealed record OllamaLoadedModel(string Name, long SizeBytes, long VramBytes, DateTimeOffset? ExpiresAt)
{
    /// <summary>Part en memoire systeme (le reste est en memoire graphique).</summary>
    public long SystemRamBytes => Math.Max(0, SizeBytes - VramBytes);
}

/// <summary>Modele Ollama installe sur le disque (GET /api/tags).</summary>
public sealed record OllamaInstalledModel(string Name, long SizeBytes);

/// <summary>
/// Client de l'API documentee d'Ollama (github.com/ollama/ollama, docs/api.md, verifiee le 2026-11-09) :
/// GET /api/ps (modeles charges : size, size_vram, expires_at), GET /api/tags (modeles installes),
/// POST /api/generate {"model", "keep_alive": 0} sans prompt = dechargement.
/// </summary>
public interface IOllamaClient
{
    /// <summary>null si aucun serveur Ollama ne repond.</summary>
    Task<IReadOnlyList<OllamaLoadedModel>?> LoadedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OllamaInstalledModel>?> InstalledAsync(CancellationToken cancellationToken);

    Task<bool> UnloadAsync(string model, CancellationToken cancellationToken);

    /// <summary>Adresse utilisee (affichage) ou motif du refus.</summary>
    string Endpoint { get; }
}

public sealed class HttpOllamaClient : IOllamaClient, IDisposable
{
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient? _http;

    /// <param name="ollamaHost">Valeur de OLLAMA_HOST (null = defaut). N'est retenue que si elle designe CETTE machine.</param>
    public HttpOllamaClient(string? ollamaHost = null, HttpMessageHandler? handler = null)
    {
        var uri = ResolveEndpoint(ollamaHost);
        Endpoint = uri?.ToString() ?? $"OLLAMA_HOST ignore (hors de cette machine) : {ollamaHost}";
        if (uri is not null)
        {
            _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.BaseAddress = uri;
            _http.Timeout = TimeSpan.FromSeconds(5);
        }
    }

    public string Endpoint { get; }

    /// <summary>
    /// Adresse d'Ollama : uniquement en boucle locale (127.0.0.1, ::1, localhost). Securite : FreePC ne doit jamais
    /// envoyer de requete vers une autre machine parce qu'une variable d'environnement le lui indique.
    /// </summary>
    public static Uri? ResolveEndpoint(string? ollamaHost)
    {
        if (string.IsNullOrWhiteSpace(ollamaHost))
        {
            return new Uri(DefaultEndpoint + "/");
        }

        var text = ollamaHost.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var host = uri.Host;
        if (host is "0.0.0.0" or "[::]" or "::")
        {
            host = "127.0.0.1";   // « ecoute sur toutes les interfaces » : on interroge la boucle locale
        }

        if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
        {
            return null;
        }

        // Port explicite seulement s'il figure dans la partie hote (« http://localhost » sans port = 11434, pas 80).
        var explicitPort = System.Text.RegularExpressions.Regex.Match(text, @"^[a-z]+://(\[[^\]]+\]|[^/:\[]+):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var port = explicitPort.Success ? int.Parse(explicitPort.Groups[2].Value, CultureInfo.InvariantCulture) : 11434;
        if (host == "::1")
        {
            host = "[::1]";
        }

        return new Uri(string.Create(CultureInfo.InvariantCulture, $"{uri.Scheme}://{host}:{port}/"));
    }

    public async Task<IReadOnlyList<OllamaLoadedModel>?> LoadedAsync(CancellationToken cancellationToken)
    {
        var r = await GetAsync<PsResponse>("api/ps", cancellationToken).ConfigureAwait(false);
        return r?.Models?.Where(m => !string.IsNullOrEmpty(m.Name))
            .Select(m => new OllamaLoadedModel(m.Name!, m.Size, m.SizeVram, m.ExpiresAt)).ToList();
    }

    public async Task<IReadOnlyList<OllamaInstalledModel>?> InstalledAsync(CancellationToken cancellationToken)
    {
        var r = await GetAsync<TagsResponse>("api/tags", cancellationToken).ConfigureAwait(false);
        return r?.Models?.Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => new OllamaInstalledModel(m.Name!, m.Size)).ToList();
    }

    public async Task<bool> UnloadAsync(string model, CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            return false;
        }

        try
        {
            using var res = await _http.PostAsJsonAsync("api/generate", new { model, keep_alive = 0 }, Json, cancellationToken).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        if (_http is null)
        {
            return null;
        }

        try
        {
            return await _http.GetFromJsonAsync<T>(path, Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return null;   // pas de serveur, delai depasse ou reponse inattendue : Ollama considere absent
        }
    }

    public void Dispose() => _http?.Dispose();

    private sealed record PsResponse(List<PsModel>? Models);

    private sealed record PsModel(
        string? Name,
        long Size,
        [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

    private sealed record TagsResponse(List<TagModel>? Models);

    private sealed record TagModel(string? Name, long Size);
}
