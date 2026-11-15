using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using SmartEdit.Mcp.Core;

namespace SmartEdit.Mcp.Tools;

/// <summary>
/// Базовый класс всех инструментов (v0.13.1): контракт ITool + protected-помощники —
/// сериализация результата через source-gen контекст, семафор сессии вокруг работы
/// (#14: EnsureLive/ReleaseIfClean), компиляция regex с таймаутом. Бывший статический ToolOps.
/// </summary>
public abstract class ToolBase : ITool {
    public abstract string Name { get; }
    public abstract string Description { get; }
    /// <summary>JSON Schema входных аргументов (объект).</summary>
    public abstract string InputSchemaJson { get; }
    public abstract Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);

    /// <summary>
    /// #22 (v0.13.0): скрыт из tools/list (нулевая телеметрия), вызов ПО ИМЕНИ живёт.
    /// virtual в иерархии классов (не только DIM интерфейса): маппинг DIM-члена
    /// работает лишь когда интерфейс назван в базовом списке — через ToolBase он терялся.
    /// </summary>
    public virtual bool Hidden => false;

    /// <summary>Сериализация результата (AOT: только source-gen контексты).</summary>
    protected static string Json<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    /// <summary>Все операции сессии — под её семафором.</summary>
    protected static async Task<T> LockedAsync<T>(EditSession s, Func<T> work) {
        await s.Gate.WaitAsync();
        try {
            s.EnsureLive();   // #14: хэндл отпущен между вызовами — восстановить (или прозрачно сбросить при внешней замене)
            return work();
        }
        finally {
            s.ReleaseIfClean(); // #14: чистый документ не держит файл между вызовами (dirty держит — защита базы)
            s.Gate.Release();
        }
    }

    protected static async Task Locked(EditSession s, Action work) {
        await s.Gate.WaitAsync();
        try {
            s.EnsureLive();   // #14
            work();
        }
        finally {
            s.ReleaseIfClean();
            s.Gate.Release();
        }
    }

    protected static Regex CompileRegex(string pattern, bool ignoreCase) {
        try {
            return new Regex(pattern,
                RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0),
                TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException ex) {
            throw new ArgumentException($"invalid regex: {ex.Message}");
        }
    }
}
