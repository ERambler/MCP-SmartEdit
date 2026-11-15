namespace SmartEdit.Mcp.Core;

// #23 (v0.13.2): модель project-map — извлекается tree-sitter-экстрактором, собирается MapBuilder'ом,
// рендерится MapRenderer'ом. Одна ответственность на файл (дисциплина v0.13.1).

/// <summary>Член типа: имя, свёрнутые аргументы («force, x»), первая строка doc.</summary>
public sealed record MapMember(int Line, string Kind, string Name, string Args, string? Doc);

/// <summary>Тип (class/interface/record/struct/enum; py/ts — def-функции как типы без членов). Bases — простые имена.</summary>
public sealed record MapType(string Kind, string Name, int Line, int EndLine, List<string> Bases, List<MapMember> Members, string? Doc);

/// <summary>Карта одного файла: язык, namespace-чип, using-рёбра («кто кем питается»), типы.</summary>
public sealed record FileMap(string Path, string Lang, string? Namespace, List<string> Usings, List<MapType> Types);
