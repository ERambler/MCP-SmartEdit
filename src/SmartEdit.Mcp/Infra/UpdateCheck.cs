using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartEdit.Mcp.Infra;

/// <summary>
/// Проверка обновлений при запуске (v0.6): GitHub latest release → системный диалог
/// «скачать новую версию?» (ОК/Отмена) с описанием релиза.
/// Ненавязчиво: отказ от vN запоминается — о vN больше не спрашиваем, спросим только когда выйдет vN+1.
/// Ничего не блокирует: проверка и диалог — в фоновом потоке, stdio живёт своей жизнью.
/// Выключить: аргумент --no-update или SMARTEDIT_NO_UPDATE_CHECK=1|true|yes.
/// Windows — MessageBoxW (user32); Linux — zenity/kdialog/xmessage при наличии графической среды
/// (DISPLAY/WAYLAND_DISPLAY); без неё или без утилит — только запись в журнал (агент увидит через log).
/// ОК → zip релиза скачивается в &lt;DataRoot&gt;\downloads и открывается папка; подменять работающий exe не берёмся.
/// </summary>
public static class UpdateCheck {
    const string Repo = "ERambler/MCP-SmartEdit";
    static readonly string DeclinedPath = Path.Combine(AppPaths.DataRoot, "update-declined.txt");

    /// <summary>Проверить обновление в фоне (однократно за запуск процесса). Вызов из Program после selftest-ветки.</summary>
    public static void RunIfNeeded(string[] args) {
        if (DisabledByArgs(args) || EnvDisabled()) return;
        var t = new Thread(CheckAndPrompt) { IsBackground = true, Name = "update-check" };
        t.Start();
    }

    /// <summary>Ключ/параметр без обновления: --no-update.</summary>
    internal static bool DisabledByArgs(string[] args) =>
        args.Contains("--no-update", StringComparer.OrdinalIgnoreCase);

    static bool EnvDisabled() =>
        Environment.GetEnvironmentVariable("SMARTEDIT_NO_UPDATE_CHECK") is { } v
        && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    static void CheckAndPrompt() {
        try {
            string? tag = null, body = "", assetUrl = null, assetName = null;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var http = new HttpClient()) {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartEdit-MCP-update-check"); // GitHub API без UA → 403
                using (var rsp = http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token).GetAwaiter().GetResult()) {
                    if (!rsp.IsSuccessStatusCode) { Log.Debug($"update: latest release → HTTP {(int)rsp.StatusCode}"); return; }
                    using var doc = JsonDocument.Parse(rsp.Content.ReadAsStream(cts.Token));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String) return;
                    tag = t.GetString()!;
                    if (root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) body = b.GetString() ?? "";
                    if (root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                        foreach (var asset in a.EnumerateArray())
                            if (asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                && asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                                && IsPlatformAsset(n.GetString()!)) {
                                assetName = n.GetString()!; assetUrl = u.GetString()!; break;
                            }
                }
            }

            string current = Rpc.McpProtocolHandler.ServerVersion;
            if (!IsNewer(tag, current)) return;
            if (assetUrl is null) {
                Log.Info($"update: доступна {tag} (у вас v{current}), но нет ассета под эту ОС — github.com/{Repo}/releases");
                return;
            }
            if (IsDeclined(DeclinedPath, tag)) {
                Log.Debug($"update: {tag} уже отклонена пользователем — молчим, пока не выйдет следующая");
                return;
            }

