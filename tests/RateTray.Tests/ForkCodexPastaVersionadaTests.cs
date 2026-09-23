using RateTray.Providers;
using Xunit;

namespace RateTray.Tests;

/// <summary>
/// Fork: o app do Codex instala o binário sob uma pasta com código de versão
/// (…\OpenAI\Codex\bin\&lt;hash&gt;\codex.exe), que muda a cada atualização. Como a trava
/// FORK-2 ignora <c>codex.executablePath</c>, a descoberta tem de achar essa pasta sozinha.
/// </summary>
public class ForkCodexPastaVersionadaTests
{
    [Fact]
    public void Varredura_DevolveMaisRecentePrimeiro()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "gaugely-codex-" + Guid.NewGuid().ToString("N"));
        try
        {
            var velha = Directory.CreateDirectory(Path.Combine(raiz, "aaaa1111"));
            var nova = Directory.CreateDirectory(Path.Combine(raiz, "bbbb2222"));
            velha.LastWriteTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            nova.LastWriteTimeUtc = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

            var achados = CodexUsageProvider.VersionedCodexBins(raiz).ToList();

            Assert.Equal(2, achados.Count);
            Assert.Equal(Path.Combine(nova.FullName, "codex.exe"), achados[0]);
            Assert.Equal(Path.Combine(velha.FullName, "codex.exe"), achados[1]);
        }
        finally
        {
            Directory.Delete(raiz, recursive: true);
        }
    }

    [Fact]
    public void Varredura_SemPastaNaoQuebra()
    {
        var inexistente = Path.Combine(Path.GetTempPath(), "gaugely-codex-nao-existe-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(CodexUsageProvider.VersionedCodexBins(inexistente));
    }
}
