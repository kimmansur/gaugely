using RateTray.Configuration;
using RateTray.Model;
using RateTray.Ui;
using Xunit;

namespace RateTray.Tests;

public class ForkCatalogoTests
{
    [Theory]
    [InlineData("claude.", "Claude")]
    [InlineData("codex.", "Codex")]
    [InlineData("kimi.", "Kimi")]
    [InlineData("openrouter.", "OpenRouter")]
    [InlineData("antigravity.", "Antigravity")]
    public void Prefixo_LevaAoGrupoCerto(string prefixo, string grupoEsperado)
    {
        // Fork: testa se cada prefixo retorna o grupo correto
        var record = ServiceCatalog.GetByPrefix(prefixo + "algum_id");
        Assert.Equal(grupoEsperado, record.Group);
    }

    [Fact]
    public void Prefixo_DesconhecidoLevaAClaude()
    {
        // Fork: testa o comportamento padrão (fallback)
        var record = ServiceCatalog.GetByPrefix("desconhecido.123");
        Assert.Equal("Claude", record.Group);
    }

    [Fact]
    public void GeminiEAntigravity_UsamMesmaCor()
    {
        // Fork: testa se a resolução da cor lida com o alias gemini
        var palette = new Palette(new AppConfig());
        var corGemini = palette.Service("gemini");
        var corAntigravity = palette.Service("antigravity");
        
        Assert.Equal(corAntigravity, corGemini);
    }

    [Fact]
    public void CincoGrupos_EstaoPresentes()
    {
        // Fork: garante que os 5 grupos base estão catalogados
        var grupos = ServiceCatalog.Services.Select(s => s.Group).ToList();
        Assert.Equal(5, grupos.Count);
        Assert.Contains("Claude", grupos);
        Assert.Contains("Codex", grupos);
        Assert.Contains("Kimi", grupos);
        Assert.Contains("OpenRouter", grupos);
        Assert.Contains("Antigravity", grupos);
    }
}
