using System.Runtime.InteropServices;
using RateTray.Configuration;

namespace RateTray.Ui;

/// <summary>
/// Fork: a conta do espaço reservado pela faixa no modo Notch, sem nada de Win32 — o retângulo
/// pedido ao shell, o ajuste depois que ele responde e a área em que a faixa se posiciona. Separada
/// de <see cref="AppBar"/> para que toda borda e todo monitor sejam testáveis.
/// </summary>
internal static class AppBarMath
{
    /// <summary>
    /// Faixa de <paramref name="espessura"/> px colada à borda do monitor, ocupando a borda inteira:
    /// o shell só reserva faixas completas, não o pedaço onde a faixa está.
    /// </summary>
    public static Rectangle Request(Rectangle monitor, BordaTela borda, int espessura)
    {
        espessura = Math.Max(0, espessura);
        return borda switch
        {
            BordaTela.Esquerda => Rectangle.FromLTRB(monitor.Left, monitor.Top, monitor.Left + espessura, monitor.Bottom),
            BordaTela.Direita => Rectangle.FromLTRB(monitor.Right - espessura, monitor.Top, monitor.Right, monitor.Bottom),
            BordaTela.Topo => Rectangle.FromLTRB(monitor.Left, monitor.Top, monitor.Right, monitor.Top + espessura),
            _ => Rectangle.FromLTRB(monitor.Left, monitor.Bottom - espessura, monitor.Right, monitor.Bottom),
        };
    }

    /// <summary>
    /// Depois do ABM_QUERYPOS: o shell empurra o retângulo para fora da barra de tarefas e de outras
    /// AppBars na mesma borda e pode encolhê-lo. Mantém o lado encostado que ele devolveu e refaz a
    /// espessura a partir dali, como manda a documentação do SHAppBarMessage.
    /// </summary>
    public static Rectangle Fit(Rectangle proposto, BordaTela borda, int espessura)
    {
        espessura = Math.Max(0, espessura);
        return borda switch
        {
            BordaTela.Esquerda => Rectangle.FromLTRB(proposto.Left, proposto.Top, proposto.Left + espessura, proposto.Bottom),
            BordaTela.Direita => Rectangle.FromLTRB(proposto.Right - espessura, proposto.Top, proposto.Right, proposto.Bottom),
            BordaTela.Topo => Rectangle.FromLTRB(proposto.Left, proposto.Top, proposto.Right, proposto.Top + espessura),
            _ => Rectangle.FromLTRB(proposto.Left, proposto.Bottom - espessura, proposto.Right, proposto.Bottom),
        };
    }

    /// <summary>
    /// Área em que a faixa se posiciona quando há reserva. A área útil do Windows já desconta o
    /// espaço reservado — usá-la direto poria a faixa para fora da reserva, colada nas janelas
    /// maximizadas. Aqui o lado da borda volta para a face externa da reserva; os outros três
    /// continuam os da área útil. Dá o mesmo resultado antes e depois de o Windows atualizar a área
    /// útil, então não importa quando ela é lida. A reserva degenerada (espessura zero, faixa fora do
    /// canto — ver <see cref="EspessuraReserva"/>) ou vazia não entra: devolve a área útil como está.
    /// A linha que o shell devolve para ela pode ficar na borda externa do monitor, além da barra de
    /// tarefas na mesma borda, e não serve de limite (ver <see cref="AreaDaFaixa"/>).
    /// </summary>
    public static Rectangle LayoutArea(Rectangle areaUtil, BordaTela borda, Rectangle reservado)
    {
        if (Degenerada(reservado)) return areaUtil;

        return borda switch
        {
            BordaTela.Esquerda => Rectangle.FromLTRB(reservado.Left, areaUtil.Top, areaUtil.Right, areaUtil.Bottom),
            BordaTela.Direita => Rectangle.FromLTRB(areaUtil.Left, areaUtil.Top, reservado.Right, areaUtil.Bottom),
            BordaTela.Topo => Rectangle.FromLTRB(areaUtil.Left, reservado.Top, areaUtil.Right, areaUtil.Bottom),
            _ => Rectangle.FromLTRB(areaUtil.Left, areaUtil.Top, areaUtil.Right, reservado.Bottom),
        };
    }

    /// <summary>Reserva sem área (espessura zero ou nenhuma concessão): não tira nada da área útil.</summary>
    public static bool Degenerada(Rectangle reservado) => reservado.Width <= 0 || reservado.Height <= 0;

