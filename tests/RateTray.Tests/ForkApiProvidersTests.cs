using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Fork: trilho de API. As entradas são os exemplos de resposta das documentações oficiais
/// (OpenAI OpenAPI, Anthropic Admin API, Kimi Open Platform, DeepSeek), com datas trocadas para
/// um mês fixo. Nada aqui toca rede nem cofre.
/// </summary>
public class ForkApiProvidersTests
{
    public ForkApiProvidersTests() => Loc.Use("en");

    // 2030-01-15 12:00 UTC — "hoje" é o balde que começa em 2030-01-15 00:00 UTC.
    private static readonly DateTimeOffset Now = new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private const long Jan14 = 1894579200;   // 2030-01-14T00:00:00Z
    private const long Jan15 = 1894665600;   // 2030-01-15T00:00:00Z

    private static LimitReading Id(IEnumerable<LimitReading> readings, string id) => readings.Single(r => r.Id == id);

    // ------------------------------------------------------------------ OpenAI

    private static string OpenAICosts(string resultsKey = "results") => $$"""
    {
      "object": "page",
      "data": [
        { "object": "bucket", "start_time": {{Jan14}}, "end_time": {{Jan15}},
          "{{resultsKey}}": [ { "object": "organization.costs.result", "amount": { "value": 1.50, "currency": "usd" } } ] },
        { "object": "bucket", "start_time": {{Jan15}}, "end_time": {{Jan15 + 86400}},
          "{{resultsKey}}": [ { "object": "organization.costs.result", "amount": { "value": 0.06, "currency": "usd" }, "line_item": "Image models" },
                              { "object": "organization.costs.result", "amount": { "value": 0.44, "currency": "usd" } } ] }
      ],
      "has_more": false,
      "next_page": null
    }
    """;

    private const string OpenAIUsage = """
    {
      "object": "page",
      "data": [
        { "object": "bucket", "start_time": 1894579200, "end_time": 1894665600,
          "results": [ { "object": "organization.usage.completions.result", "input_tokens": 1000, "output_tokens": 500,
                         "input_cached_tokens": 800, "num_model_requests": 5 } ] },
        { "object": "bucket", "start_time": 1894665600, "end_time": 1894752000,
          "results": [ { "object": "organization.usage.completions.result", "input_tokens": 1200000, "output_tokens": 300000,
                         "num_model_requests": 42 } ] }
      ],
      "has_more": false, "next_page": null
    }
    """;

    [Fact]
    public void OpenAI_soma_o_gasto_do_mes_e_separa_o_de_hoje()
    {
        var r = OpenAIApiUsageProvider.ParseForTest([OpenAICosts()], [OpenAIUsage], Now);

        Assert.Equal(2.00m, Id(r, "openaiapi.spend_month").Amount);
        Assert.Equal(0.50m, Id(r, "openaiapi.spend_today").Amount);
        Assert.All(r, x => Assert.Equal("US$", x.AmountUnit));
        Assert.All(r, x => Assert.True(x.IsInformational));
        Assert.Equal("1.5 M tokens · 47 requests", Id(r, "openaiapi.spend_month").Note);
    }

    [Fact]
    public void OpenAI_aceita_result_no_singular_como_diz_o_esquema_formal()
    {
        var r = OpenAIApiUsageProvider.ParseForTest([OpenAICosts("result")], [], Now);

        Assert.Equal(2.00m, Id(r, "openaiapi.spend_month").Amount);
        Assert.Null(Id(r, "openaiapi.spend_month").Note);          // sem resposta de uso, sem nota
    }

    [Fact]
    public void OpenAI_ignora_moeda_que_nao_e_dolar()
    {
        const string eur = """
        { "data": [ { "start_time": 1894665600, "results": [ { "amount": { "value": 9.99, "currency": "eur" } } ] } ] }
        """;
        Assert.Equal(2.00m, Id(OpenAIApiUsageProvider.ParseForTest([OpenAICosts(), eur], [], Now), "openaiapi.spend_month").Amount);
    }

    [Fact]
    public void OpenAI_sem_custo_nao_inventa_leitura()
    {
        Assert.Empty(OpenAIApiUsageProvider.ParseForTest([], [OpenAIUsage], Now));
        Assert.Empty(OpenAIApiUsageProvider.ParseForTest(["<html>502</html>"], [], Now));
    }

    [Fact]
    public void OpenAI_pagina_sem_resultados_nao_atrapalha_a_soma()
    {
        const string vazio = """{ "object": "page", "data": [ { "start_time": 1894665600, "results": [] } ], "has_more": false }""";
        var r = OpenAIApiUsageProvider.ParseForTest([vazio, OpenAICosts()], [], Now);
        Assert.Equal(2.00m, Id(r, "openaiapi.spend_month").Amount);
    }

