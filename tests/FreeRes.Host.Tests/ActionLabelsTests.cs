namespace FreeRes.Host.Tests;

public sealed class ActionLabelsTests
{
    [Theory]
    [InlineData("CleanUserZone", "CleanUserZone : npm-logs : 0,2 GB", "Nettoyage automatique : npm-logs : 0,2 GB")]
    [InlineData("SetServiceManual", "SetServiceManual (élevé) : gupdate en manuel", "Service passé en démarrage manuel : gupdate en manuel")]
    [InlineData("SetServiceManual", "SetServiceManual (eleve) : ancien journal", "Service passé en démarrage manuel : ancien journal")]
    [InlineData("UnloadOllamaModel", "Message sans prefixe", "Message sans prefixe")]
    [InlineData("TypeInconnu", "TypeInconnu : détail", "TypeInconnu : détail")]
    public void Message_du_journal_reecrit_avec_l_intitule(string type, string message, string expected) =>
        Assert.Equal(expected, ActionLabels.Rewrite(type, message));

    [Fact]
    public void Type_inconnu_rendu_tel_quel_jamais_vide()
    {
        Assert.False(ActionLabels.IsKnown("TypeInconnu"));
        Assert.Equal("TypeInconnu", ActionLabels.For("TypeInconnu"));
        Assert.Equal("Corbeille vidée", ActionLabels.For("EmptyRecycleBin"));
    }
}
