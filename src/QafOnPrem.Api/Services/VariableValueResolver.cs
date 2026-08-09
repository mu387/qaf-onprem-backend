using System.Globalization;

namespace QafOnPrem.Api.Services;

internal static class VariableValueResolver
{
    public static string Resolve(string? rawValue, string? executableMethod, bool isEncrypted)
    {
        if (isEncrypted)
        {
            return rawValue ?? string.Empty;
        }

        var value = rawValue ?? string.Empty;
        var normalizedMethod = (executableMethod ?? string.Empty).Trim().ToLowerInvariant();
        var safeLength = int.TryParse(value, out var parsedLength) && parsedLength > 0 ? parsedLength : 6;

        return normalizedMethod switch
        {
            "randomnumber" => string.Concat(Enumerable.Range(0, safeLength).Select(_ => Random.Shared.Next(0, 10).ToString(CultureInfo.InvariantCulture))),
            "randomfloat" => $"{string.Concat(Enumerable.Range(0, safeLength).Select(_ => Random.Shared.Next(0, 10).ToString(CultureInfo.InvariantCulture)))}.{Random.Shared.Next(0, 100):00}",
            "text" => string.Concat(Enumerable.Range(0, Math.Max(safeLength, 5)).Select(_ => LowercaseChars[Random.Shared.Next(0, LowercaseChars.Length)])),
            "safeemail" => $"user{Random.Shared.Next(10000, 99999)}@example.com",
            "excel" => TryResolveExcelFormula(value, out var resolved) ? resolved : value,
            _ => value
        };
    }

