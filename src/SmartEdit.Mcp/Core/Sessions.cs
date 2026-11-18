using System.Collections.Concurrent;

namespace SmartEdit.Mcp.Core;

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
    public int LastSeenRev { get; internal set; }
    public DateTime LastSeen { get; internal set; } = DateTime.UtcNow;
    public bool Stale => (DateTime.UtcNow - LastSeen).TotalMinutes > 30;
}

/// <summary>
/// Реестр подключений и документов: повторный open того же пути переиспользует документ —
/// правки всех участников живут в одном piece table и одном rev. Последний закрывший
/// гасит документ (dirty → discard/отказ; shadow-страховка внутри EditSession).
/// </summary>
static class Sessions {
    internal static readonly ConcurrentDictionary<string, Attachment> Map = new();
    /// <summary>#24: документы без вытеснения копились до смерти сервера; internal — замки selftest.</summary>
    internal static readonly ConcurrentDictionary<string, EditSession> ByPath = new(); // canonical → документ
    const int MaxSessions = 32;
    /// <summary>#24 (v0.13.2): cap на документы (LRU среди чистых; грязные не выталкиваются никогда — cap мягкий).</summary>
    const int MaxDocuments = 16;
    /// <summary>#24 (v0.13.2): чистый документ без обращений дольше этого — кандидат на вытеснение.</summary>
    static readonly TimeSpan EvictAfterIdle = TimeSpan.FromMinutes(30);

    /// <summary>Подключиться к документу пути (переиспользовать существующий или открыть новый).</summary>
    /// <summary>#21: создать фантомную сессию нового файла (RAM до save); путь должен быть свободен.</summary>
    public static (Attachment Att, bool Reused) Create(string path, string content, bool createParents, string? note) {
        lock (Map) {
            PruneStale();
            EvictIdle();
            if (Map.Count >= MaxSessions)
                throw new ToolException(ToolException.InvalidArgs, $"{MaxSessions} sessions already open — close one");
            string canonical = System.IO.Path.GetFullPath(path);
            if (ByPath.TryGetValue(canonical, out var doc)) {
                // сессия на этом пути уже жива (в т.ч. фантомная) — повторный create это ошибка уже-существует
                throw new ToolException("already_exists",
                    $"a session for this path already exists (rev {doc.Rev}{(doc.IsNewFile ? ", created-not-yet-saved" : "")}) — " +
                    "continue editing it or close it first");
            }
            EnforceDocCap(canonical); // #24
            var s = EditSession.CreateNew(path, content, createParents);
            ByPath[s.Path] = s;
            var first = new Attachment { Id = "s" + Guid.NewGuid().ToString("N")[..8], Doc = s, Note = note, LastSeenRev = 0 };
            Map[first.Id] = first;
            return (first, false);
        }
    }

    public static (Attachment Att, bool Reused) Attach(string path, string? encoding, string? note) {
        lock (Map) {
            PruneStale();
            EvictIdle(); // #12: GC брошенных подключений до проверки лимита — живой сервер не копит мусор
            if (Map.Count >= MaxSessions)
                throw new ToolException(ToolException.InvalidArgs, $"{MaxSessions} sessions already open — close one");
            string canonical = System.IO.Path.GetFullPath(path);
            if (ByPath.TryGetValue(canonical, out var doc)) {
                var att = new Attachment { Id = "s" + Guid.NewGuid().ToString("N")[..8], Doc = doc, Note = note, LastSeenRev = doc.Rev };
                Map[att.Id] = att;
                return (att, true);
            }
            EnforceDocCap(canonical); // #24
            var s = EditSession.Open(path, encoding);
            ByPath[s.Path] = s;
            var first = new Attachment { Id = "s" + Guid.NewGuid().ToString("N")[..8], Doc = s, Note = note, LastSeenRev = 0 };
            Map[first.Id] = first;
            return (first, false);
        }
    }

    /// <summary>
    /// #12: GC подключений — stale (&gt;30 минут тишины) без несохранённых правок отцепляем прямо в open.
    /// Документ НЕ гасим (остаётся в ByPath — данные и rev живы, вдруг агент вернётся); живой
    /// сервер неделями не должен копить attachment'ы брошенных разговоров.
    /// </summary>
    static void PruneStale() {
        foreach (var a in Map.Values) {
            if (!a.Stale || a.Doc.PendingEdits.GetValueOrDefault(a.Id) > 0) continue;
            if (Map.TryRemove(a.Id, out _))
                SmartEdit.Mcp.Infra.Log.Info($"sessions: auto-detach {a.Id} (idle >30m, no pending edits) — document kept");
        }
    }

    // ── #24 (v0.13.2): автовытеснение чистых сессий — «close влит в save» ──────────────
    // Проверка на вызове (без таймеров): idle — осиротевшие чистые документы; cap — LRU среди чистых.

    /// <summary>Осиротевший (0 подключений) чистый документ без обращений дольше EvictAfterIdle — гасим.</summary>
    static void EvictIdle() {
        foreach (var (path, doc) in ByPath) {
            if (Peers(doc).Count > 0 || doc.Dirty) continue;
            if (DateTime.UtcNow - doc.LastUsed < EvictAfterIdle) continue;
            if (ByPath.TryRemove(path, out _)) {
                doc.Close(false);
                doc.Dispose();
                SmartEdit.Mcp.Infra.Log.Info($"session:evict path={path} reason=idle (clean, no peers, >{EvictAfterIdle.TotalMinutes:0}m idle)");
            }
        }
    }

