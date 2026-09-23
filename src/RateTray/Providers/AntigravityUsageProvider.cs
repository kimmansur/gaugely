using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RateTray.Configuration;
using RateTray.Localization;
using RateTray.Model;

namespace RateTray.Providers;

/// <summary>
/// Fork: cota do Google AI Pro pelo Antigravity. O app não toca no login do Google:
/// roda o próprio <c>agy -p /usage --output-format json</c>, que usa o OAuth que o agy guardou no
/// cofre do Windows, e lê só o relatório. Não gasta token (o relatório vem com total_tokens 0).
/// </summary>
public sealed class AntigravityUsageProvider(AntigravityOptions options) : IUsageProvider
{
    internal const string GroupName = "Antigravity";

    /// <summary>
    /// Caminho fixo do instalador oficial, sem campo no settings.json — mesmo princípio da FORK-2:
    /// um arquivo de ajustes adulterado não consegue trocar o executável que o app dispara.
    /// </summary>
    internal static string ExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");

    private static string StateDir => Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gaugely")).FullName;

    private static string WorkDir => Directory.CreateDirectory(Path.Combine(StateDir, "agy-cwd")).FullName;

    public string Group => GroupName;

    /// <summary>Sem agy instalado o provedor fica desligado, não em erro.</summary>
    public bool Enabled => options.Enabled && File.Exists(ExecutablePath);

    public TimeSpan MinInterval => TimeSpan.FromSeconds(Math.Max(0, options.MinIntervalSeconds));

    public async Task<ProviderResult> ReadAsync(CancellationToken ct)
    {
        var auth = new AuthStatus { Group = Group, IsValid = true, Detail = "Google AI Pro" };

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Log num arquivo só, sobrescrito a cada consulta: sem isso o agy cria um log novo por
        // execução em ~/.gemini/antigravity-cli/log, perto de 240 por dia.
        foreach (var arg in (string[])["-p", "/usage", "--output-format", "json", "--log-file", Path.Combine(StateDir, "agy-usage.log")])
            startInfo.ArgumentList.Add(arg);

        // Sem esta variável, a cada ~18 min o agy dispara o atualizador em segundo plano num
        // console próprio, que ignora o CREATE_NO_WINDOW e pisca no Windows Terminal (visto em
        // 16/09/2026). Vale só para estas consultas; o agy do usuário continua se atualizando.
        startInfo.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";

        // Pasta vazia e própria: herdado do autostart, o diretório era C:\WINDOWS\system32, e o
        // agy o registrava como workspace confiável.
        startInfo.WorkingDirectory = WorkDir;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));

        Process? process = null;
        Task<string>? stderrTask = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null) return ProviderResult.Failed(Group, Loc.T("error.antigravity.startFailed")) with { Auth = auth };

            stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            
            // Fork: Espere o stderr no máximo 2 s depois de WaitForExitAsync, ignorando timeout ou erro
            try { _ = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }

            var readings = Parse(stdout, out var status);
            if (status is not null && !status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                return ProviderResult.Failed(Group, Loc.T("error.antigravity.status", status)) with { Auth = auth with { IsValid = false } };

            return readings.Count == 0
                ? ProviderResult.Failed(Group, Loc.T("error.antigravity.noLimits")) with { Auth = auth }
                : ProviderResult.Success(Group, readings) with { Auth = auth };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Failed(Group, Loc.T("error.antigravity.timeout", options.TimeoutSeconds)) with { Auth = auth };
        }
        catch (Exception ex)
        {
            return ProviderResult.Failed(Group, Loc.T("error.fetchFailed", ex.Message)) with { Auth = auth };
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            
            // Fork: a leitura do stderr pode falhar depois daqui (pipe fechado no Dispose). A continuação
            // observa a exceção quando ela vier; checar só IsFaulted agora deixava a falha tardia órfã.
            stderrTask?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            
            process?.Dispose();
        }
    }

    /// <summary>
    /// Lê <c>command.data.groups[].buckets[]</c>. O relatório traz o que <b>sobra</b>
    /// (remaining_fraction); a bandeja mostra o que foi <b>usado</b>, como nos demais serviços.
    /// </summary>
    internal static List<LimitReading> Parse(string json, out string? status)
    {
        status = null;
        var readings = new List<LimitReading>();

        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return readings; }
        if (root is null) return readings;

        status = root["status"] is JsonValue s && s.TryGetValue<string>(out var text) ? text : null;

        if (root["command"]?["data"]?["groups"] is not JsonArray groups) return readings;

        foreach (var group in groups.OfType<JsonObject>())
        {
            var name = group["name"] is JsonValue n && n.TryGetValue<string>(out var gn) ? gn : "";
            var (key, label) = name.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase)
                ? ("gemini", "Gemini")
                : ("3p", "Claude/GPT");

            if (group["buckets"] is not JsonArray buckets) continue;

            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                if (bucket["remaining_fraction"] is not JsonValue rf || !rf.TryGetValue<double>(out var remaining)
                    || !double.IsFinite(remaining)) continue;

                var windowName = bucket["window"] is JsonValue w && w.TryGetValue<string>(out var wn) ? wn : "";
                var (suffix, window, windowLabel) = windowName switch
                {
                    "5h" => ("5h", TimeSpan.FromHours(5), "5 h"),
                    "weekly" => ("week", TimeSpan.FromDays(7), Loc.T("window.week")),
                    _ => ((string?)null, TimeSpan.Zero, ""),
                };
                if (suffix is null) continue;

                DateTimeOffset? reset = bucket["reset_time"] is JsonValue r && r.TryGetValue<string>(out var raw)
                    && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : null;

                readings.Add(new LimitReading
                {
                    Id = $"antigravity.{key}.{suffix}",
                    Label = $"{label} · {windowLabel}",
                    Group = GroupName,
                    Percent = Math.Clamp(100.0 * (1 - remaining), 0, 100),
                    ResetsAt = reset,
                    Window = window,
                    IsActive = key == "gemini" && suffix == "5h",
                });
            }
        }

        return readings
            .Select((reading, index) => reading with { Variant = index, VariantCount = readings.Count })
            .ToList();
    }
}
