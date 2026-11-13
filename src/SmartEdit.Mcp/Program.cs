using System.Text;
using SmartEdit.Mcp.Infra;
using SmartEdit.Mcp.Rpc;
using SmartEdit.Mcp.Tools;

// #10: телеметрия selftest-прогонов НЕ должна загрязнять общий журнал продакшена —
// редирект каталога данных ДО первого обращения к AppPaths/Log (их статические поля читают env лениво)
if (args.Length > 0 && args[0] == "selftest")
    Environment.SetEnvironmentVariable("SMARTEDIT_DATA_DIR",
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartedit-selftest-data"));

AppPaths.EnsureCreated();
// cp1251 для транскод-пути (под AOT таблицы кодовых страниц нужно регистрировать явно)
Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// SmartEdit.Mcp.exe selftest  →  прогон всего набора проверок (10МБ + 300МБ, кодировки, rev-гард)
if (args.Length > 0 && args[0] == "selftest")
    return await SmartEdit.Mcp.SelfTest.RunAsync();

// SmartEdit.Mcp.exe smoke → живые смоки ЗАПУЩЕННОГО сервера (#20): транспорт + все инструменты +
// пакет «пьяная нейронка»; <15с, отчёт с expected/got, exit 0/1
if (args.Length > 0 && args[0] == "smoke") {
    Environment.SetEnvironmentVariable("SMARTEDIT_NO_UPDATE_CHECK", "1");
    // AOT: smoke-клиент сериализует запросы рефлексией (анонимные типы) — без dynamic кода он не работает;
    // честный отказ вместо падения на первом вызове; selftest на AOT гоняется полностью
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {
        Console.WriteLine("smoke requires dynamic code (reflection JSON) — unavailable in Native AOT; run selftest instead");
        return 2;
    }
    return await SmartEdit.Mcp.Smoke.RunAsync();
}

Log.Info($"SmartEdit MCP v{McpProtocolHandler.ServerVersion} запускается. Data dir: {AppPaths.DataRoot}");

// v0.6: фоновая проверка обновлений (GitHub latest release → системный диалог); stdio не блокируется.
// Выключить: --no-update или SMARTEDIT_NO_UPDATE_CHECK=1
UpdateCheck.CleanupOldBinary(); // подчистить .exe.old от прошлой самозамены (v0.6.1)
UpdateCheck.RunIfNeeded(args);

var tools = new ToolRegistry();
tools.RegisterEditTools();

var server = new JsonRpcServer(new McpProtocolHandler(tools));
await server.RunAsync(); // до EOF stdin + graceful drain активных обработок
return 0;
