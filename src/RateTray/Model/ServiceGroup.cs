namespace RateTray.Model;

/// <summary>
/// Fork: um ícone por serviço, e não um por limite. O Codex reporta três janelas
/// (5 h, semana e o modelo base) e o Claude até quatro; com um ícone para cada uma a bandeja
/// enche de números iguais em cores quase iguais, e ninguém sabe qual olhar.
///
/// O número que representa o serviço é o do limite que trava primeiro — o maior percentual.
/// Uma sessão de 5 h a 10 % não serve de consolo se a semana já está em 95 %. O detalhe de cada
/// janela fica no cartão do mouse e no painel do clique.
/// </summary>
public static class ServiceGroup
{
    public const string IdPrefix = "grupo.";

    public static string IdFor(string group) => IdPrefix + group.ToLowerInvariant();

    public static bool IsGroupId(string id) => id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fork: nomes cuja caixa não sai de "primeira letra maiúscula". O id do grupo é minúsculo
    /// ("grupo.openrouter"), e reconstruir "Openrouter" faria o ícone não achar o próprio
    /// provedor, que se chama "OpenRouter" — erro e cartão do mouse sumiriam calados.
    /// </summary>
    public static string[] KnownGroups => ServiceCatalog.Services.Select(s => s.Group).ToArray();

    /// <summary>"grupo.codex" → "Codex", com a caixa que os provedores usam.</summary>
    public static string GroupOfId(string id)
    {
        var name = id[IdPrefix.Length..];
        var known = ServiceCatalog.FindByGroup(name);
        if (known is not null) return known.Group;
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Ordem de leitura: a janela que está correndo primeiro, depois das curtas às longas.
    /// Fork: as informativas (saldo, gasto) vão para o fim — contexto, não limite — e entre elas
    /// vale a ordem em que o provedor as entregou (a ordenação do LINQ é estável). Ordenar por
    /// janela poria "gasto hoje" antes do saldo, e o <see cref="Summary"/> de um serviço só
    /// informativo mostraria o gasto do dia no ícone em vez do dinheiro que sobra.
    /// </summary>
    public static IReadOnlyList<LimitReading> Ordered(IEnumerable<LimitReading> readings) =>
        readings
            .OrderBy(r => r.IsInformational)
            .ThenByDescending(r => !r.IsInformational && r.IsActive)
            .ThenBy(r => r.IsInformational ? TimeSpan.Zero : r.Window ?? TimeSpan.MaxValue)
            .ThenBy(r => r.IsInformational ? "" : r.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// O limite que trava primeiro. Empate vai para a janela ativa.
    /// Fork: leituras informativas não entram — saldo não trava, e o Percent delas é sempre zero.
    /// </summary>
    public static LimitReading? Binding(IEnumerable<LimitReading> readings) =>
        readings
            .Where(r => !r.IsInformational)
            .OrderByDescending(r => r.Percent)
            .ThenByDescending(r => r.IsActive)
            .FirstOrDefault();

    /// <summary>
    /// Leitura sintética que representa o serviço inteiro: valor, reset e janela do limite que
    /// trava primeiro, com a variação de cor zerada — um ícone só não tem vizinhos para se
    /// distinguir, e deve sair na cor do serviço.
    ///
    /// Fork: serviço que só informa (OpenRouter sem teto na chave) não tem limite que trave; aí o
    /// ícone mostra a primeira informativa, que é o saldo — melhor que "?" para quem está ok.
    /// </summary>
    public static LimitReading? Summary(string group, IEnumerable<LimitReading> readings)
    {
        var list = readings as IReadOnlyCollection<LimitReading> ?? readings.ToList();
        var chosen = Binding(list) ?? list.FirstOrDefault(r => r.IsInformational);
        return chosen is null
            ? null
            : chosen with { Id = IdFor(group), Label = group, Variant = 0, VariantCount = 1 };
    }
}
