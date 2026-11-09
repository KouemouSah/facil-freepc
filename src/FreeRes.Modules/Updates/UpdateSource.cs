namespace FreeRes.Modules.Updates;

/// <summary>Acces reseau des mises a jour (remplace par un faux dans les tests).</summary>
public interface IUpdateSource
{
    /// <summary>Manifeste et sa signature (base64), ou null si la source ne repond pas (hors ligne, pas encore publie).</summary>
    Task<(byte[] Manifest, string Signature)?> FetchManifestAsync(CancellationToken cancellationToken);

    /// <summary>Telecharge l'installeur dans <paramref name="destination"/> (taille bornee).</summary>
    Task DownloadAsync(Uri url, string destination, CancellationToken cancellationToken);
}

/// <summary>
/// Source reelle : derniere version publiee sur GitHub (latest.json et latest.json.sig). Lecture seule : aucune
/// donnee n'est envoyee (simple requete GET ; l'adresse IP est visible de GitHub, comme pour toute page web).
/// </summary>
public sealed class HttpUpdateSource : IUpdateSource, IDisposable
{
    public const string ManifestUrl = "https://github.com/KouemouSah/facil-freepc/releases/latest/download/latest.json";
    public const long MaxInstallerBytes = 300L * 1024 * 1024;

    private readonly HttpClient _http;

    public HttpUpdateSource(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"FacilFreePC/{typeof(HttpUpdateSource).Assembly.GetName().Version?.ToString(3)}");
    }

    public async Task<(byte[] Manifest, string Signature)?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await GetLimitedAsync(new Uri(ManifestUrl), UpdateVerifier.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
            var signature = await GetLimitedAsync(new Uri(ManifestUrl + ".sig"), 1024, cancellationToken).ConfigureAwait(false);
            return manifest is null || signature is null ? null : (manifest, System.Text.Encoding.ASCII.GetString(signature));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task DownloadAsync(Uri url, string destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxInstallerBytes)
        {
            throw new UpdateRejectedException("installeur trop volumineux");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxInstallerBytes)
            {
                throw new UpdateRejectedException("installeur trop volumineux");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]?> GetLimitedAsync(Uri url, int max, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength > max)
        {
            return null;
        }

        // Lecture bornee : jamais plus de max + 1 octets en memoire, meme sans longueur annoncee.
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[max + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total > max ? null : buffer[..total];
    }

    public void Dispose() => _http.Dispose();
}
