using System.Net;
using System.Net.Sockets;
using System.Text;
using RateTray.Providers;

namespace RateTray.Tests;

/// <summary>
/// Fork: o cliente de quem manda chave. Servidores de teste em 127.0.0.1 respondendo HTTP cru — o
/// que se prova é o comportamento do cliente, que é o mesmo em HTTPS.
/// </summary>
public class ForkSecureHttpTests
{
    [Fact]
    public async Task Redirecionamento_nao_e_seguido_e_a_chave_nao_chega_ao_outro_host()
    {
        using var outro = new TcpListener(IPAddress.Loopback, 0);
        outro.Start();
        var portaOutro = ((IPEndPoint)outro.LocalEndpoint).Port;
        var conexoesNoOutro = 0;
        _ = Task.Run(async () =>
        {
            try { using var c = await outro.AcceptTcpClientAsync(); Interlocked.Increment(ref conexoesNoOutro); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });

        await using var origem = Servidor.Resposta(
            $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{portaOutro}/roubar\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        using var http = SecureHttp.Create();
        using var pedido = new HttpRequestMessage(HttpMethod.Get, origem.Url);
        pedido.Headers.Add("x-api-key", "sk-ant-admin-teste");
        using var resposta = await http.SendAsync(pedido);

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref conexoesNoOutro));
        Assert.Contains("x-api-key: sk-ant-admin-teste", await origem.PedidoRecebido, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resposta_acima_do_teto_falha_em_vez_de_ir_inteira_para_a_memoria()
    {
        var grande = SecureHttp.MaxResponseBytes + 1024;
        await using var origem = Servidor.Resposta(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {grande}\r\nConnection: close\r\n\r\n",
            corpoBytes: grande);

        using var http = SecureHttp.Create();
        var erro = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(origem.Url));
        Assert.NotEmpty(SecureHttp.Describe(erro));
    }

    [Fact]
    public void Descricao_de_erro_nunca_repete_o_texto_da_excecao()
    {
        var comSegredo = new HttpRequestException("falhou com Authorization: Bearer sk-segredo-123");
        Assert.DoesNotContain("sk-segredo", SecureHttp.Describe(comSegredo));
        Assert.DoesNotContain("sk-segredo", SecureHttp.Describe(new InvalidOperationException("sk-segredo-123")));
    }

    [Fact]
    public void Cliente_desliga_redirecionamento_e_tem_teto()
    {
        using var handler = SecureHttp.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        using var http = SecureHttp.Create();
        Assert.Equal(SecureHttp.MaxResponseBytes, http.MaxResponseContentBufferSize);
    }

    /// <summary>Servidor de uma resposta só: lê o pedido, devolve o texto dado e fecha.</summary>
    private sealed class Servidor : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<string> _pedido;

        private Servidor(TcpListener listener, Task<string> pedido)
        {
            _listener = listener;
            _pedido = pedido;
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/teste";

        public Task<string> PedidoRecebido => _pedido;

        public static Servidor Resposta(string cabecalho, long corpoBytes = 0)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var pedido = Task.Run(async () =>
            {
                using var cliente = await listener.AcceptTcpClientAsync();
                var stream = cliente.GetStream();
                var buffer = new byte[8192];
                var lido = new StringBuilder();
                while (!lido.ToString().Contains("\r\n\r\n"))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) break;
                    lido.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(cabecalho));
                var bloco = new byte[64 * 1024];
                try
                {
                    for (long enviado = 0; enviado < corpoBytes; enviado += bloco.Length)
                        await stream.WriteAsync(bloco.AsMemory(0, (int)Math.Min(bloco.Length, corpoBytes - enviado)));
                }
                catch (IOException) { }             // o cliente desistiu ao passar do teto
                return lido.ToString();
            });
            return new Servidor(listener, pedido);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _pedido; } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
    }
}
