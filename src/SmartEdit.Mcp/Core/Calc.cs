using System.Globalization;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// Синхронный безопасный калькулятор: рекурсивный спуск, без eval/dynamic (AOT-safe).
/// Числа (dec/hex/bin), + - * / % ^ (право-ассоц), унарный минус, постфикс-факториал,
/// скобки, функции (sin cos tan asin acos atan sqrt abs ln log2 log10 exp min max pow
/// round floor ceil), константы pi/e. Целые результаты дополнительно отдаются в hex.
/// </summary>
public static class Calc {
    public static string Evaluate(string expr) {
        var p = new Parser(expr);
        double v = p.ParseExpr();
        p.SkipWs();
        if (!p.AtEnd) throw new ArgumentException($"unexpected '{p.Peek()}' at position {p.Pos}");
        return Format(v);
    }

    static string Format(double v) {
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new DivideByZeroException("result is not a finite number");
        if (Math.Abs(v) < 1e15 && Math.Floor(v) == v) {
            long l = (long)v;
            return Math.Abs(l) <= long.MaxValue
                ? $"{l} (0x{l:X})"
                : v.ToString("0", CultureInfo.InvariantCulture);
        }
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    sealed class Parser(string src) {
        int _pos;
        public int Pos => _pos;
        public bool AtEnd => _pos >= src.Length;
        public char Peek() => _pos < src.Length ? src[_pos] : '\0';

        public void SkipWs() { while (_pos < src.Length && char.IsWhiteSpace(src[_pos])) _pos++; }

        public double ParseExpr() => ParseAdditive();

        double ParseAdditive() {
            double v = ParseMultiplicative();
            while (true) {
                SkipWs();
                char op = Peek();
                if (op is '+' or '-') { _pos++; v = op == '+' ? v + ParseMultiplicative() : v - ParseMultiplicative(); }
                else return v;
            }
        }

        double ParseMultiplicative() {
            double v = ParsePower();
            while (true) {
                SkipWs();
                char op = Peek();
                if (op is '*' or '/' or '%') {
                    _pos++;
                    double r = ParsePower();
                    if ((op == '/' || op == '%') && r == 0) throw new DivideByZeroException("division by zero");
                    v = op == '*' ? v * r : op == '/' ? v / r : v % r;
                }
                else return v;
            }
        }

        double ParsePower() {
            double b = ParseUnary();
            SkipWs();
            if (Peek() == '^') { _pos++; return Math.Pow(b, ParsePower()); } // право-ассоциативно: 2^3^2 = 512
            return b;
        }

        double ParseUnary() {
            SkipWs();
            if (Peek() == '-') { _pos++; return -ParseUnary(); }
            if (Peek() == '+') { _pos++; return ParseUnary(); }
            return ParsePostfix();
        }

        double ParsePostfix() {
            double v = ParsePrimary();
            while (true) {
                SkipWs();
                if (Peek() == '!') { _pos++; v = Factorial(v); }
                else return v;
            }
        }

        static double Factorial(double v) {
            if (v < 0 || Math.Floor(v) != v || v > 170) throw new ArgumentException($"factorial of {v} is not defined");
            double r = 1;
            for (double i = 2; i <= v; i++) r *= i;
            return r;
        }

        double ParsePrimary() {
            SkipWs();
            char c = Peek();
            if (c == '(') {
                _pos++;
                double v = ParseExpr();
                SkipWs();
                if (Peek() != ')') throw new ArgumentException($"expected ')' at position {_pos}");
                _pos++;
                return v;
            }
            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c) || c == '_') return ParseWord();
            throw new ArgumentException($"unexpected '{c}' at position {_pos}");
        }

        double ParseNumber() {
            int start = _pos;
            if (src[_pos] == '0' && _pos + 1 < src.Length && (src[_pos + 1] is 'x' or 'X')) {
                _pos += 2;
                while (_pos < src.Length && Uri.IsHexDigit(src[_pos])) _pos++;
                return long.Parse(src[start.._pos].Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            if (src[_pos] == '0' && _pos + 1 < src.Length && (src[_pos + 1] is 'b' or 'B')) {
                _pos += 2;
                while (_pos < src.Length && src[_pos] is '0' or '1') _pos++;
                return Convert.ToInt64(src[(start + 2).._pos], 2);
            }
            while (_pos < src.Length && (char.IsDigit(src[_pos]) || src[_pos] == '.')) _pos++;
            // экспонента: 1e3 / 1.5e-3
            if (_pos < src.Length && (src[_pos] is 'e' or 'E')
                && (_pos + 1 < src.Length && (char.IsDigit(src[_pos + 1]) || src[_pos + 1] is '+' or '-'))) {
                _pos++;
                if (src[_pos] is '+' or '-') _pos++;
                while (_pos < src.Length && char.IsDigit(src[_pos])) _pos++;
            }
            if (!double.TryParse(src[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new ArgumentException($"bad number at position {start}");
            return v;
        }

        double ParseWord() {
            int start = _pos;
            while (_pos < src.Length && (char.IsLetterOrDigit(src[_pos]) || src[_pos] == '_')) _pos++;
            string word = src[start.._pos];
            SkipWs();
            if (Peek() == '(') {
                _pos++;
                var args = new List<double>();
                SkipWs();
                if (Peek() != ')') {
                    args.Add(ParseExpr());
                    SkipWs();
                    while (Peek() == ',') { _pos++; args.Add(ParseExpr()); SkipWs(); }
                }
                if (Peek() != ')') throw new ArgumentException($"expected ')' after {word}(...) at position {_pos}");
                _pos++;
                return Apply(word, args, start);
            }
            return word.ToLowerInvariant() switch {
                "pi" => Math.PI,
                "e" => Math.E,
                "tau" => Math.Tau,
                _ => throw new ArgumentException($"unknown constant '{word}' at position {start}"),
            };
        }

        static double Apply(string f, List<double> a, int pos) {
            double Arg() => a.Count > 0 ? a[0] : throw ArgC(f, 1);
            double A1() => a.Count > 1 ? a[1] : throw ArgC(f, 2);
            return f.ToLowerInvariant() switch {
                "sin" => Math.Sin(Arg()),
                "cos" => Math.Cos(Arg()),
                "tan" => Math.Tan(Arg()),
                "asin" => Math.Asin(Arg()),
                "acos" => Math.Acos(Arg()),
                "atan" => Math.Atan(Arg()),
                "atan2" => Math.Atan2(Arg(), A1()),
                "sqrt" => Math.Sqrt(Arg()),
                "abs" => Math.Abs(Arg()),
                "ln" => Math.Log(Arg()),
                "log" => Math.Log(Arg()),          // log = натуральный, как в math-нотации C#
                "log2" => Math.Log2(Arg()),
                "log10" => Math.Log10(Arg()),
                "exp" => Math.Exp(Arg()),
                "round" => Math.Round(Arg(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(Arg()),
                "ceil" or "ceiling" => Math.Ceiling(Arg()),
                "min" => a.Count >= 1 ? a.Min() : throw ArgC(f, 1),
                "max" => a.Count >= 1 ? a.Max() : throw ArgC(f, 1),
                "pow" => Math.Pow(Arg(), A1()),
                _ => throw new ArgumentException($"unknown function '{f}' at position {pos}"),
            };
            static ArgumentException ArgC(string fn, int n) => new($"{fn} expects {n} argument(s)");
        }
    }
}
