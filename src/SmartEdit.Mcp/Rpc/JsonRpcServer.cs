using System.Text;
using System.Text.Json;
using SmartEdit.Mcp.Infra;

namespace SmartEdit.Mcp.Rpc;

/// <summary>
/// Транспортный цикл MCP over stdio: newline-delimited JSON-RPC 2.0.
/// Каждое сообщение обрабатывается в отдельной задаче — длинный tools/call не блокирует чтение.
/// </summary>
public sealed class JsonRpcServer(McpProtocolHandler handler) {
    readonly object _writeLock = new();

    // ВАЖНО: не используем Console.In/Out/Error — обращения к Console.* под
    // перенаправленными дескрипторами Windows пересоздают потоки и теряют позицию stdin.
    int _pending;

    /// <summary>
    /// Цикл чтения stdin в вызывающем (главном) потоке; обработка — в Task.Run.
    /// Возврат = stdin закрыт (клиент отключился) и начатые обработки завершены (drain ≤10с).
    /// </summary>
    public async Task RunAsync() {
        Log.Info("stdio JSON-RPC цикл запущен; жду сообщения клиента");
        using var stdinReader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, false, 1 << 16);
        while (true) {
            string? line;
            try { line = stdinReader.ReadLine(); }
            catch (IOException) { break; }
            if (line is null) break; // EOF — клиент ушёл
            if (line.Length == 0) continue;
            if (Log.DebugEnabled) Log.Debug(Brief(line)); // F4: payload аргументов — никогда в журнал
            Interlocked.Increment(ref _pending);
            _ = Task.Run(async () => {
                try { await HandleLineAsync(line); }
                finally { Interlocked.Decrement(ref _pending); }
            }); // параллельная обработка, ответ по Id
        }
        Log.Info("stdin закрыт; жду завершения обработок");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_pending > 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Log.Info("остановка завершена");
    }

    async Task HandleLineAsync(string line) {
        RpcRequest? req = null;
        try { req = JsonSerializer.Deserialize(line, RpcJson.Default.RpcRequest); }
        catch (JsonException ex) {
            Log.Warn($"не парсится как JSON-RPC: {ex.Message}");
            // спека JSON-RPC 2.0: parse error — ответ -32700 с id:null (id в ответе обязателен)
            Write(new RpcResponse { Id = null, Error = new() { Code = -32700, Message = "Parse error" } });
            return;
        }
        if (req?.Method is null) return;

        try {
            var (result, error) = await handler.HandleAsync(req);
            if (req.Id is null) return; // notification — молча
            Write(new RpcResponse { Id = req.Id, Result = result, Error = error });
        }
        catch (Exception ex) {
            // ex.Message (не {ex}): ToString() многострочен (стек) — ломает «одна запись = одна строка» журнала
            Log.Error($"необработанная ошибка в {req.Method}: {ex.Message}");
            if (req.Id is not null)
                Write(new RpcResponse { Id = req.Id, Error = new() { Code = -32603, Message = "Internal error" } });
        }
    }
    /// <summary>
    /// F4 (утечка секретов): сырая строка JSON-RPC несёт аргументы tools/call — содержимое файлов,
    /// тексты правок, заметки, потенциально логины/токены. Общий журнал читается инструментом log
    /// из любых чатов/проектов, поэтому в debug пишем только метод/id/размер — payload не касается журнала никогда.
    /// </summary>
    internal static string Brief(string line) {
        string id = "-", method = "?";
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String) method = Cut(m.GetString()!);
            if (root.TryGetProperty("id", out var i))
                id = i.ValueKind == JsonValueKind.String ? Cut(i.GetString()!) : i.ToString();
        }
        catch (JsonException) { method = "unparsable"; }
        return $"← id={id} method={method} len={line.Length}";
    }

    static string Cut(string s) => s.Length <= 64 ? s : s[..64] + "…";

    static readonly StreamWriter Stdout = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) {
        AutoFlush = true,
    };

    void Write(RpcResponse resp) {
        lock (_writeLock) {
            Stdout.WriteLine(JsonSerializer.Serialize(resp, RpcJson.Default.RpcResponse));
        }
    }
}