    /// <summary>
    /// Área em que a faixa se posiciona depois do ABM_SETPOS. No canto, <see cref="LayoutArea"/> sobre
    /// a área do cache. Fora do canto (reserva degenerada) o retângulo concedido não conta: a faixa
    /// encosta no limite da área útil, que já exclui a barra de tarefas — como sem reserva. Vale a área
    /// lida na hora do monitor (<paramref name="fresca"/>, GetMonitorInfo depois do SETPOS), porque o
    /// cache de Screen logo depois de sair do canto ainda desconta a reserva antiga; sem leitura
    /// válida fica o cache (<see cref="AreaAposRemover"/>), e o relayout de quando ele atualizar corrige.
    /// </summary>
    public static Rectangle AreaDaFaixa(Rectangle cache, Rectangle? fresca, Rectangle monitor, BordaTela borda, Rectangle reservado) =>
        Degenerada(reservado) ? AreaAposRemover(fresca, cache, monitor) : LayoutArea(cache, borda, reservado);

    /// <summary>
    /// Folga, em px físicos, para a faixa solta perto do início ou do fim da área útil ser encaixada
    /// no canto: 8 px a 96 dpi. Só vale no arraste (e na migração da fração antiga); depois disso o
    /// canto é a fração exata.
    /// </summary>
    public static int ToleranciaCanto(int dpi) => (int)Math.Round(8 * Math.Max(dpi, 96) / 96.0);

    /// <summary>
    /// A faixa está num canto da tela? Só então a reserva vale: no meio da borda, reservar a borda
    /// inteira encolheria toda janela maximizada por causa de um pedaço pequeno. Fork: canto é a
    /// fração exata 0 ou 1 — a faixa encostada no início ou no fim da área útil, que cobre a barra de
    /// tarefas numa borda vizinha. Medir em px fazia a mesma fração ser canto num monitor e não em
    /// outro (resolução, escala, número de grupos); o arraste já grava 0 ou 1 quando solta perto.
    /// Não oscila: a reserva não mexe na fração.
    /// </summary>
    public static bool NoCanto(double fracaoBorda) => fracaoBorda is 0.0 or 1.0;

    /// <summary>
    /// Espessura pedida ao shell com a opção ligada: a da faixa no canto, zero fora dele. A faixa
    /// continua registrada como AppBar nos dois casos, para receber ABN_FULLSCREENAPP; o retângulo de
    /// espessura zero, colado na borda e degenerado (borda superior: top = bottom = topo do monitor),
    /// não tira nada da área útil — o shell a calcula subtraindo os retângulos das AppBars.
    /// </summary>
    public static int EspessuraReserva(bool noCanto, int espessuraFaixa) => noCanto ? Math.Max(0, espessuraFaixa) : 0;

    /// <summary>
    /// O registro atual sobrevive a este relayout? Só se a opção continua ligada e ele está na mesma
    /// borda do mesmo monitor; senão sai antes de a área útil ser lida. Canto ou meio da borda não
    /// entram: a troca só muda a espessura pedida, sem tirar o registro.
    /// </summary>
    public static bool MantemReserva(AppBarReserva? atual, bool opcaoReserva, BordaTela borda, Rectangle monitor) =>
        opcaoReserva && atual is { } a && a.Borda == borda && a.Monitor == monitor;

    /// <summary>
    /// O que fazer com o registro no início de um relayout. Desligar (opção desligada ou fora do modo
    /// Notch) tira o registro e devolve a faixa para cima; Refazer (outra borda ou monitor) tira o
    /// registro guardando o estado de tela cheia, que o novo registro reaplica.
    /// </summary>
    public static AcaoRegistro AcaoAoRelayout(bool registrado, AppBarReserva? atual, bool opcaoReserva, BordaTela borda, Rectangle monitor) =>
        !registrado || MantemReserva(atual, opcaoReserva, borda, monitor) ? AcaoRegistro.Manter
        : opcaoReserva ? AcaoRegistro.Refazer
        : AcaoRegistro.Desligar;

    /// <summary>
    /// Área útil depois do ABM_REMOVE. O shell recalcula a área útil dentro do próprio ABM_REMOVE;
    /// quem fica para trás é o cache de Screen do WinForms. Vale então a área lida na hora do
    /// monitor (GetMonitorInfo), e não uma área sintetizada: outra AppBar na mesma borda continua
    /// descontada. Sem leitura válida (vazia ou fora do monitor), fica a do cache — um relayout a
    /// mais corrige quando ele atualizar.
    /// </summary>
    public static Rectangle AreaAposRemover(Rectangle? fresca, Rectangle cache, Rectangle monitor) =>
        fresca is { Width: > 0, Height: > 0 } f && monitor.Contains(f) ? f : cache;

