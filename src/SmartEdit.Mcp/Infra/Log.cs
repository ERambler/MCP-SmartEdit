using System.Text;

namespace SmartEdit.Mcp.Infra;

/// <summary>
/// Общий журнал ВСЕХ экземпляров сервера (все чаты и проекты пишут в один mcp.log
/// под межпроцессным мьютексом; каждая строка помечена pid — можно отличить параллельные
/// сессии). Ротация по размеру: mcp.log → mcp.log.1 → mcp.log.2 (старейший удаляется).
/// Экстренная ручка для агента — инструмент log: хвост/фильтр/grep по этому журналу.
/// stderr пишем напрямую через дескриптор: stdout занят JSON-RPC (см. Program).
/// </summary>
public static class Log {
    static readonly object Gate = new();
    public static bool DebugEnabled { get; } = Environment.GetEnvironmentVariable("SMARTEDIT_LOG") == "debug";
    static readonly StreamWriter Stderr = new(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
    static readonly Mutex Interprocess = new(false, @"Global\SmartEdit-Log");
    static readonly int Pid = Environment.ProcessId;
    static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Ротация при превышении (архивы mcp.log.1/.2 хранятся).</summary>
    public const long RotateAboveBytes = 5L << 20; // 5 МБ
    public const int KeepArchives = 2;

    /// <summary>Файл архива по индексу (0 = живой журнал, 1..2 = архивы).</summary>
    public static string ArchivePath(int n) => n == 0 ? AppPaths.LogFile : AppPaths.LogFile + "." + n;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) { if (DebugEnabled) Write("DEBUG", msg); }

    static void Write(string level, string msg) {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Pid}] {Sanitize(msg)}";
        lock (Gate) {
            try {
                bool held = Interprocess.WaitOne(2000);
                try {
                    RotateIfNeeded();
                    File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Utf8NoBom);
                }
                finally { if (held) try { Interprocess.ReleaseMutex(); } catch { } }
            }
            catch { /* журнал не должен ронять сервер */ }
            try { Stderr.WriteLine(line); } catch { }
        }
    }

    /// <summary>
    /// Экранирование control-символов (\r \n \t, ANSI ESC, U+2028/2029): одна запись журнала — ровно одна строка.
    /// Защита от log forging: в msg попадают клиентские данные (имена инструментов, пути, session_id,
    /// тексты ошибок), перенос строки в них подделал бы записи журнала, которые затем читает
    /// инструмент log в чужих чатах/проектах (общий журнал на все процессы).
    /// </summary>
    static string Sanitize(string msg) {
        bool dirty = false;
        foreach (var ch in msg)
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029') { dirty = true; break; }
        if (!dirty) return msg; // горячий путь: почти всегда чисто
        var sb = new StringBuilder(msg.Length + 16);
        foreach (var ch in msg)
            switch (ch) {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch) || ch is '\u2028' or '\u2029') sb.Append($"\\u{(int)ch:x4}");
                    else sb.Append(ch);
                    break;
            }
        return sb.ToString();
    }

    /// <summary>mcp.log → .1 → .2; старейший архив удаляется. Вызывается при каждой записи (дёшево: одна stat).</summary>
    public static void RotateIfNeeded() {
        try {
            if (!File.Exists(AppPaths.LogFile) || new FileInfo(AppPaths.LogFile).Length < RotateAboveBytes) return;
            string a2 = ArchivePath(2), a1 = ArchivePath(1);
            if (File.Exists(a2)) File.Delete(a2);
            if (File.Exists(a1)) File.Move(a1, a2);
            File.Move(AppPaths.LogFile, a1);
        }
        catch (IOException) { /* параллельная ротация другим процессом — не страшно */ }
    }
}
