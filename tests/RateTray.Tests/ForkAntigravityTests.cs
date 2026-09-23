using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>Fork: formato do relatório do agy 1.2.4, com valores sintéticos.</summary>
public class ForkAntigravityTests
{
    private const string Amostra = """
        {"conversation_id":"","status":"SUCCESS","response":"...","duration_seconds":0,"num_turns":0,
         "usage":{"total_tokens":0},
         "command":{"name":"usage","data":{"groups":[
           {"name":"Gemini Models","buckets":[
             {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.8,"reset_time":"2030-01-08T12:00:00Z"},
             {"id":"gemini-5h","window":"5h","remaining_fraction":1,"reset_time":"2030-01-01T17:00:00Z"}]},
           {"name":"Claude and GPT models","buckets":[
             {"id":"3p-weekly","window":"weekly","remaining_fraction":0.5,"reset_time":"2030-01-08T12:00:00Z"},
             {"id":"3p-5h","window":"5h","remaining_fraction":1,"reset_time":"2030-01-01T17:00:00Z"}]}]}}}
        """;

    [Fact]
    public void Le_as_quatro_janelas_como_percentual_usado()
    {
        var r = AntigravityUsageProvider.Parse(Amostra, out var status);

        Assert.Equal("SUCCESS", status);
        Assert.Equal(4, r.Count);
        Assert.Equal(20, r.Single(x => x.Id == "antigravity.gemini.week").Percent, 3);
        Assert.Equal(0, r.Single(x => x.Id == "antigravity.gemini.5h").Percent, 3);
        Assert.Equal(50, r.Single(x => x.Id == "antigravity.3p.week").Percent, 3);
        Assert.True(r.Single(x => x.Id == "antigravity.gemini.5h").IsActive);
        Assert.Equal(new DateTimeOffset(2030, 1, 8, 12, 0, 0, TimeSpan.Zero), r[0].ResetsAt);
    }

    [Fact]
    public void Lixo_nao_vira_leitura()
    {
        Assert.Empty(AntigravityUsageProvider.Parse("not json", out _));
        Assert.Empty(AntigravityUsageProvider.Parse("""{"status":"ERROR"}""", out var status));
        Assert.Equal("ERROR", status);
    }

    [Fact]
    public void Id_aponta_o_servico()
    {
        Assert.Equal("Antigravity", TrayApp.GroupOf("antigravity.gemini.5h"));
    }
}