    /// <summary>
    /// ABN_FULLSCREENAPP: com tela cheia aberta a faixa vai para o fundo (HWND_BOTTOM, como no exemplo
    /// "Using Application Desktop Toolbars" da Microsoft); HWND_NOTOPMOST a deixaria acima de toda
    /// janela comum, inclusive do app em tela cheia. Ao fechar, volta a ser topmost.
    /// </summary>
    public static IntPtr ZOrderTelaCheia(bool aberto) => aberto ? HwndBottom : HwndTopMost;

    public static readonly IntPtr HwndTopMost = new(-1);
    public static readonly IntPtr HwndBottom = new(1);

    /// <summary>Espessura da faixa perpendicular à borda — o quanto reservar.</summary>
    public static int Thickness(Rectangle faixa, BordaTela borda) =>
        borda is BordaTela.Topo or BordaTela.Base ? faixa.Height : faixa.Width;
}

/// <summary>Fork: o registro da faixa no começo de um relayout (ver <see cref="AppBarMath.AcaoAoRelayout"/>).</summary>
internal enum AcaoRegistro
{
    /// <summary>Sem registro, ou registro na mesma borda e monitor: fica como está.</summary>
    Manter,

    /// <summary>Outra borda ou monitor com a opção ligada: ABM_REMOVE guardando a tela cheia.</summary>
    Refazer,

    /// <summary>Opção desligada ou fora do modo Notch: ABM_REMOVE e a faixa volta para cima.</summary>
    Desligar,
}

/// <summary>Fork: o que o shell concedeu — borda, monitor e retângulo reservado.</summary>
internal readonly record struct AppBarReserva(BordaTela Borda, Rectangle Monitor, Rectangle Reservado);

/// <summary>O que fazer com o retângulo ajustado depois do ABM_QUERYPOS.</summary>
internal enum SetPosDecisao
{
    /// <summary>Pedido novo: vai ao shell por ABM_SETPOS.</summary>
    Enviar,

    /// <summary>Já pedido (ou já concedido): fica a concessão anterior, sem SETPOS.</summary>
    Reusar,

    /// <summary>
    /// Limite de SETPOS estourado: fica a concessão anterior durante o resfriamento, e o pedido
    /// novo vira uma reconciliação pendente para o fim dele.
    /// </summary>
    Adiado,
}

/// <summary>
/// Fork: decide se um ABN_POSCHANGED vira ABM_SETPOS. Cada SETPOS faz o shell avisar todas as
/// AppBars; responder a todo aviso com outro SETPOS fecharia um ciclo — com o próprio aviso, com um
/// shell que ajusta o retângulo concedido de forma diferente do pedido, ou com outra AppBar que
/// também responde. Duas travas, sem Win32 para ser testável:
///
/// 1. o retângulo é comparado com o último <em>pedido</em> (e com o último concedido), não só com
///    o concedido — pedido repetido não volta ao shell, por mais que ele o devolva diferente;
/// 2. no máximo <see cref="Maximo"/> SETPOS em <see cref="JanelaMs"/> ms; passou disso, um
///    resfriamento de <see cref="JanelaMs"/> ms sem SETPOS algum. A trava é temporária: o pedido
///    adiado fica <see cref="Pendente"/>, e quem chama refaz o QUERYPOS em
///    <see cref="ResfriamentoAte"/> (timer ou próximo aviso) — uma reconciliação, que só manda
///    SETPOS se o retângulo diferir do último pedido e do último concedido. Assim a reserva sempre
///    chega à posição estável, e no pior caso (shell que nunca estabiliza) são <see cref="Maximo"/>
///    SETPOS a cada janela mais resfriamento, nunca um ciclo livre.
///
/// <see cref="Liberar"/> (evento real: borda, monitor ou espessura novos, mudança de tela, Explorer
/// reiniciado, novo registro) zera tudo na hora.
/// </summary>
internal sealed class SetPosGuarda
{
    public const int Maximo = 3;
    public const long JanelaMs = 2000;

    private readonly Queue<long> _envios = new();

    public Rectangle? UltimoPedido { get; private set; }
    public Rectangle? UltimoConcedido { get; private set; }

