using System.Runtime.InteropServices;
using System.Text;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace RateTray.Configuration;

/// <summary>
/// Fork: chaves de API no Gerenciador de Credenciais do Windows.
///
/// Por que o cofre e não o settings.json: o arquivo de ajustes é texto puro em %APPDATA%, que
/// qualquer processo do usuário lê e escreve, e já foi tratado como superfície de ataque (FORK-1
/// trava o destino do token, FORK-2 trava o executável). Uma chave de API ali seria copiada junto
/// com o arquivo, cairia em backup e em print de "olha meu settings.json". No cofre ela fica
/// cifrada pelo DPAPI da conta, fora de qualquer arquivo que o app escreva, e o usuário a vê e
/// remove em Painel de Controle > Gerenciador de Credenciais.
///
/// Regras deste arquivo: o segredo nunca é logado, nunca vai para mensagem de exceção e nunca é
/// devolvido pela interface além de <see cref="Read"/> — quem só precisa saber se há chave usa
/// <see cref="Has"/>, que nem decodifica o conteúdo.
/// </summary>
public static class CredentialVault
{
    public const string Kimi = "kimi";
    public const string OpenRouter = "openrouter";
    public const string OpenAIAdmin = "openai-admin";
    public const string AnthropicAdmin = "anthropic-admin";
    public const string KimiPlatform = "kimi-platform";
    public const string DeepSeek = "deepseek";

    /// <summary>Todos os serviços com chave no cofre, na ordem em que aparecem nos ajustes.</summary>
    public static readonly IReadOnlyList<string> Services =
        [Kimi, KimiPlatform, OpenRouter, OpenAIAdmin, AnthropicAdmin, DeepSeek];

    private const uint CredTypeGeneric = 1;

    /// <summary>
    /// Apesar do nome, é por usuário: a credencial fica no perfil da conta e sobrevive a logoff,
    /// mas não viaja com perfil móvel (CRED_PERSIST_ENTERPRISE faria isso — não queremos).
    /// </summary>
    private const uint CredPersistLocalMachine = 2;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes, ou 1280 caracteres UTF-16.</summary>
    private const int MaxBlobBytes = 5 * 512;

    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    // Ponteiros crus (IntPtr) em vez de strings marshaladas: assim o buffer do segredo é nosso,
    // e dá para zerá-lo antes de liberar em vez de deixar uma cópia solta na memória nativa.
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>
    /// Alvo fixo por serviço. Nome desconhecido é erro de programação, não de usuário — e a
    /// mensagem só cita o nome do serviço, nunca conteúdo.
    /// </summary>
    internal static string TargetFor(string service) => service switch
    {
        Kimi => "Gaugely/kimi",
        OpenRouter => "Gaugely/openrouter",
        OpenAIAdmin => "Gaugely/openai-admin",
        AnthropicAdmin => "Gaugely/anthropic-admin",
        KimiPlatform => "Gaugely/kimi-platform",
        DeepSeek => "Gaugely/deepseek",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "serviço sem alvo no cofre"),
    };

    /// <summary>
    /// Alvo usado antes da renomeação do app. Só é lido, para migrar, e fica no lugar — a versão
    /// anterior continua funcionando se for preciso voltar a ela. A única exceção é o usuário
    /// remover a chave: aí sai dos dois, senão ela voltaria da migração na leitura seguinte.
    /// </summary>
    internal static string? LegacyTargetFor(string service) => service switch
    {
        Kimi => "RateTray-Nox/kimi",
        OpenRouter => "RateTray-Nox/openrouter",
        _ => null,                                   // serviço que não existia antes da renomeação
    };

    /// <summary>
    /// A chave guardada, ou null se não há (ou se o cofre não respondeu). Chave que só existe sob o
    /// nome antigo é copiada uma vez para o alvo novo na primeira leitura.
    /// </summary>
    public static string? Read(string service)
    {
        var secret = ReadTarget(TargetFor(service));
        if (secret is not null) return secret;

        if (LegacyTargetFor(service) is not { } legacyTarget) return null;

        var legacy = ReadTarget(legacyTarget);
        if (legacy is not null) Write(service, legacy);
        return legacy;
    }

    private static string? ReadTarget(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle)) return null;

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(handle);
            var size = (int)Math.Min(cred.CredentialBlobSize, (uint)MaxBlobBytes);
            if (cred.CredentialBlob == IntPtr.Zero || size < 2) return null;

            var bytes = new byte[size & ~1];            // UTF-16: número par de bytes
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                var secret = Encoding.Unicode.GetString(bytes).Trim();
                return secret.Length == 0 ? null : secret;
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <summary>
    /// Grava (ou substitui) a chave. Falso quando vazia, grande demais para o cofre ou recusada
    /// pelo Windows — o chamador mostra "não configurada", sem precisar saber o motivo exato.
    /// </summary>
    public static bool Write(string service, string secret)
    {
        var target = TargetFor(service);
        var clean = secret?.Trim() ?? "";
        if (clean.Length == 0) return false;

        var bytes = Encoding.Unicode.GetBytes(clean);
        if (bytes.Length > MaxBlobBytes)
        {
            Array.Clear(bytes);
            return false;
        }

        var targetPtr = IntPtr.Zero;
        var userPtr = IntPtr.Zero;
        var blobPtr = IntPtr.Zero;
        try
        {
            targetPtr = Marshal.StringToHGlobalUni(target);
            userPtr = Marshal.StringToHGlobalUni("Gaugely");
            blobPtr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);

            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
            };

            return CredWrite(ref cred, 0);
        }
        finally
        {
            Array.Clear(bytes);
            if (blobPtr != IntPtr.Zero)
            {
                // Zera a cópia nativa antes de devolver a memória.
                Marshal.Copy(new byte[bytes.Length], 0, blobPtr, bytes.Length);
                Marshal.FreeHGlobal(blobPtr);
            }
            if (userPtr != IntPtr.Zero) Marshal.FreeHGlobal(userPtr);
            if (targetPtr != IntPtr.Zero) Marshal.FreeHGlobal(targetPtr);
        }
    }

    /// <summary>Remove a chave. Verdadeiro também quando ela já não existia: o estado final é o pedido.</summary>
    public static bool Delete(string service) =>
        DeleteTarget(TargetFor(service)) & (LegacyTargetFor(service) is not { } legacy || DeleteTarget(legacy));

    private static bool DeleteTarget(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        return Marshal.GetLastWin32Error() == ErrorNotFound;
    }

    /// <summary>
    /// Há chave guardada? Consultado a cada ciclo pelo <c>Enabled</c> dos provedores, para que a
    /// chave colada nos ajustes passe a valer sem reiniciar. Não decodifica o segredo.
    /// </summary>
    public static bool Has(string service) =>
        HasTarget(TargetFor(service)) || (LegacyTargetFor(service) is { } legacy && HasTarget(legacy));

    private static bool HasTarget(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle)) return false;

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(handle);
            return cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize >= 2;
        }
        finally
        {
            CredFree(handle);
        }
    }
}
