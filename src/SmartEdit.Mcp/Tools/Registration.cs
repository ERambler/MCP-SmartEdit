namespace SmartEdit.Mcp.Tools;

// v0.13.1: единая точка регистрации всех 27 инструментов (бывш. EditToolsRegistration —
// имя врало: «EditTools» регистрировало всё). Порядок = порядок в tools/list.

public static class Registration {
    public static void RegisterAllTools(this ToolRegistry tools) {
        tools.Register(new OpenTool());
        tools.Register(new ShowTool());
        tools.Register(new FindTool());
        tools.Register(new ReplaceLinesTool());
        tools.Register(new InsertAfterTool());
        tools.Register(new SaveTool());
        tools.Register(new CloseTool());
        tools.Register(new CreateTool());       // #21 (v0.12)
        tools.Register(new RecipeSaveTool());   // #8 (v0.11)
        tools.Register(new RecipeListTool());
        tools.Register(new RecipeApplyTool());
        // v0.2: структурное зрение и мета-утилиты
        tools.Register(new SymbolsTool());
        tools.Register(new CheckTool());
        tools.Register(new TreeTool());
        tools.Register(new CalcTool());
        tools.Register(new FileInfoTool());
        tools.Register(new ConvertTool());
        // v0.3: серия правок одним вызовом + литеральный rename по файлу
        tools.Register(new BatchTool());
        tools.Register(new ReplaceAllTool());
        tools.Register(new SessionsTool());
        tools.Register(new SyncTool()); // v0.4
        tools.Register(new LogTool());  // v0.5: экстремная ручка
        tools.Register(new UndoTool());
        tools.Register(new GotoTool()); // v0.5
        tools.Register(new HelpTool());
        tools.Register(new TransactionTool()); // v0.9 (#3)
        tools.Register(new ReplaceContentTool()); // v0.9.1: 23-й инструмент — замена точного текста без номеров строк
    }
}