    /// <summary>Fim do resfriamento em curso (relógio de <see cref="Decidir"/>), ou null sem resfriamento.</summary>
    public long? ResfriamentoAte { get; private set; }

    /// <summary>Um pedido diferente foi adiado: refazer o QUERYPOS em <see cref="ResfriamentoAte"/>.</summary>
    public bool Pendente { get; private set; }

    public SetPosDecisao Decidir(Rectangle ajustado, long agoraMs)
    {
        if (UltimoConcedido is not null && (ajustado == UltimoPedido || ajustado == UltimoConcedido))
        {
            Pendente = false;   // o shell voltou ao que já está pedido: nada a reconciliar
            return SetPosDecisao.Reusar;
        }

        if (ResfriamentoAte is { } fim)
        {
            if (agoraMs < fim)
            {
                Pendente = true;
                return SetPosDecisao.Adiado;
            }
            ResfriamentoAte = null;   // fim do resfriamento: esta é a reconciliação
            Pendente = false;
        }

        while (_envios.Count > 0 && agoraMs - _envios.Peek() >= JanelaMs) _envios.Dequeue();
        if (_envios.Count >= Maximo && UltimoConcedido is not null)   // sem concessão não há o que manter
        {
            ResfriamentoAte = agoraMs + JanelaMs;
            Pendente = true;
            return SetPosDecisao.Adiado;
        }
        return SetPosDecisao.Enviar;
    }

    /// <summary>Anota o SETPOS feito: o que foi pedido e o que o shell devolveu.</summary>
    public void Registrar(Rectangle pedido, Rectangle concedido, long agoraMs)
    {
        UltimoPedido = pedido;
        UltimoConcedido = concedido;
        _envios.Enqueue(agoraMs);
    }

    /// <summary>Evento real de mudança: zera o limite e esquece pedido e concessão.</summary>
    public void Liberar()
    {
        _envios.Clear();
        ResfriamentoAte = null;
        Pendente = false;
        UltimoPedido = null;
        UltimoConcedido = null;
    }
}

/// <summary>
/// Fork: tela cheia aberta por outro app (ABN_FULLSCREENAPP). O shell só avisa nas transições, então
/// o estado mora aqui, fora do handle: um RecreateHandle com o app ainda em tela cheia devolve a
/// janela nova como topmost, e sem o estado ela ficaria por cima dele até a próxima transição.
/// Cada método devolve o z-order a aplicar, ou null para não mexer.
/// </summary>
internal sealed class EstadoTelaCheia
{
    public bool Aberto { get; private set; }

    /// <summary>Aviso do shell.</summary>
    public IntPtr Avisar(bool aberto)
    {
        Aberto = aberto;
        return AppBarMath.ZOrderTelaCheia(aberto);
    }

    /// <summary>Janela (re)registrada: se a tela cheia continua aberta, volta para o fundo.</summary>
    public IntPtr? AoRegistrar() => Aberto ? AppBarMath.HwndBottom : null;

    /// <summary>
    /// Reserva desligada de vez (opção desligada, fora do modo Notch, ou shell recusou o registro):
    /// sem registro ninguém avisaria o fim da tela cheia, então a faixa volta para cima já e o estado
    /// é esquecido — como antes da 0.9.0, sem gestão de tela cheia.
    /// </summary>
    public IntPtr? AoDesligar()
    {
        if (!Aberto) return null;
        Aberto = false;
        return AppBarMath.HwndTopMost;
    }
}

/// <summary>
/// Fork: registra a faixa do modo Notch como AppBar (SHAppBarMessage), para que janelas maximizadas
/// parem antes dela em vez de ficarem por baixo. Uma instância por janela.
///
/// Com a opção ligada, a faixa fica registrada o tempo todo, no canto ou no meio da borda: fora do
/// canto o pedido tem espessura zero (<see cref="AppBarMath.EspessuraReserva"/>) e não reserva nada,
/// mas o shell continua mandando ABN_FULLSCREENAPP — sem registro não haveria aviso de tela cheia.
///
/// O registro some quando o Explorer reinicia (o shell novo não conhece a janela): a faixa recebe
/// "TaskbarCreated", chama <see cref="Lost"/> e registra de novo no próximo
/// <see cref="Reserve"/>. Trocar de borda ou de monitor tira o registro e refaz, para não deixar
/// a reserva antiga para trás.
/// </summary>
internal sealed class AppBar : IDisposable
{
    public const int AbnPosChanged = 1;
    public const int AbnFullScreenApp = 2;
    public const uint AbmActivate = 6;
    public const uint AbmWindowPosChanged = 9;

