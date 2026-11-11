using System.Text;

namespace SmartEdit.Mcp.Core.SymbolScan;

/// <summary>
/// Вырезание строк-литералов и комментариев из строки кода — заменой пробелами, длины
/// сохранены (номера колонок не плывут). Стейт между строками: блок-комментарий и
/// verbatim-строка. Интерполяция $"…" обрабатывается с дырками: текст и {{ }} — blanks,
/// код внутри {дырок} сохраняется (его скобки — настоящие). Общее для сканеров и аудита.
/// </summary>
public static class CodeStrip {
    public static string Strip(string line, ref bool inBlockComment, ref bool inVerbatim) {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length) {
            if (inBlockComment) {
                int end = line.IndexOf("*/", i);
                if (end < 0) { sb.Append(' ', line.Length - i); return sb.ToString(); }
                sb.Append(' ', end + 2 - i);
                i = end + 2;
                inBlockComment = false;
                continue;
            }
            if (inVerbatim) {
                int q = line.IndexOf('"', i);
                if (q < 0) { sb.Append(' ', line.Length - i); return sb.ToString(); }
                if (q + 1 < line.Length && line[q + 1] == '"') { sb.Append("  "); i = q + 2; continue; }
                sb.Append(' ');
                i = q + 1;
                inVerbatim = false;
                continue;
            }
            char c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { sb.Append(' ', line.Length - i); return sb.ToString(); }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') {
                inBlockComment = true;
                sb.Append("  ");
                i += 2;
                continue;
            }
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"') {
                sb.Append("  ");
                i += 2;
                inVerbatim = true;
                continue;
            }
            if (c == '$' && i + 1 < line.Length && line[i + 1] == '"') { ScanInterpolated(); continue; }
            if (c == '"') { ScanPlainString(); continue; }
            if (c == '\'') { ScanCharLiteral(); continue; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();

        // обычная "…": контент blanks, \\-эскейпы не ломают закрывающую кавычку
        void ScanPlainString() {
            sb.Append(' ');
            i++;
            while (i < line.Length) {
                if (line[i] == '\\') { sb.Append("  "); i += 2; continue; }
                if (line[i] == '"') { sb.Append(' '); i++; break; }
                sb.Append(' ');
                i++;
            }
        }

        void ScanCharLiteral() {
            sb.Append(' ');
            i++;
            while (i < line.Length) {
                if (line[i] == '\\') { sb.Append("  "); i += 2; continue; }
                if (line[i] == '\'') { sb.Append(' '); i++; break; }
                sb.Append(' ');
                i++;
            }
        }

        /// <summary>$"…": текст и {{ }} — blanks; код {дырок} сохраняется (вкл. вложенные строки).</summary>
        void ScanInterpolated() {
            sb.Append("  ");
            i += 2;
            int hole = 0; // глубина { внутри строки: 0 = текст, >0 = код дырки
            while (i < line.Length) {
                char s = line[i];
                if (hole == 0 && s == '\\' && i + 1 < line.Length) { sb.Append("  "); i += 2; continue; }
                if (hole == 0 && s == '"') { sb.Append(' '); i++; return; }
                if (s == '{') {
                    if (hole == 0 && i + 1 < line.Length && line[i + 1] == '{') { sb.Append("  "); i += 2; continue; }
                    sb.Append(' '); i++; hole++;
                    continue;
                }
                if (s == '}') {
                    if (hole == 0 && i + 1 < line.Length && line[i + 1] == '}') { sb.Append("  "); i += 2; continue; }
                    sb.Append(' '); i++; hole = Math.Max(0, hole - 1);
                    continue;
                }
                if (hole > 0) {
                    if (s == '$' && i + 1 < line.Length && line[i + 1] == '"') { ScanInterpolated(); continue; }
                    if (s == '"') { ScanPlainString(); continue; }
                    sb.Append(s); // код дырки — как обычный код
                }
                else sb.Append(' '); // текст строки
                i++;
            }
        }
    }
}