    [Theory]
    [InlineData("""{ "object": "page", "data": [], "has_more": false }""")]
    [InlineData("""{ "object": "page", "data": [ { "start_time": 1894665600, "results": [] } ], "has_more": false }""")]
    public void OpenAI_mes_sem_gasto_mostra_zero_em_vez_de_sumir(string vazio)
    {
        var r = OpenAIApiUsageProvider.ParseForTest([vazio], [], Now);
        Assert.Equal(0m, Id(r, "openaiapi.spend_month").Amount);
        Assert.Equal(0m, Id(r, "openaiapi.spend_today").Amount);
    }

    [Fact]
    public void OpenAI_sem_relatorio_de_custos_valido_nao_inventa_zero()
    {
        Assert.Empty(OpenAIApiUsageProvider.ParseForTest(["""{ "error": "x" }"""], [], Now));
        Assert.Empty(OpenAIApiUsageProvider.ParseForTest([], [OpenAIUsage], Now));
    }

    // ------------------------------------------------------------------ Anthropic

    private const string AnthropicCosts = """
    {
      "data": [
        { "starting_at": "2030-01-14T00:00:00Z", "ending_at": "2030-01-15T00:00:00Z",
          "results": [ { "amount": "123.78912", "currency": "USD", "cost_type": "tokens", "model": "claude-opus-5" } ] },
        { "starting_at": "2030-01-15T00:00:00Z", "ending_at": "2030-01-16T00:00:00Z",
          "results": [ { "amount": "250", "currency": "USD" }, { "amount": "26.21088", "currency": "USD" } ] }
      ],
      "has_more": false,
      "next_page": null
    }
    """;

    private const string AnthropicUsage = """
    {
      "data": [
        { "starting_at": "2030-01-15T00:00:00Z", "ending_at": "2030-01-16T00:00:00Z",
          "results": [ { "uncached_input_tokens": 1500, "cache_read_input_tokens": 200,
                         "cache_creation": { "ephemeral_1h_input_tokens": 0, "ephemeral_5m_input_tokens": 300 },
                         "output_tokens": 500, "model": "claude-opus-5" } ] }
      ],
      "has_more": false, "next_page": null
    }
    """;

    [Fact]
    public void Anthropic_converte_centavos_em_texto_para_dolares()
    {
        var r = AnthropicApiUsageProvider.ParseForTest([AnthropicCosts], [AnthropicUsage], Now);

        // 123.78912 + 250 + 26.21088 = 400 centavos = US$ 4,00; hoje: 276,21088 centavos = US$ 2,76
        Assert.Equal(4.00m, Id(r, "anthropicapi.spend_month").Amount);
        Assert.Equal(2.76m, Id(r, "anthropicapi.spend_today").Amount);
        Assert.Equal("2,500 tokens", Id(r, "anthropicapi.spend_month").Note);
    }

    [Fact]
    public void Anthropic_resposta_ilegivel_nao_vira_leitura() =>
        Assert.Empty(AnthropicApiUsageProvider.ParseForTest(["{\"type\":\"error\"}"], [], Now));

    // ------------------------------------------------------------------ Kimi (plataforma)

    [Fact]
    public void Kimi_plataforma_le_o_exemplo_da_documentacao()
    {
        const string doc = """
        { "code": 0, "data": { "available_balance": 49.58894, "voucher_balance": 46.58893, "cash_balance": 3.00001 },
          "scode": "0x0", "status": true }
        """;
        var r = KimiApiUsageProvider.ParseForTest(doc);

        Assert.Equal(49.58894m, Id(r, "kimiapi.balance").Amount);
        Assert.Equal("Voucher US$ 46.59 · cash US$ 3.00", Id(r, "kimiapi.balance").Note);
    }

    [Fact]
    public void Kimi_plataforma_com_divida_mostra_zero_e_a_divida_na_nota()
    {
        const string devendo = """{ "code": 0, "data": { "available_balance": -2.5, "voucher_balance": 0, "cash_balance": -2.5 } }""";
        var r = KimiApiUsageProvider.ParseForTest(devendo);

        Assert.Equal(0m, Id(r, "kimiapi.balance").Amount);
        Assert.Contains("-2.50", Id(r, "kimiapi.balance").Note);
    }

    // ------------------------------------------------------------------ DeepSeek