    private const uint AbmNew = 0;
    private const uint AbmRemove = 1;
    private const uint AbmQueryPos = 2;
    private const uint AbmSetPos = 3;

    /// <summary>Mensagem com que o shell avisa a AppBar (ABN_*).</summary>
    public static readonly int CallbackMessage = (int)RegisterWindowMessage("Gaugely.AppBar");

    /// <summary>Difundida quando o Explorer (re)cria a barra de tarefas.</summary>
    public static readonly int TaskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");

    private IntPtr _hwnd;
    private (BordaTela Borda, Rectangle Monitor, int Espessura)? _pedido;
    private (BordaTela Borda, Rectangle Monitor, int Espessura)? _base;
    private AppBarReserva? _atual;
    private readonly SetPosGuarda _guarda = new();
    private readonly EstadoTelaCheia _telaCheia = new();

    /// <summary>
    /// Fim do resfriamento da <see cref="SetPosGuarda"/> com pedido adiado: dispara
    /// <see cref="Reconciliar"/> na thread de UI. Criado só quando preciso.
    /// </summary>
    private System.Windows.Forms.Timer? _reconciliacao;

    /// <summary>
    /// Resfriamento acabou com um pedido adiado: refazer a reserva (Invalidate + Reserve) uma vez,
    /// para chegar à posição estável sem esperar outro aviso do shell.
    /// </summary>
    public event Action? Reconciliar;

    public bool Registered { get; private set; }

    /// <summary>Reserva concedida pelo último ABM_SETPOS, ou null sem registro.</summary>
    public AppBarReserva? Atual => _atual;

    /// <summary>
    /// Verdadeiro durante o ABM_SETPOS: o ABN_POSCHANGED que ele provoca é efeito do próprio ajuste
    /// e não deve disparar outro.
    /// </summary>
    public bool Ajustando { get; private set; }

    /// <summary>
    /// Reserva <paramref name="espessura"/> px na borda do monitor e devolve o retângulo que o shell
    /// concedeu, ou null se ele recusou o registro. Pedido igual ao anterior não vai ao shell de novo:
    /// cada ABM_SETPOS faz o Windows recalcular a área útil e reposicionar as janelas maximizadas.
    /// </summary>
    public Rectangle? Reserve(IntPtr hwnd, BordaTela borda, Rectangle monitor, int espessura)
    {
        if (Registered && (_hwnd != hwnd || _pedido is { } antes && (antes.Borda != borda || antes.Monitor != monitor)))
            Remove(manterTelaCheia: true);   // re-registra logo abaixo; a tela cheia continua aberta

        if (!Registered)
        {
            var novo = Data(hwnd);
            novo.uCallbackMessage = (uint)CallbackMessage;
            if (SHAppBarMessage(AbmNew, ref novo) == UIntPtr.Zero) return null;
            _hwnd = hwnd;
            Registered = true;
            _pedido = null;
            _base = null;
            _atual = null;
            _guarda.Liberar();
            // Handle recriado, borda ou monitor novos, ou Explorer reiniciado, com um app ainda em
            // tela cheia: o aviso não se repete, então o novo registro manda a faixa ao fundo.
            if (_telaCheia.AoRegistrar() is { } z) ZOrder(z);
        }

        var pedido = (borda, monitor, espessura);
        if (_pedido == pedido && _atual is { } igual) return igual.Reservado;

        // Borda, monitor ou espessura novos são mudança real: o limite de SETPOS recomeça. A troca
        // canto ↔ meio da borda muda a espessura (normal ↔ zero), então também.
        if (_base != pedido) _guarda.Liberar();
        _base = pedido;

        var dados = Data(hwnd);
        dados.uEdge = Edge(borda);
        dados.rc = RECT.From(AppBarMath.Request(monitor, borda, espessura));
        SHAppBarMessage(AbmQueryPos, ref dados);
        var ajustado = AppBarMath.Fit(dados.rc.ToRectangle(), borda, espessura);

        var agora = Environment.TickCount64;
        if (_guarda.Decidir(ajustado, agora) == SetPosDecisao.Enviar)
        {
            dados.rc = RECT.From(ajustado);
            Ajustando = true;
            try { SHAppBarMessage(AbmSetPos, ref dados); }
            finally { Ajustando = false; }
            _guarda.Registrar(ajustado, dados.rc.ToRectangle(), agora);
        }

        // Reusar ou Adiado: fica o que o shell concedeu da última vez (Decidir só não manda Enviar
        // quando há concessão). Adiado deixa a reconciliação marcada para o fim do resfriamento.
        if (_guarda.Pendente && _guarda.ResfriamentoAte is { } fim) AgendarReconciliacao(fim - agora);
        else PararReconciliacao();
        _atual = new AppBarReserva(borda, monitor, _guarda.UltimoConcedido!.Value);
        _pedido = pedido;
        return _atual.Value.Reservado;
    }

