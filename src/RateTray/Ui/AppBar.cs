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

    /// <summary>Espessura da faixa perpendicular à borda — o quanto reservar.</summary>
    public static int Thickness(Rectangle faixa, BordaTela borda) =>
        borda is BordaTela.Topo or BordaTela.Base ? faixa.Height : faixa.Width;
}

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
    private Rectangle _reservado;
    private bool _abaixoDeTelaCheia;

    public bool Registered { get; private set; }

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
        }

        var pedido = (borda, monitor, espessura);
        if (_pedido == pedido) return _reservado;

        var dados = Data(hwnd);
        dados.uEdge = Edge(borda);
        dados.rc = RECT.From(AppBarMath.Request(monitor, borda, espessura));
        SHAppBarMessage(AbmQueryPos, ref dados);
        dados.rc = RECT.From(AppBarMath.Fit(dados.rc.ToRectangle(), borda, espessura));
        SHAppBarMessage(AbmSetPos, ref dados);

        _reservado = dados.rc.ToRectangle();
        _pedido = pedido;
        return _reservado;
    }

    /// <summary>ABN_POSCHANGED: a barra de tarefas ou outra AppBar mudou — o próximo pedido vai ao shell.</summary>
    public void Invalidate() => _pedido = null;

    /// <summary>O Explorer reiniciou e esqueceu o registro; não adianta mandar ABM_REMOVE.</summary>
    public void Lost()
    {
        Registered = false;
        _pedido = null;
    }

    public void Remove()
    {
        if (!Registered) return;
        var dados = Data(_hwnd);
        SHAppBarMessage(AbmRemove, ref dados);
        Registered = false;
        _pedido = null;
        if (_abaixoDeTelaCheia) FullScreen(false);   // sem reserva, ninguém mais avisaria a volta
    }

    /// <summary>ABM_ACTIVATE / ABM_WINDOWPOSCHANGED, que o shell pede a toda AppBar registrada.</summary>
    public void Notify(uint abm)
    {
        if (!Registered) return;
        var dados = Data(_hwnd);
        SHAppBarMessage(abm, ref dados);
    }

    /// <summary>
    /// ABN_FULLSCREENAPP: com um app em tela cheia (vídeo, jogo, apresentação) a faixa sai de cima;
    /// quando ele fecha, volta a ficar por cima. Sem ativar a janela.
    /// </summary>
    public void FullScreen(bool aberto)
    {
        if (_hwnd == IntPtr.Zero) return;
        _abaixoDeTelaCheia = aberto;
        SetWindowPos(_hwnd, aberto ? HwndNoTopMost : HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
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

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
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