    [Fact]
    public void DeepSeek_prefere_dolar_quando_ha_as_duas_moedas()
    {
        const string ambas = """
        { "is_available": true, "balance_infos": [
            { "currency": "CNY", "total_balance": "110.00", "granted_balance": "10.00", "topped_up_balance": "100.00" },
            { "currency": "USD", "total_balance": "4.10", "granted_balance": "0.00", "topped_up_balance": "4.10" } ] }
        """;
        var r = DeepSeekUsageProvider.ParseForTest(ambas);

        Assert.Equal(4.10m, Id(r, "deepseek.balance").Amount);
        Assert.Equal("US$", Id(r, "deepseek.balance").AmountUnit);
    }

    [Fact]
    public void DeepSeek_so_em_yuan_mostra_yuan()
    {
        const string yuan = """{ "is_available": true, "balance_infos": [ { "currency": "CNY", "total_balance": "110.00" } ] }""";
        var r = DeepSeekUsageProvider.ParseForTest(yuan);

        Assert.Equal(110.00m, Id(r, "deepseek.balance").Amount);
        Assert.Equal("¥", Id(r, "deepseek.balance").AmountUnit);
    }

    // ------------------------------------------------------------------ paginação

    [Fact]
    public async Task Paginacao_segue_o_cursor_e_para_no_teto()
    {
        var pedidos = new List<string>();
        Task<ApiKeyProvider.Fetch> Get(string path)
        {
            pedidos.Add(path);
            return Task.FromResult(ApiKeyProvider.Fetch.Ok("""{ "data": [], "has_more": true, "next_page": "page_xyz==" }"""));
        }

        var paginas = await PaginasDeTeste.FetchAsync(Get, "/costs?limit=31", maxPages: 3);

        // Três pedidos e ainda has_more: o relatório passou do teto. Somar o que chegou mostraria
        // um gasto menor que o real, então o resultado é só a falha.
        Assert.Equal(3, pedidos.Count);
        Assert.Equal("/costs?limit=31", pedidos[0]);
        Assert.Equal("/costs?limit=31&page=page_xyz%3D%3D", pedidos[1]);
        var unica = Assert.Single(paginas);
        Assert.Null(unica.Json);
        Assert.Equal("teste", unica.Kind);
        Assert.Equal(Loc.T("error.api.incomplete", "teste"), unica.Error);
    }

    [Fact]
    public async Task Paginacao_com_falha_no_meio_descarta_as_paginas_ja_lidas()
    {
        var chamadas = 0;
        Task<ApiKeyProvider.Fetch> Get(string path) => Task.FromResult(++chamadas == 1
            ? ApiKeyProvider.Fetch.Ok("""{ "data": [ {"x": 1} ], "has_more": true, "next_page": "p2" }""")
            : new ApiKeyProvider.Fetch(null, System.Net.HttpStatusCode.TooManyRequests, "limite") { RateLimited = true });

        var paginas = await PaginasDeTeste.FetchAsync(Get, "/costs", maxPages: 5);

        var unica = Assert.Single(paginas);
        Assert.Null(unica.Json);
        Assert.True(unica.RateLimited);          // o motivo da falha chega inteiro, para o recuo
        Assert.Equal(2, chamadas);
    }

    [Fact]
    public async Task Pagina_com_objeto_de_erro_no_meio_nao_vira_pagina_vazia()
    {
        var chamadas = 0;
        Task<ApiKeyProvider.Fetch> Get(string path) => Task.FromResult(ApiKeyProvider.Fetch.Ok(++chamadas == 1
            ? """{ "data": [ {"x": 1} ], "has_more": true, "next_page": "p2" }"""
            : """{ "error": "upstream" }"""));

        var unica = Assert.Single(await PaginasDeTeste.FetchAsync(Get, "/costs", maxPages: 5));
        Assert.Null(unica.Json);
        Assert.Equal(Loc.T("error.api.noData", "teste"), unica.Error);
    }

    [Fact]
    public async Task Mais_paginas_sem_cursor_e_relatorio_incompleto()
    {
        Task<ApiKeyProvider.Fetch> Get(string path) =>
            Task.FromResult(ApiKeyProvider.Fetch.Ok("""{ "data": [], "has_more": true, "next_page": null }"""));

        var unica = Assert.Single(await PaginasDeTeste.FetchAsync(Get, "/costs", maxPages: 5));
        Assert.Null(unica.Json);
        Assert.Equal(Loc.T("error.api.incomplete", "teste"), unica.Error);
    }