    /// <summary>ABN_POSCHANGED: a barra de tarefas ou outra AppBar mudou — o próximo pedido vai ao shell.</summary>
    public void Invalidate() => _pedido = null;

    /// <summary>Mudança real de tela (resolução, monitores): o limite de SETPOS recomeça.</summary>
    public void Liberar()
    {
        _guarda.Liberar();
        PararReconciliacao();
        _pedido = null;
    }

    private void AgendarReconciliacao(long emMs)
    {
        if (_reconciliacao is null)
        {
            _reconciliacao = new System.Windows.Forms.Timer();
            _reconciliacao.Tick += (_, _) =>
            {
                PararReconciliacao();
                if (Registered && _guarda.Pendente) Reconciliar?.Invoke();
            };
        }
        _reconciliacao.Stop();
        _reconciliacao.Interval = (int)Math.Clamp(emMs, 1, SetPosGuarda.JanelaMs);
        _reconciliacao.Start();
    }

    private void PararReconciliacao() => _reconciliacao?.Stop();

    public void Dispose()
    {
        _reconciliacao?.Dispose();
        _reconciliacao = null;
    }

    /// <summary>O Explorer reiniciou e esqueceu o registro; não adianta mandar ABM_REMOVE.</summary>
    public void Lost()
    {
        Registered = false;
        _pedido = null;
        _base = null;
        _atual = null;
        _guarda.Liberar();
        PararReconciliacao();
    }

    /// <summary>
    /// Tira o registro e devolve a reserva que havia. <paramref name="manterTelaCheia"/> é para o
    /// handle que vai ser recriado: o estado de tela cheia sobrevive e é reaplicado no novo registro.
    /// Fora disso a reserva saiu de vez e a faixa volta para cima.
    /// </summary>
    public AppBarReserva? Remove(bool manterTelaCheia = false)
    {
        if (!manterTelaCheia && _telaCheia.AoDesligar() is { } z) ZOrder(z);
        PararReconciliacao();
        if (!Registered) return null;
        var removida = _atual;
        var dados = Data(_hwnd);
        SHAppBarMessage(AbmRemove, ref dados);
        Registered = false;
        _pedido = null;
        _base = null;
        _atual = null;
        _guarda.Liberar();
        return removida;
    }

    /// <summary>
    /// Área útil do monitor lida na hora (GetMonitorInfo), sem o cache de Screen do WinForms; null
    /// se a chamada falhar.
    /// </summary>
    public static Rectangle? AreaUtilAgora(Rectangle monitor)
    {
        var rc = RECT.From(monitor);
        var hmon = MonitorFromRect(ref rc, MonitorDefaultToNearest);
        if (hmon == IntPtr.Zero) return null;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(hmon, ref info) ? info.rcWork.ToRectangle() : null;
    }

    /// <summary>ABM_ACTIVATE / ABM_WINDOWPOSCHANGED, que o shell pede a toda AppBar registrada.</summary>
    public void Notify(uint abm)
    {
        if (!Registered) return;
        var dados = Data(_hwnd);
        SHAppBarMessage(abm, ref dados);
    }

    /// <summary>
    /// ABN_FULLSCREENAPP: com um app em tela cheia (vídeo, jogo, apresentação) a faixa vai para o
    /// fundo; quando ele fecha, volta a ficar por cima. Sem ativar a janela.
    /// </summary>
    public void FullScreen(bool aberto) => ZOrder(_telaCheia.Avisar(aberto));

    private void ZOrder(IntPtr depoisDe)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, depoisDe, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static APPBARDATA Data(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
    };

    private static uint Edge(BordaTela borda) => borda switch
    {
        BordaTela.Esquerda => 0,  // ABE_LEFT
        BordaTela.Topo => 1,      // ABE_TOP
        BordaTela.Direita => 2,   // ABE_RIGHT
        _ => 3,                   // ABE_BOTTOM
    };

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public static RECT From(Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
