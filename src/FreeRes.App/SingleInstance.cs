namespace FreeRes.App;

/// <summary>Noms des objets de synchronisation d'une instance (« Local\ » = par session Windows, sans droits).</summary>
internal sealed record InstanceNames(string Mutex, string ShowEvent, string QuitEvent)
{
    /// <summary>
    /// Noms de l'application. « Local\FacilFreePC.SingleInstance » sert aussi a l'installeur via « --quit » :
    /// ne pas renommer sans adapter installer\FacilFreePC.iss.
    /// </summary>
    public static InstanceNames Default { get; } = For(Core.AppInfo.Id);

    /// <summary>Jeu de noms derive d'un identifiant (les tests en utilisent un unique pour ne jamais toucher l'appli reelle).</summary>
    public static InstanceNames For(string id) =>
        new($@"Local\{id}.SingleInstance", $@"Local\{id}.Show", $@"Local\{id}.Quit");
}

/// <summary>
/// Une seule instance par session Windows. Une 2e lancee signale « afficher » a la premiere ;
/// « FacilFreePC.exe --quit » (installeur) lui demande de se fermer et attend sa fin reelle.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public bool IsOwner { get; }

    public static SingleInstance Acquire(InstanceNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var mutex = new Mutex(initiallyOwned: true, names.Mutex, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>Signale l'instance en cours ; false si aucune n'ecoute.</summary>
    public static bool Signal(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    /// <summary>
    /// Demande a l'instance en cours de se fermer et attend qu'elle libere le mutex.
    /// true si aucune instance ne tourne (ou plus) ; false si elle ne s'est pas fermee dans le delai.
    /// </summary>
    public static bool RequestQuitAndWait(InstanceNames names, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (!Mutex.TryOpenExisting(names.Mutex, out var mutex))
        {
            return true;
        }

        using (mutex)
        {
            Signal(names.QuitEvent);
            try
            {
                if (!mutex.WaitOne(timeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // Proprietaire termine sans liberer : il est bien arrete. Le mutex nous appartient desormais.
            }

            mutex.ReleaseMutex();
            return true;
        }
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
