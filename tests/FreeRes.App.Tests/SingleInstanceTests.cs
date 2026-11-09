namespace FreeRes.App.Tests;

/// <summary>
/// Fermeture demandee par l'installeur (« --quit ») : doit attendre la fin REELLE de l'instance,
/// sinon la desinstallation tenterait de supprimer un exe encore en cours d'execution.
/// Chaque test utilise des noms uniques : jamais d'interaction avec une vraie instance de Facil FreePC.
/// </summary>
public sealed class SingleInstanceTests
{
    private readonly InstanceNames _names = InstanceNames.For("FacilFreePC.Test." + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Noms_de_l_application_inchanges_car_utilises_par_l_installeur()
    {
        Assert.Equal(@"Local\FacilFreePC.SingleInstance", InstanceNames.Default.Mutex);
        Assert.Equal(@"Local\FacilFreePC.Quit", InstanceNames.Default.QuitEvent);
    }

    [Fact]
    public void Sans_instance_en_cours_retourne_vrai_immediatement()
    {
        Assert.True(SingleInstance.RequestQuitAndWait(_names, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Une_seule_instance_proprietaire()
    {
        using var first = SingleInstance.Acquire(_names);
        Assert.True(first.IsOwner);
        var second = RunOnThread(() => SingleInstance.Acquire(_names));
        try
        {
            Assert.False(second.IsOwner);
        }
        finally
        {
            RunOnThread(() => { second.Dispose(); return 0; });
        }
    }

    [Fact]
    public void Attend_que_l_instance_se_ferme_sur_signal()
    {
        using var ready = new ManualResetEventSlim();
        var closed = false;
        var owner = new Thread(() =>
        {
            using (SingleInstance.Acquire(_names))
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, _names.QuitEvent))
            {
                ready.Set();
                quit.WaitOne(TimeSpan.FromSeconds(10));
                Thread.Sleep(300);               // simule l'arret du serveur et de la fenetre
                Volatile.Write(ref closed, true);
            }                                    // Dispose libere le mutex : fin reelle de l'instance
        });
        owner.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(SingleInstance.RequestQuitAndWait(_names, TimeSpan.FromSeconds(10)));
        Assert.True(Volatile.Read(ref closed));  // la main n'est rendue qu'APRES la fermeture
        owner.Join();
    }

    [Fact]
    public void Retourne_faux_si_l_instance_ne_se_ferme_pas_dans_le_delai()
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using (SingleInstance.Acquire(_names))   // n'ecoute pas l'evenement « quitter »
            {
                ready.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        owner.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(SingleInstance.RequestQuitAndWait(_names, TimeSpan.FromMilliseconds(300)));
        release.Set();
        owner.Join();
    }

    // Un mutex appartient a un thread : chaque « instance » simulee vit sur son propre thread.
    private static T RunOnThread<T>(Func<T> action)
    {
        T result = default!;
        var t = new Thread(() => result = action());
        t.Start();
        t.Join();
        return result;
    }
}