    private static bool TryResolveExcelFormula(string rawFormula, out string resolved)
    {
        resolved = rawFormula;
        if (string.IsNullOrWhiteSpace(rawFormula))
        {
            resolved = string.Empty;
            return true;
        }

        if (!rawFormula.TrimStart().StartsWith("=", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var parser = new ExcelFormulaParser(rawFormula);
            resolved = ToText(parser.Parse());
            return true;
        }
        catch
        {
            resolved = rawFormula;
            return false;
        }
    }

    private static string ToText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            DateTime dateTime => dateTime.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "TRUE" : "FALSE",
            double number when Math.Abs(number % 1) < double.Epsilon => number.ToString("0", CultureInfo.InvariantCulture),
            double number => number.ToString("0.###############", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string ConvertExcelFormatToDotNet(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return format;
        }

        var result = format;
        var hasTimeTokens = result.Contains('h', StringComparison.OrdinalIgnoreCase)
            || result.Contains('s', StringComparison.OrdinalIgnoreCase)
            || result.Contains("am/pm", StringComparison.OrdinalIgnoreCase);

        if (!hasTimeTokens)
        {
            result = result
                .Replace("mmmm", "MMMM", StringComparison.OrdinalIgnoreCase)
                .Replace("mmm", "MMM", StringComparison.OrdinalIgnoreCase)
                .Replace("mm", "MM", StringComparison.OrdinalIgnoreCase);
        }

        return result.Replace("am/pm", "tt", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ExcelFormulaParser
    {
        private readonly string _formula;
        private int _index;

        public ExcelFormulaParser(string formula)
        {
            _formula = formula.Trim();
            if (_formula.StartsWith("=", StringComparison.Ordinal))
            {
                _index = 1;
            }
        }

        public object? Parse()
        {
            var value = ParseExpression();
            SkipWhitespace();
            if (_index < _formula.Length)
            {
                throw new FormatException("Unexpected trailing formula content.");
            }

            return value;
        }

        private object? ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (!TryRead('+') && !TryRead('-'))
                {
                    return left;
                }

                var op = _formula[_index - 1];
                var right = ParseTerm();
                left = ApplyBinaryOperation(left, right, op);
            }
        }

        private object? ParseTerm()
        {
            var left = ParsePrimary();
            while (true)
            {
                SkipWhitespace();
                if (!TryRead('*') && !TryRead('/'))
                {
                    return left;
                }

                var op = _formula[_index - 1];
                var right = ParsePrimary();
                left = ApplyBinaryOperation(left, right, op);
            }
        }

        private object? ParsePrimary()
        {
            SkipWhitespace();
            if (_index >= _formula.Length)
            {
                throw new FormatException("Unexpected end of formula.");
            }

            if (TryRead('('))
            {
                var value = ParseExpression();
                Expect(')');
                return value;
            }

            if (_formula[_index] == '"')
            {
                return ParseStringLiteral();
            }

            if (char.IsDigit(_formula[_index]) || _formula[_index] == '.')
            {
                return ParseNumberLiteral();
            }

            if (char.IsLetter(_formula[_index]) || _formula[_index] == '_')
            {
                return ParseIdentifierOrFunction();
            }

            throw new FormatException($"Unsupported token '{_formula[_index]}'.");
        }

        private string ParseStringLiteral()
        {
            Expect('"');
            var result = new System.Text.StringBuilder();
            while (_index < _formula.Length)
            {
                var ch = _formula[_index++];
                if (ch == '"')
                {
                    if (_index < _formula.Length && _formula[_index] == '"')
                    {
                        result.Append('"');
                        _index++;
                        continue;
                    }

                    return result.ToString();
                }

                result.Append(ch);
            }

            throw new FormatException("Unterminated string literal.");
        }

        private double ParseNumberLiteral()
        {
            var start = _index;
            while (_index < _formula.Length && (char.IsDigit(_formula[_index]) || _formula[_index] == '.'))
            {
                _index++;
            }

            return double.Parse(_formula[start.._index], CultureInfo.InvariantCulture);
        }

        private object? ParseIdentifierOrFunction()
        {
            var start = _index;
            while (_index < _formula.Length && (char.IsLetterOrDigit(_formula[_index]) || _formula[_index] == '_' || _formula[_index] == '.'))
            {
                _index++;
            }

            var name = _formula[start.._index];
            SkipWhitespace();
            if (!TryRead('('))
            {
                return name.ToUpperInvariant() switch
                {
                    "TRUE" => true,
                    "FALSE" => false,
                    _ => throw new FormatException($"Unsupported identifier '{name}'.")
                };
            }

            var args = new List<object?>();
            SkipWhitespace();
            if (!TryRead(')'))
            {
                while (true)
                {
                    args.Add(ParseExpression());
                    SkipWhitespace();
                    if (TryRead(')'))
                    {
                        break;
                    }

                    Expect(',');
                }
            }

            return EvaluateFunction(name, args);
        }

        private object? EvaluateFunction(string name, IReadOnlyList<object?> args)
        {
            switch (name.Trim().ToUpperInvariant())
            {
                case "TODAY":
                    if (args.Count != 0)
                    {
                        throw new FormatException("TODAY does not accept arguments.");
                    }
                    return DateTime.Today;
                case "NOW":
                    if (args.Count != 0)
                    {
                        throw new FormatException("NOW does not accept arguments.");
                    }
                    return DateTime.Now;
                case "TEXT":
                    if (args.Count != 2)
                    {
                        throw new FormatException("TEXT requires exactly two arguments.");
                    }

                    var format = ToText(args[1]);
                    return args[0] switch
                    {
                        DateTime dateTime => dateTime.ToString(ConvertExcelFormatToDotNet(format), CultureInfo.InvariantCulture),
                        double number => number.ToString(ConvertExcelFormatToDotNet(format), CultureInfo.InvariantCulture),
                        _ => ToText(args[0])
                    };
                case "CONCAT":
                case "CONCATENATE":
                    return string.Concat(args.Select(ToText));
                default:
                    throw new FormatException($"Unsupported formula function '{name}'.");
            }
        }

        private static object ApplyBinaryOperation(object? left, object? right, char op)
        {
            if (left is DateTime date && right is double dateOffset)
            {
                return op switch
                {
                    '+' => date.AddDays(dateOffset),
                    '-' => date.AddDays(-dateOffset),
                    _ => throw new FormatException($"Operator '{op}' is not supported for date values.")
                };
            }

            var leftNumber = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            var rightNumber = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            return op switch
            {
                '+' => leftNumber + rightNumber,
                '-' => leftNumber - rightNumber,
                '*' => leftNumber * rightNumber,
                '/' => rightNumber == 0 ? throw new DivideByZeroException() : leftNumber / rightNumber,
                _ => throw new FormatException($"Unsupported operator '{op}'.")
            };
        }

        private void SkipWhitespace()
        {
            while (_index < _formula.Length && char.IsWhiteSpace(_formula[_index]))
            {
                _index++;
            }
        }

        private bool TryRead(char expected)
        {
            SkipWhitespace();
            if (_index < _formula.Length && _formula[_index] == expected)
            {
                _index++;
                return true;
            }

            return false;
        }

        private void Expect(char expected)
        {
            if (!TryRead(expected))
            {
                throw new FormatException($"Expected '{expected}'.");
            }
        }
    }

    private static readonly char[] LowercaseChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
}