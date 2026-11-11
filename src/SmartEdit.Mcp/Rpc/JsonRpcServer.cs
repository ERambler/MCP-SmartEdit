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
            Log.Debug($"← {line}");
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
            Log.Error($"необработанная ошибка в {req.Method}: {ex}");
            if (req.Id is not null)
                Write(new RpcResponse { Id = req.Id, Error = new() { Code = -32603, Message = "Internal error" } });
        }
    }

    static readonly StreamWriter Stdout = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) {
        AutoFlush = true,
    };

    void Write(RpcResponse resp) {
        lock (_writeLock) {
            Stdout.WriteLine(JsonSerializer.Serialize(resp, RpcJson.Default.RpcResponse));
        }
    }
}