            bool? ok = Prompt(PromptText(tag!, current, body));
            if (ok is null) {
                Log.Info($"update: доступна {tag} (у вас v{current}) — диалог не показан (нет графической среды?), скачать: github.com/{Repo}/releases");
                return;
            }
            if (ok == true) Download(tag!, assetUrl, assetName!);
            else WriteDeclined(DeclinedPath, tag!); // однократность: эту версию больше не предлагаем
        }
        catch (Exception ex) { Log.Debug($"update: проверка не удалась: {ex.Message}"); }
    }

    /// <summary>vX.Y.Z (ведущая v необязательна) строго больше current; кривые теги → false.</summary>
    internal static bool IsNewer(string? tag, string current) {
        if (tag is null || !TryVersion(tag.TrimStart('v'), out var maj, out var min, out var pat)) return false;
        if (!TryVersion(current.TrimStart('v'), out var cmaj, out var cmin, out var cpat)) return false;
        return (maj, min, pat).CompareTo((cmaj, cmin, cpat)) > 0;
    }

    static bool TryVersion(string s, out int a, out int b, out int c) {
        a = b = c = 0;
        var parts = s.Split('.');
        if (parts.Length < 1 || !int.TryParse(parts[0], out a)) return false;
        if (parts.Length > 1 && !int.TryParse(parts[1], out b)) return false;
        if (parts.Length > 2 && !int.TryParse(parts[2], out c)) return false;
        return true;
    }

    static bool IsPlatformAsset(string name) => OperatingSystem.IsWindows()
        ? name.StartsWith("SmartEdit-win-x64-", StringComparison.Ordinal)
        : name.StartsWith("SmartEdit-linux", StringComparison.Ordinal);

    internal static bool IsDeclined(string path, string tag) {
        try { return File.Exists(path) && File.ReadAllText(path).Trim() == tag; }
        catch (IOException) { return false; }
    }

    internal static void WriteDeclined(string path, string tag) {
        try { File.WriteAllText(path, tag); } catch (IOException) { /* не смогли запомнить — переспросим, не страшно */ }
    }

    /// <summary>Текст диалога: версии + описание релиза (длинные notes обрезаются).</summary>
    internal static string PromptText(string tag, string current, string body) {
        var notes = body.Trim();
        if (notes.Length > 1200) notes = notes[..1200] + "…";
        return $"Доступна новая версия {tag} (у вас v{current}).\n\n{notes}\n\nСкачать новую версию?";
    }

    /// <summary>Диалог ОК/Отмена; null — показать не удалось (нет GUI) или пользователь не ответил (таймаут).</summary>
    static bool? Prompt(string text) =>
        OperatingSystem.IsWindows() ? PromptWindows(text) : PromptLinux(text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    static bool? PromptWindows(string text) {
        const uint MB_OKCANCEL = 0x1, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        int r = MessageBoxW(IntPtr.Zero, text, "SmartEdit MCP — обновление",
            MB_OKCANCEL | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST);
        return r switch { 1 => true, 2 => false, _ => (bool?)null }; // 0 = окна нет (сервис/без десктопа)
    }

    static bool? PromptLinux(string text) {
        if (Environment.GetEnvironmentVariable("DISPLAY") is null
            && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null) return null;
        var variants = new (string exe, string[] args, Func<int, bool?> map)[] {
            ("zenity",   new[] { "--question", "--title", "SmartEdit MCP — обновление", "--text", text,
                                 "--ok-label", "Скачать", "--cancel-label", "Отмена" },
                        code => code == 0 ? true : code == 1 ? false : null),
            ("kdialog",  new[] { "--title", "SmartEdit MCP — обновление", "--yesno", text },
                        code => code == 0 ? true : code == 1 ? false : null),
            ("xmessage", new[] { "-buttons", "Скачать:10,Отмена:20", text },
                        code => code == 10 ? true : code == 20 ? false : null),
        };
        foreach (var (exe, args, map) in variants) {
            Process? p;
            try {
                p = Process.Start(new ProcessStartInfo(exe) { Arguments = string.Join(' ', args.Select(Quote)), UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception) { continue; } // утилина не установлена — следующая
            if (p is null) continue;
            if (!p.WaitForExit(TimeSpan.FromMinutes(10))) { try { p.Kill(); } catch { } return null; } // не ответил — не считаем отказом
            var verdict = map(p.ExitCode);
            if (verdict is not null) return verdict;
        }
        return null; // ни одного диалогового инструмента
    }

    static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    static void Download(string tag, string url, string assetName) {
        try {
            var dir = Path.Combine(AppPaths.DataRoot, "downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, assetName);
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var http = new HttpClient())
            using (var rsp = http.GetAsync(url, cts.Token).GetAwaiter().GetResult()) {
                if (!rsp.IsSuccessStatusCode) { Log.Warn($"update: загрузка {tag} → HTTP {(int)rsp.StatusCode}"); return; }
                using var fs = File.Create(path);
                rsp.Content.CopyToAsync(fs, cts.Token).GetAwaiter().GetResult();
            }
            Log.Info($"update: {tag} загружена → {path}");
            OpenFolder(dir);
        }
        catch (Exception ex) { Log.Warn($"update: загрузка не удалась: {ex.Message}"); }
    }

    static void OpenFolder(string dir) {
        try {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"\"{dir}\"");
            else Process.Start(new ProcessStartInfo("xdg-open") { Arguments = dir, UseShellExecute = false });
        }
        catch (Exception ex) { Log.Debug($"update: открыть папку не удалось: {ex.Message}"); }
    }
}
