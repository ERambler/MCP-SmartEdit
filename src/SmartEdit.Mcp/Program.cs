using System.Text;
using SmartEdit.Mcp.Infra;
using SmartEdit.Mcp.Rpc;
using SmartEdit.Mcp.Tools;

AppPaths.EnsureCreated();
// cp1251 для транскод-пути (под AOT таблицы кодовых страниц нужно регистрировать явно)
Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// SmartEdit.Mcp.exe selftest  →  прогон всего набора проверок (10МБ + 300МБ, кодировки, rev-гард)
if (args.Length > 0 && args[0] == "selftest")
    return await SmartEdit.Mcp.SelfTest.RunAsync();

Log.Info($"SmartEdit MCP v{McpProtocolHandler.ServerVersion} запускается. Data dir: {AppPaths.DataRoot}");

var tools = new ToolRegistry();
tools.RegisterEditTools();

var server = new JsonRpcServer(new McpProtocolHandler(tools));
await server.RunAsync(); // до EOF stdin + graceful drain активных обработок
return 0;
