using System.Runtime.InteropServices;

namespace RateTray.Providers;

/// <summary>
/// Fork: a trava que o Claude Code põe no diretório das credenciais antes de renovar o token.
/// Ele usa proper-lockfile, que cria o diretório <c>&lt;dir&gt;.lock</c>, renova a data de
/// modificação dele a cada metade do prazo e o considera abandonado quando essa data passa de
/// 10 s. Aqui valem as mesmas regras, para que os dois programas nunca troquem o mesmo refresh
/// token ao mesmo tempo:
/// <list type="bullet">
/// <item>enquanto a trava é nossa, a data é renovada a cada 5 s, então uma renovação lenta não
/// parece abandonada;</item>
/// <item>a trava é reconhecida pela data que nós mesmos gravamos — como o proper-lockfile faz.
/// Se a data mudou, outro processo a tomou, e ela não é renovada nem apagada por nós;</item>
/// <item>nada é gravado dentro do diretório: o proper-lockfile o remove com <c>rmdir</c>, que
/// falha em diretório com conteúdo.</item>
/// </list>
/// </summary>
internal sealed class RefreshLock : IDisposable
{
    /// <summary>O <c>stale</c> padrão do proper-lockfile.</summary>
    internal static readonly TimeSpan Stale = TimeSpan.FromSeconds(10);

    /// <summary>O <c>update</c> padrão do proper-lockfile: metade do prazo.</summary>
    internal static readonly TimeSpan Touch = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private DateTime _stamp;
    private bool _lost;
    private bool _disposed;

    private RefreshLock(string path, DateTime stamp, TimeSpan touch)
    {
        _path = path;
        _stamp = stamp;
        _timer = new System.Threading.Timer(_ => Renew(), null, touch, touch);
    }

    internal static string PathFor(string directory) => Path.TrimEndingDirectorySeparator(directory) + ".lock";

    /// <summary>False quando outro processo tomou a trava enquanto ela era nossa.</summary>
    internal bool Held
    {
        get { lock (_gate) return !_lost && !_disposed && Owns(); }
    }

    /// <summary>
    /// Tenta pegar a trava por até <paramref name="wait"/>. Trava abandonada é removida e tomada,
    /// como o proper-lockfile faz. Devolve null se outro processo a segurar até o fim da espera.
    /// </summary>
    internal static async Task<RefreshLock?> AcquireAsync(string directory, CancellationToken ct,
        TimeSpan? wait = null, TimeSpan? touch = null)
    {
        var path = PathFor(directory);
        var until = DateTime.UtcNow + (wait ?? DefaultWait);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (TryCreate(path))
            {
                var stamp = Stamp(path);
                if (stamp is not null) return new RefreshLock(path, stamp.Value, touch ?? Touch);
                TryRemove(path, recursive: false);   // criada mas sem data gravável: não fica para trás
                return null;
            }

            if (IsStale(path) && TryRemove(path, recursive: true)) continue;

            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(Step, ct).ConfigureAwait(false);
        }
    }

    private void Renew()
    {
        lock (_gate)
        {
            if (_lost || _disposed) return;
            if (!Owns())
            {
                _lost = true;
                return;
            }

            if (Stamp(_path) is { } stamp) _stamp = stamp;
            else _lost = true;
        }
    }

    /// <summary>A trava ainda é a que criamos: existe e tem a data que gravamos por último.</summary>
    private bool Owns()
    {
        try
        {
            return Directory.Exists(_path) && Directory.GetLastWriteTimeUtc(_path) == _stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Grava agora como data de modificação e devolve o valor lido de volta.</summary>
    private static DateTime? Stamp(string path)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Criação exclusiva: falha se o diretório já existe. <see cref="Directory.CreateDirectory(string)"/>
    /// não serve, porque aceita um diretório existente sem erro.
    /// </summary>
    private static bool TryCreate(string path) => CreateDirectoryW(path, IntPtr.Zero);

    private static bool IsStale(string path)
    {
        try
        {
            return Directory.Exists(path) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path) > Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryRemove(string path, bool recursive)
    {
        try
        {
            Directory.Delete(path, recursive);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            if (!_lost && Owns()) TryRemove(_path, recursive: false);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
