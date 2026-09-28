using System.Globalization;

namespace DipTick.Models.Tools;

/// <summary>What an expression came to: its value in words, or why it has none; and whether the value is exact.</summary>
internal sealed record Calculated(decimal? Value, string Text, bool Exact, string? Problem);

/// <summary>
/// Exact decimal arithmetic (<c>calc</c>): numbers, <c>+ - * / ^ ( )</c>
/// (and <c>× ÷ −</c>), <c>%</c> after a number, <c>of</c> for times, and a few named functions:
/// <c>abs round floor ceil min max sqrt</c>. A parser, never code: nothing typed here is run. Arithmetic
/// is in <see cref="decimal"/> (28 significant digits), so 0.1 + 0.2 is 0.3; a power with a
/// fractional exponent is the one thing done in binary floating point, and the answer says so.
/// </summary>
internal sealed class Calculator
{
    /// <summary>The longest expression read.</summary>
    public const int MostChars = 1000;

    private const int MostDepth = 64;
    private const int MostExponent = 1000;

    private static readonly string[] Functions = ["abs", "round", "floor", "ceil", "min", "max", "sqrt"];

    private readonly string _text;
    private int _at;
    private int _depth;
    private bool _exact = true;

    private Calculator(string text) => _text = text;

    /// <summary>What <paramref name="expression"/> comes to, or why it comes to nothing.</summary>
    public static Calculated Evaluate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Length > MostChars)
        {
            return new Calculated(null, string.Empty, false, $"the expression is longer than {MostChars.ToString(CultureInfo.InvariantCulture)} characters");
        }

        var calc = new Calculator(expression.Replace('×', '*').Replace('÷', '/').Replace('−', '-'));
        try
        {
            var value = calc.Sum();
            calc.Space();
            if (calc._at < calc._text.Length)
            {
                return new Calculated(null, string.Empty, false, $"'{calc._text[calc._at]}' at {(calc._at + 1).ToString(CultureInfo.InvariantCulture)} is not understood");
            }

            return new Calculated(value, Plain(value), calc._exact, null);
        }
        catch (FormatException ex)
        {
            return new Calculated(null, string.Empty, false, ex.Message);
        }
        catch (DivideByZeroException)
        {
            return new Calculated(null, string.Empty, false, "division by zero");
        }
        catch (OverflowException)
        {
            return new Calculated(null, string.Empty, false, "the number is too large (decimal holds about 7.9 × 10^28)");
        }
    }

    /// <summary>A decimal as people write it: no trailing zeros after the point, never an exponent.</summary>
    public static string Plain(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    private decimal Sum()
    {
        var value = Product();
        while (true)
        {
            Space();
            if (Take('+'))
            {
                value += Product();
            }
            else if (Take('-'))
            {
                value -= Product();
            }
            else
            {
                return value;
            }
        }
    }

    private decimal Product()
    {
        var value = Unary();
        while (true)
        {
            Space();
            if (Take('*') || Word("of"))
            {
                value *= Unary();
            }
            else if (Take('/'))
            {
                value /= Unary();
            }
            else
            {
                return value;
            }
        }
    }

    private decimal Unary()
    {
        Space();
        if (Take('-'))
        {
            return -Unary();
        }

        if (Take('+'))
        {
            return Unary();
        }

        return Power();
    }

    /// <summary>A power, right to left: 2^3^2 is 2^9. -2^2 is -(2^2), as in mathematics.</summary>
    private decimal Power()
    {
        var value = Postfix();
        Space();
        return Take('^') ? Raise(value, Unary()) : value;
    }

    private decimal Postfix()
    {
        var value = Primary();
        while (true)
        {
            Space();
            if (Take('%'))
            {
                value /= 100m;
            }
            else
            {
                return value;
            }
        }
    }

    private decimal Primary()
    {
        Space();
        if (Take('('))
        {
            Deeper();
            try
            {
                var inside = Sum();
                Space();
                return Take(')') ? inside : throw new FormatException("a ( is never closed");
            }
            finally
            {
                _depth--;
            }
        }

        if (_at < _text.Length && (char.IsAsciiDigit(_text[_at]) || _text[_at] == '.'))
        {
            return Number();
        }

        if (_at < _text.Length && char.IsAsciiLetter(_text[_at]))
        {
            return Function();
        }

        throw new FormatException(_at < _text.Length
            ? $"'{_text[_at]}' at {(_at + 1).ToString(CultureInfo.InvariantCulture)} is not understood"
            : "the expression ends too soon");
    }

    /// <summary>One bracket deeper, a function's own included; refused past <see cref="MostDepth"/>, so no expression can run the stack out.</summary>
    private void Deeper()
    {
        if (++_depth > MostDepth)
        {
            throw new FormatException($"more than {MostDepth.ToString(CultureInfo.InvariantCulture)} brackets deep");
        }
    }

    private decimal Number()
    {
        var start = _at;
        while (_at < _text.Length && (char.IsAsciiDigit(_text[_at]) || _text[_at] == '.'))
        {
            _at++;
        }

        // An exponent: 1.5e3, 2E-4.
        if (_at < _text.Length && (_text[_at] is 'e' or 'E') && _at + 1 < _text.Length && (char.IsAsciiDigit(_text[_at + 1]) || (_text[_at + 1] is '+' or '-' && _at + 2 < _text.Length && char.IsAsciiDigit(_text[_at + 2]))))
        {
            _at += 2;
            while (_at < _text.Length && char.IsAsciiDigit(_text[_at]))
            {
                _at++;
            }
        }

        var token = _text[start.._at];
        return decimal.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"'{token}' is not a number");
    }

    private decimal Function()
    {
        var start = _at;
        while (_at < _text.Length && char.IsAsciiLetter(_text[_at]))
        {
            _at++;
        }

        var name = _text[start.._at].ToUpperInvariant();
        if (!Functions.Contains(name.ToLowerInvariantAscii(), StringComparer.Ordinal))
        {
            throw new FormatException($"'{_text[start.._at]}' is not a function here; there are {string.Join(", ", Functions)}");
        }

        Space();
        if (!Take('('))
        {
            throw new FormatException($"{name.ToLowerInvariantAscii()} needs its arguments in brackets");
        }

        Deeper();
        var args = new List<decimal>();
        try
        {
            args.Add(Sum());
            Space();
            while (Take(','))
            {
                args.Add(Sum());
                Space();
            }

            if (!Take(')'))
            {
                throw new FormatException($"{name.ToLowerInvariantAscii()}( is never closed");
            }
        }
        finally
        {
            _depth--;
        }

        return (name, args.Count) switch
        {
            ("ABS", 1) => Math.Abs(args[0]),
            ("ROUND", 1) => Math.Round(args[0], MidpointRounding.AwayFromZero),
            ("ROUND", 2) => Math.Round(args[0], Places(args[1]), MidpointRounding.AwayFromZero),
            ("FLOOR", 1) => decimal.Floor(args[0]),
            ("CEIL", 1) => decimal.Ceiling(args[0]),
            ("MIN", > 0) => args.Min(),
            ("MAX", > 0) => args.Max(),
            ("SQRT", 1) => Root(args[0]),
            _ => throw new FormatException($"{name.ToLowerInvariantAscii()} does not take {args.Count.ToString(CultureInfo.InvariantCulture)} arguments"),
        };
    }

    private static int Places(decimal n) =>
        n == decimal.Truncate(n) && n is >= 0 and <= 28 ? (int)n : throw new FormatException("round's places are a whole number from 0 to 28");

    /// <summary>A square root in decimal: Newton's method from binary floating point's guess, to decimal's own precision.</summary>
    private static decimal Root(decimal x)
    {
        if (x < 0)
        {
            throw new FormatException("the square root of a negative number is not a real number");
        }

        if (x == 0)
        {
            return 0;
        }

        var guess = (decimal)Math.Sqrt((double)x);
        for (var i = 0; i < 8; i++)
        {
            var next = (guess + (x / guess)) / 2m;
            if (next == guess)
            {
                break;
            }

            guess = next;
        }

        return guess;
    }

    /// <summary>A power: exact for a whole exponent, by squaring; a fractional one in binary floating point, marked not exact.</summary>
    private decimal Raise(decimal value, decimal exponent)
    {
        if (exponent == decimal.Truncate(exponent) && Math.Abs(exponent) <= MostExponent)
        {
            var n = (int)Math.Abs(exponent);
            var result = 1m;
            var square = value;
            while (n > 0)
            {
                if ((n & 1) == 1)
                {
                    result *= square;
                }

                n >>= 1;
                if (n > 0)
                {
                    square *= square;
                }
            }

            return exponent < 0 ? 1m / result : result;
        }

        if (value < 0)
        {
            throw new FormatException("a negative number to a fractional power is not a real number");
        }

        _exact = false;
        var power = Math.Pow((double)value, (double)exponent);
        return double.IsFinite(power) ? (decimal)power : throw new OverflowException();
    }

    private void Space()
    {
        while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
        {
            _at++;
        }
    }

    private bool Take(char c)
    {
        if (_at < _text.Length && _text[_at] == c)
        {
            _at++;
            return true;
        }

        return false;
    }

    /// <summary>A whole word, as in "17% of 2345.60".</summary>
    private bool Word(string word)
    {
        if (_at + word.Length <= _text.Length
            && string.Compare(_text, _at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (_at + word.Length == _text.Length || !char.IsAsciiLetter(_text[_at + word.Length])))
        {
            _at += word.Length;
            return true;
        }

        return false;
    }
}

/// <summary>Lower case for the ASCII letters of a function's name, since the names are ASCII (CA1308 asks for upper case elsewhere).</summary>
internal static class AsciiText
{
    public static string ToLowerInvariantAscii(this string text) => Chat.Seam.Addressing.Lower(text);
}
