namespace SmartEdit.Mcp.Infra;

/// <summary>Пути данных сервера. Переопределяется переменной окружения SMARTEDIT_DATA_DIR.</summary>
public static class AppPaths {
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("SMARTEDIT_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartEdit");

    public static string LogFile => Path.Combine(DataRoot, "mcp.log");

    public static void EnsureCreated() => Directory.CreateDirectory(DataRoot);
}