    [Fact]
    public async Task Paginacao_com_json_invalido_vira_sem_dados()
    {
        Task<ApiKeyProvider.Fetch> Get(string path) => Task.FromResult(ApiKeyProvider.Fetch.Ok("<html>proxy</html>"));

        var unica = Assert.Single(await PaginasDeTeste.FetchAsync(Get, "/costs", maxPages: 5));
        Assert.Null(unica.Json);
        Assert.Equal(Loc.T("error.api.noData", "teste"), unica.Error);
    }

    [Fact]
    public async Task Paginacao_para_quando_nao_ha_mais()
    {
        var chamadas = 0;
        Task<ApiKeyProvider.Fetch> Get(string path)
        {
            chamadas++;
            return Task.FromResult(ApiKeyProvider.Fetch.Ok("""{ "data": [], "has_more": false, "next_page": null }"""));
        }

        await PaginasDeTeste.FetchAsync(Get, "/costs", maxPages: 3);
        Assert.Equal(1, chamadas);
    }

    /// <summary>Expõe o método protegido de paginação para o teste.</summary>
    private sealed class PaginasDeTeste() : ApiKeyProvider(new ApiProviderOptions())
    {
        public static Task<List<Fetch>> FetchAsync(Func<string, Task<Fetch>> get, string path, int maxPages) =>
            FetchPagesAsync(get, path, "teste", "teste", maxPages);

        public override string Group => "teste";
        protected override string VaultService => CredentialVault.DeepSeek;
        protected override string Vendor => "teste";
        protected override string BaseUrl => "https://example.invalid";
        protected override void Authorize(HttpRequestMessage request, string key) { }
        protected override Task<IReadOnlyList<Fetch>> FetchAllAsync(Func<string, Task<Fetch>> get) =>
            Task.FromResult<IReadOnlyList<Fetch>>([]);
        protected override List<LimitReading> Parse(IReadOnlyList<Fetch> fetches, DateTimeOffset now) => [];
    }

    // ------------------------------------------------------------------ datas

    [Fact]
    public void Mes_e_dia_comecam_em_utc()
    {
        var noite = new DateTimeOffset(2030, 2, 1, 1, 30, 0, TimeSpan.FromHours(3));   // 31/01 22:30 UTC
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), ApiKeyProvider.MonthStart(noite));
        Assert.Equal(new DateTimeOffset(2030, 1, 31, 0, 0, 0, TimeSpan.Zero), ApiKeyProvider.DayStart(noite));
    }

    [Theory]
    [InlineData(900, "900")]
    [InlineData(12_345, "12 k")]
    [InlineData(1_500_000, "1.5 M")]
    [InlineData(2_000_000_000, "2 G")]
    public void Contagem_curta_para_a_nota(long valor, string esperado) =>
        Assert.Equal(esperado, ApiKeyProvider.CompactCount(valor));

    // ------------------------------------------------------------------ catálogo e cofre

    [Fact]
    public void Todo_servico_tem_fornecedor_listado_e_prefixo_que_nao_engole_outro()
    {
        foreach (var s in ServiceCatalog.Services)
        {
            Assert.Contains(s.Vendor, ServiceCatalog.VendorOrder);
            foreach (var outro in ServiceCatalog.Services.Where(o => o != s))
                Assert.False(outro.IdPrefix.StartsWith(s.IdPrefix, StringComparison.OrdinalIgnoreCase),
                    $"{s.IdPrefix} é prefixo de {outro.IdPrefix}");
        }
    }

    [Theory]
    [InlineData("anthropicapi.spend_month", "Anthropic API")]
    [InlineData("openaiapi.spend_today", "OpenAI API")]
    [InlineData("kimiapi.balance", "Kimi API")]
    [InlineData("kimi.5h", "Kimi")]
    [InlineData("deepseek.balance", "DeepSeek")]
    public void Id_aponta_o_servico_certo(string id, string grupo) =>
        Assert.Equal(grupo, TrayApp.GroupOf(id));

    [Fact]
    public void Chaves_novas_tem_alvo_proprio_e_nenhum_nome_antigo()
    {
        foreach (var servico in new[] { CredentialVault.OpenAIAdmin, CredentialVault.AnthropicAdmin, CredentialVault.KimiPlatform, CredentialVault.DeepSeek })
        {
            Assert.StartsWith("Gaugely/", CredentialVault.TargetFor(servico));
            Assert.Null(CredentialVault.LegacyTargetFor(servico));
            Assert.Contains(servico, CredentialVault.Services);
        }
    }

    [Fact]
    public void Anthropic_mes_sem_gasto_mostra_zero()
    {
        var r = AnthropicApiUsageProvider.ParseForTest([""" { "data": [], "has_more": false, "next_page": null } """], [], Now);
        Assert.Equal(0m, Id(r, "anthropicapi.spend_month").Amount);
    }
}