    /// <summary>Cap на документы: перед открытием нового выталкиваем самый давно использованный ЧИСТЫЙ (кроме открываемого). Все грязные — cap мягко превышаем.</summary>
    static void EnforceDocCap(string exceptCanonical) {
        while (ByPath.Count >= MaxDocuments) {
            (string Path, EditSession Doc)? victim = null;
            foreach (var (path, doc) in ByPath) {
                if (path == exceptCanonical || doc.Dirty) continue;
                if (victim is null || doc.LastUsed < victim.Value.Doc.LastUsed) victim = (path, doc);
            }
            if (victim is null) break; // всё грязное или открываемое — не трогаем
            var (vpath, vdoc) = victim.Value;
            // через существующий Detach: гасим подключения (последний гасит документ), семантика peers сохранена
            foreach (var att in Peers(vdoc).ToList())
                Detach(att.Id, discard: false);
            if (ByPath.TryGetValue(vpath, out var d)) { d.Close(false); ByPath.TryRemove(vpath, out _); }
            vdoc.Dispose();
            SmartEdit.Mcp.Infra.Log.Info($"session:evict path={vpath} reason=cap ({MaxDocuments} documents LRU, clean)");
        }
    }

    /// <summary>
    /// v0.9 (#3): session_id ИЛИ path — инструменты принимают путь напрямую, сервер сам подключается
    /// к общему документу (переиспользуемый path-attachment "p:…"). Ритуал open умирает: правки по пути
    /// без предварительного open; ответ эхом возвращает sessionId для продолжения. Оба сразу → ambiguous_target.
    /// Ни один не задан → single-document fallback (#36): если в процессе открыт ровно ОДИН документ, это
    /// однозначная цель — работаем с ним; 0 документов → unknown_session с подсказкой, N&gt;1 → ambiguous_target.
    /// </summary>
    public static Attachment Resolve(string? sessionId, string? path) {
        if (sessionId is not null && path is not null)
            throw new ToolException("ambiguous_target", "pass either session_id or path, not both");
        lock (Map) { PruneStale(); EvictIdle(); } // #24: гигиена на КАЖДОМ вызове — повторные вызовы тоже вытесняют (без таймеров)
        if (sessionId is not null) return Get(sessionId);
        if (string.IsNullOrEmpty(path)) {
            // #36: слабые LLM (например, 9B-дистилляты) «забывают» session_id/path в мутаторах и падают
            // в unknown_session бесконечно, не извлекая урока из структурной ошибки. Если открыт ровно
            // ОДИН документ — это однозначная цель: работаем с ним; иначе ошибки остаются честными.
            lock (Map) {
                var docs = Map.Values.Select(a => a.Doc).Distinct().ToList();
                if (docs.Count == 1) {
                    var single = docs[0];
                    var reuse = Map.Values.Where(a => a.Doc == single).OrderByDescending(a => a.LastSeen).First();
                    Touch(reuse);
                    reuse.LastSeenRev = single.Rev;
                    SmartEdit.Mcp.Infra.Log.Info($"session:resolve fallback → single open document, attachment {reuse.Id}, path={single.Path}");
                    return reuse;
                }
                if (docs.Count == 0)
                    throw new ToolException(ToolException.UnknownSession, "no open session — pass path/session_id, or open(path) first");
                throw new ToolException("ambiguous_target", $"{docs.Count} documents are open — pass session_id or path to pick one");
            }
        }
        string canonical = System.IO.Path.GetFullPath(path);
        if (ByPath.TryGetValue(canonical, out var doc)) {
            var reuse = Map.Values.FirstOrDefault(a => a.Doc == doc && a.Id.StartsWith("p:"));
            if (reuse is not null) { Touch(reuse); reuse.LastSeenRev = doc.Rev; return reuse; }
            var att = new Attachment { Id = "p:" + Guid.NewGuid().ToString("N")[..8], Doc = doc, Note = "path", LastSeenRev = doc.Rev };
            Map[att.Id] = att;
            return att;
        }
        // нового документа нет — открыть СРАЗУ с p:-идентификатором: иначе следующий path-вызов
        // не найдёт p: для переиспользования, создаст ВТОРОЕ вложение и увидит peers_dirty на свои же правки
        lock (Map) {
            PruneStale();
            EvictIdle();
            if (Map.Count >= MaxSessions)
                throw new ToolException(ToolException.InvalidArgs, $"{MaxSessions} sessions already open — close one");
            EnforceDocCap(System.IO.Path.GetFullPath(path)); // #24
            var s = EditSession.Open(path, null);
            ByPath[s.Path] = s;
            var att = new Attachment { Id = "p:" + Guid.NewGuid().ToString("N")[..8], Doc = s, Note = "path", LastSeenRev = 0 };
            Map[att.Id] = att;
            return att;
        }
    }

    /// <summary>Найти подключение по session_id (unknown_session, если не существует).</summary>
    public static Attachment Get(string id) =>
        Map.TryGetValue(id, out var a)
            ? a
            : throw new ToolException(ToolException.UnknownSession, $"no session '{id}' — call open first");

    /// <summary>Отметить активность подключения (liveness для stale-маркера); документу тоже — часы LRU (#24).</summary>
    public static void Touch(Attachment a) { a.LastSeen = DateTime.UtcNow; a.Doc.LastUsed = DateTime.UtcNow; }

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
