namespace FreeRes.Host;

/// <summary>Parametres du serveur local.</summary>
public sealed class LocalWebHostOptions
{
    /// <summary>Nom du cookie portant le jeton de session (pose par la fenetre WebView2 uniquement).</summary>
    public const string TokenCookieName = "freeres_token";

    /// <summary>Seul nom d'hote accepte : bloque le « DNS rebinding » (Host: attaquant.com -> 127.0.0.1).</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>Jeton secret aleatoire (genere a chaque lancement de l'application).</summary>
    public required string Token { get; init; }

    /// <summary>Port d'ecoute ; 0 = port libre choisi par le systeme.</summary>
    public int Port { get; init; }

    /// <summary>Instant de demarrage expose par /api/health.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
}
