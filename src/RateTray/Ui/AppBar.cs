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
    /// útil, então não importa quando ela é lida.
    /// </summary>
    public static Rectangle LayoutArea(Rectangle areaUtil, BordaTela borda, Rectangle reservado)
    {
        if (reservado.Width <= 0 || reservado.Height <= 0) return areaUtil;

        return borda switch
        {
            BordaTela.Esquerda => Rectangle.FromLTRB(reservado.Left, areaUtil.Top, areaUtil.Right, areaUtil.Bottom),
            BordaTela.Direita => Rectangle.FromLTRB(areaUtil.Left, areaUtil.Top, reservado.Right, areaUtil.Bottom),
            BordaTela.Topo => Rectangle.FromLTRB(areaUtil.Left, reservado.Top, areaUtil.Right, areaUtil.Bottom),
            _ => Rectangle.FromLTRB(areaUtil.Left, areaUtil.Top, areaUtil.Right, reservado.Bottom),
        };
    }

    /// <summary>
    /// Só manda ABM_SETPOS se o retângulo ajustado mudou. Cada SETPOS faz o shell avisar as AppBars
    /// (ABN_POSCHANGED); repetir o mesmo retângulo a cada aviso fecharia um ciclo — com a própria
    /// faixa ou com outra AppBar que também responda ao aviso pedindo de novo.
    /// </summary>
    public static bool PrecisaSetPos(Rectangle? aprovado, Rectangle ajustado) => aprovado != ajustado;

    /// <summary>
    /// A reserva atual sobrevive a este relayout? Só se ainda é querida e continua na mesma borda do
    /// mesmo monitor; senão sai antes de a área útil ser lida.
    /// </summary>
    public static bool MantemReserva(AppBarReserva? atual, bool querReserva, BordaTela borda, Rectangle monitor) =>
        querReserva && atual is { } a && a.Borda == borda && a.Monitor == monitor;

    /// <summary>
    /// Área útil sem a reserva que acabou de sair. O Windows (e o cache de Screen do WinForms) pode
    /// ainda não ter devolvido o espaço: o lado da borda reservada volta para a face externa da
    /// reserva. Se a área útil já foi atualizada, o resultado é o mesmo.
    /// </summary>
    public static Rectangle AreaSemReserva(Rectangle areaUtil, Rectangle monitor, AppBarReserva? removida) =>
        removida is { } r && r.Monitor == monitor ? LayoutArea(areaUtil, r.Borda, r.Reservado) : areaUtil;

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

/// <summary>Fork: o que o shell concedeu — borda, monitor e retângulo reservado.</summary>
internal readonly record struct AppBarReserva(BordaTela Borda, Rectangle Monitor, Rectangle Reservado);

/// <summary>
/// Fork: registra a faixa do modo Notch como AppBar (SHAppBarMessage), para que janelas maximizadas
/// parem antes dela em vez de ficarem por baixo. Uma instância por janela.
///
/// O registro some quando o Explorer reinicia (o shell novo não conhece a janela): a faixa recebe
/// "TaskbarCreated", chama <see cref="Lost"/> e registra de novo no próximo
/// <see cref="Reserve"/>. Trocar de borda ou de monitor tira o registro e refaz, para não deixar
/// a reserva antiga para trás.
/// </summary>
internal sealed class AppBar
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
    private AppBarReserva? _atual;
    private bool _abaixoDeTelaCheia;

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
            Remove();

        if (!Registered)
        {
            var novo = Data(hwnd);
            novo.uCallbackMessage = (uint)CallbackMessage;
            if (SHAppBarMessage(AbmNew, ref novo) == UIntPtr.Zero) return null;
            _hwnd = hwnd;
            Registered = true;
            _pedido = null;
            _atual = null;
        }

        var pedido = (borda, monitor, espessura);
        if (_pedido == pedido && _atual is { } igual) return igual.Reservado;

        var dados = Data(hwnd);
        dados.uEdge = Edge(borda);
        dados.rc = RECT.From(AppBarMath.Request(monitor, borda, espessura));
        SHAppBarMessage(AbmQueryPos, ref dados);
        var ajustado = AppBarMath.Fit(dados.rc.ToRectangle(), borda, espessura);

        // Mesmo retângulo já aprovado (ex.: o ABN_POSCHANGED veio de outra AppBar e não mexeu nesta
        // borda): não há o que pedir, e um SETPOS repetido só geraria outro aviso.
        if (AppBarMath.PrecisaSetPos(_atual?.Reservado, ajustado))
        {
            dados.rc = RECT.From(ajustado);
            Ajustando = true;
            try { SHAppBarMessage(AbmSetPos, ref dados); }
            finally { Ajustando = false; }
            _atual = new AppBarReserva(borda, monitor, dados.rc.ToRectangle());
        }
        else
        {
            _atual = new AppBarReserva(borda, monitor, ajustado);
        }

        _pedido = pedido;
        return _atual.Value.Reservado;
    }

    /// <summary>ABN_POSCHANGED: a barra de tarefas ou outra AppBar mudou — o próximo pedido vai ao shell.</summary>
    public void Invalidate() => _pedido = null;

    /// <summary>O Explorer reiniciou e esqueceu o registro; não adianta mandar ABM_REMOVE.</summary>
    public void Lost()
    {
        Registered = false;
        _pedido = null;
        _atual = null;
    }

    /// <summary>Tira o registro e devolve a reserva que havia, para o layout descontá-la.</summary>
    public AppBarReserva? Remove()
    {
        if (!Registered) return null;
        var removida = _atual;
        var dados = Data(_hwnd);
        SHAppBarMessage(AbmRemove, ref dados);
        Registered = false;
        _pedido = null;
        _atual = null;
        if (_abaixoDeTelaCheia) FullScreen(false);   // sem reserva, ninguém mais avisaria a volta
        return removida;
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
    public void FullScreen(bool aberto)
    {
        if (_hwnd == IntPtr.Zero) return;
        _abaixoDeTelaCheia = aberto;
        SetWindowPos(_hwnd, AppBarMath.ZOrderTelaCheia(aberto), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
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

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
