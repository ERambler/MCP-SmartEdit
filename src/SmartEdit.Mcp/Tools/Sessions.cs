using System.Collections.Concurrent;
using SmartEdit.Mcp.Core;

namespace SmartEdit.Mcp.Tools;

// ── v0.4: коллаборация — один документ на путь, N подключений ──────────────────

/// <summary>
/// Подключение агента к общему документу: свой id (session_id в инструментах), заметка
/// «зачем зашёл», видимость (LastSeenRev — до какого rev доставлены события) и liveness
/// (LastSeen/Stale: >30 минут без вызовов — вероятно, мёртвый агент).
/// </summary>
sealed class Attachment {
    public required string Id { get; init; }
    public required EditSession Doc { get; init; }
    public string? Note { get; init; }
    public int LastSeenRev;
    public DateTime LastSeen = DateTime.UtcNow;
    public bool Stale => (DateTime.UtcNow - LastSeen).TotalMinutes > 30;
}

/// <summary>
/// Реестр подключений и документов: повторный open того же пути переиспользует документ —
/// правки всех участников живут в одном piece table и одном rev. Последний закрывший
/// гасит документ (dirty → discard/отказ; shadow-страховка внутри EditSession).
/// </summary>
static class Sessions {
    internal static readonly ConcurrentDictionary<string, Attachment> Map = new();
    static readonly ConcurrentDictionary<string, EditSession> ByPath = new(); // canonical → документ
    const int MaxSessions = 32;

    /// <summary>Подключиться к документу пути (переиспользовать существующий или открыть новый).</summary>
    public static (Attachment Att, bool Reused) Attach(string path, string? encoding, string? note) {
        lock (Map) {
            if (Map.Count >= MaxSessions)
                throw new ToolException(ToolException.InvalidArgs, $"{MaxSessions} sessions already open — close one");
            string canonical = System.IO.Path.GetFullPath(path);
            if (ByPath.TryGetValue(canonical, out var doc)) {
                var att = new Attachment { Id = "s" + Guid.NewGuid().ToString("N")[..8], Doc = doc, Note = note, LastSeenRev = doc.Rev };
                Map[att.Id] = att;
                return (att, true);
            }
            var s = EditSession.Open(path, encoding);
            ByPath[s.Path] = s;
            var first = new Attachment { Id = "s" + Guid.NewGuid().ToString("N")[..8], Doc = s, Note = note, LastSeenRev = 0 };
            Map[first.Id] = first;
            return (first, false);
        }
    }

    /// <summary>Найти подключение по session_id (unknown_session, если не существует).</summary>
    public static Attachment Get(string id) =>
        Map.TryGetValue(id, out var a)
            ? a
            : throw new ToolException(ToolException.UnknownSession, $"no session '{id}' — call open first");

    /// <summary>Отметить активность подключения (liveness для stale-маркера).</summary>
    public static void Touch(Attachment a) { a.LastSeen = DateTime.UtcNow; }

    /// <summary>Все подключения документа.</summary>
    public static List<Attachment> Peers(EditSession doc) =>
        [.. Map.Values.Where(a => a.Doc == doc).OrderBy(a => a.Id)];

    /// <summary>
    /// Отключиться. Не последний — просто detach (отказ только при СВОИХ несохранённых и !discard).
    /// Последний гасит документ (dirty → discard/отказ; shadow-страховка внутри EditSession).
    /// </summary>
    public static (bool WasDirty, bool DocDisposed, string? Shadow) Detach(string id, bool discard) {
        lock (Map) {
            var att = Get(id);
            var doc = att.Doc;
            bool mine = doc.PendingEdits.GetValueOrDefault(att.Id) > 0;
            if (Peers(doc).Count > 1) {
                if (mine && !discard)
                    throw new ToolException(ToolException.DirtyClose,
                        $"your attachment has unsaved edits (×{doc.PendingEdits[att.Id]}) — save first or pass discard=true");
                Map.TryRemove(id, out _);
                return (mine, false, null);
            }
            bool wasDirty = doc.Dirty;
            if (wasDirty && !discard)
                throw new ToolException(ToolException.DirtyClose, "session has unsaved edits — save first or pass discard=true");
            Map.TryRemove(id, out _);
            ByPath.TryRemove(doc.Path, out _);
            doc.Close(discard); // dirty+discard → shadow остаётся страховкой
            return (wasDirty, true, wasDirty && discard ? doc.Path + ".smartedit-shadow" : null);
        }
    }
}

/// <summary>Presence-хелперы: доставка событий отставшим подключениям без токен-налога.</summary>
static class Presence {
    /// <summary>
    /// Компактные события, накопившиеся с LastSeenRev подключения (null — не отставал:
    /// токены тратятся только когда реально есть что доставить). При выходе за кольцо
    /// истории — явный маркер resync.
    /// </summary>
    public static string[]? EventsSince(EditSession doc, Attachment att) {
        if (att.LastSeenRev >= doc.Rev) return null;
        var events = doc.History.Where(h => h.Rev > att.LastSeenRev).Take(10)
            .Select(h => $"r{h.Rev} {h.Actor}: {h.Desc}").ToList();
        int missed = doc.Rev - att.LastSeenRev - events.Count;
        if (missed > 0) events.Add($"… +{missed} older edits outside the history ring — resync via show");
        att.LastSeenRev = doc.Rev; // события доставлены
        return [.. events];
    }
}
