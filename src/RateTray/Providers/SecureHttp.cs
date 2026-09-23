using System.Text.Json;

namespace RateTray.Providers;

/// <summary>
/// Fork: o cliente HTTP de quem manda chave ou token. Duas regras valem para todos:
/// <list type="bullet">
/// <item><b>Sem redirecionamento automático.</b> O .NET tira o <c>Authorization</c> ao seguir um
/// 3xx, mas não cabeçalhos próprios como o <c>x-api-key</c> da Anthropic: um 302 para outro host
/// levaria a chave Admin junto. Os endereços são fixos no código; um 3xx vira erro HTTP.</item>
/// <item><b>Teto de tamanho da resposta.</b> Leitura de saldo tem poucos KB; acima do teto o
/// pedido falha em vez de a resposta inteira ir para a memória.</item>
/// </list>
/// </summary>
internal static class SecureHttp
{
    internal const long MaxResponseBytes = 4 * 1024 * 1024;

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),   // renova DNS sem prender sockets
        AllowAutoRedirect = false,
    };

    /// <summary>O prazo de cada ciclo vem do CancellationToken, não do cliente.</summary>
    internal static HttpClient Create() => new(CreateHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    /// <summary>
    /// O motivo de uma falha de rede em categoria, nunca o texto cru da exceção: a mensagem vai
    /// para a interface, e não há como garantir que nenhuma exceção de transporte ou de montagem do
    /// pedido traga um pedaço do cabeçalho.
    /// </summary>
    internal static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http => http.HttpRequestError.ToString(),
        HttpRequestException => "HTTP",
        JsonException => "JSON",
        _ => ex.GetType().Name,
    };
}
