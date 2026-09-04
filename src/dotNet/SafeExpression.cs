using System;
using System.Globalization;

namespace DesktopAICompanion
{
    /// <summary>
    /// Small arithmetic evaluator for pet XML. It deliberately supports only numbers, known
    /// variables, parentheses, +, -, *, /, %, and Convert(value,System.Int32).
    /// </summary>
    internal static class SafeExpression
    {
        public static int Evaluate(string expression, Func<string, double> resolveVariable)
        {
            ValidateExpressionText(expression);
            var parser = new Parser(expression, resolveVariable, false);
            ParsedValue parsed = parser.Parse();
            if (!parsed.IsKnown)
                throw new InvalidOperationException("Runtime expression did not produce a value.");
            double result = parsed.Number;
            ValidateFinalResult(result);
            return (int)result;
        }

        private static void ValidateExpressionText(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 256)
                throw new FormatException("Expression is empty or too long.");
        }

        private static void ValidateFinalResult(double result)
        {
            if (double.IsNaN(result) || double.IsInfinity(result) ||
                result < int.MinValue || result > int.MaxValue)
                throw new OverflowException("Expression result is outside the supported integer range.");
        }

        /// <summary>
        /// Validates grammar and variable names without inventing runtime screen or sprite values.
        /// Constant-only subexpressions are still evaluated so deterministic faults are rejected.
        /// </summary>
        public static bool IsValid(string expression, out string error)
        {
            try
            {
                ValidateExpressionText(expression);
                var parser = new Parser(expression, delegate(string name)
                {
                    switch (name)
                    {
                        case "screenW":
                        case "screenH":
                        case "areaW":
                        case "areaH":
                        case "imageW":
                        case "imageH":
                        case "imageX":
                        case "imageY":
                        case "random":
                        case "randS":
                        case "scale":
                            return 0.0;
                        default:
                            throw new FormatException("Unknown expression variable: " + name);
                    }
                }, true);
                ParsedValue result = parser.Parse();
                if (result.IsKnown) ValidateFinalResult(result.Number);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private struct ParsedValue
        {
            public bool IsKnown;
            public double Number;

            public static ParsedValue Known(double number)
            {
                return new ParsedValue { IsKnown = true, Number = number };
            }

            public static ParsedValue Unknown()
            {
                return new ParsedValue { IsKnown = false };
            }
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly Func<string, double> _resolveVariable;
            private readonly bool _syntaxOnly;
            private int _position;
            private int _tokens;

            public Parser(
                string text,
                Func<string, double> resolveVariable,
                bool syntaxOnly)
            {
                _text = text;
                _resolveVariable = resolveVariable;
                _syntaxOnly = syntaxOnly;
            }

            public ParsedValue Parse()
            {
                ParsedValue value = ParseExpression();
                SkipWhiteSpace();
                if (_position != _text.Length)
                    throw new FormatException("Unexpected token at position " + _position + ".");
                return value;
            }

            private ParsedValue ParseExpression()
            {
                ParsedValue value = ParseTerm();
                while (true)
                {
                    SkipWhiteSpace();
                    if (Take('+'))
                    {
                        ParsedValue right = ParseTerm();
                        value = Add(value, right);
                    }
                    else if (Take('-'))
                    {
                        ParsedValue right = ParseTerm();
                        value = Subtract(value, right);
                    }
                    else return value;
                }
            }

            private ParsedValue ParseTerm()
            {
                ParsedValue value = ParseUnary();
                while (true)
                {
                    SkipWhiteSpace();
                    if (Take('*'))
                    {
                        ParsedValue right = ParseUnary();
                        value = Multiply(value, right);
                    }
                    else if (Take('/'))
                    {
                        ParsedValue divisor = ParseUnary();
                        if (divisor.IsKnown &&
                            Math.Abs(divisor.Number) < double.Epsilon)
                            throw new DivideByZeroException("Expression divides by zero.");
                        value = Divide(value, divisor);
                    }
                    else if (Take('%'))
                    {
                        ParsedValue divisor = ParseUnary();
                        if (divisor.IsKnown &&
                            Math.Abs(divisor.Number) < double.Epsilon)
                            throw new DivideByZeroException("Expression divides by zero.");
                        value = Modulo(value, divisor);
                    }
                    else
                    {
                        return value;
                    }
                }
            }

            private ParsedValue ParseUnary()
            {
                SkipWhiteSpace();
                if (Take('+')) return ParseUnary();
                if (Take('-'))
                {
                    ParsedValue value = ParseUnary();
                    return value.IsKnown
                        ? ParsedValue.Known(Checked(-value.Number))
                        : ParsedValue.Unknown();
                }
                return ParsePrimary();
            }

            private ParsedValue ParsePrimary()
            {
                CountToken();
                SkipWhiteSpace();

                if (Take('('))
                {
                    ParsedValue grouped = ParseExpression();
                    Require(')');
                    return grouped;
                }

                if (_position < _text.Length &&
                    (char.IsDigit(_text[_position]) || _text[_position] == '.'))
                    return ParsedValue.Known(ParseNumber());

                string identifier = ParseIdentifier();
                if (string.Equals(identifier, "Convert", StringComparison.Ordinal))
                {
                    Require('(');
                    ParsedValue value = ParseExpression();
                    Require(',');
                    string system = ParseIdentifier();
                    Require('.');
                    string int32 = ParseIdentifier();
                    if (!string.Equals(system, "System", StringComparison.Ordinal) ||
                        !string.Equals(int32, "Int32", StringComparison.Ordinal))
                        throw new FormatException("Only Convert(value,System.Int32) is supported.");
                    Require(')');
                    return value.IsKnown
                        ? ParsedValue.Known(
                            Convert.ToInt32(value.Number, CultureInfo.InvariantCulture))
                        : ParsedValue.Unknown();
                }

                if (_resolveVariable == null)
                    throw new FormatException("Variables are not available in this expression.");
                double resolved = _resolveVariable(identifier);
                return _syntaxOnly
                    ? ParsedValue.Unknown()
                    : ParsedValue.Known(Checked(resolved));
            }

            private static ParsedValue Add(ParsedValue left, ParsedValue right)
            {
                return left.IsKnown && right.IsKnown
                    ? ParsedValue.Known(Checked(left.Number + right.Number))
                    : ParsedValue.Unknown();
            }

            private static ParsedValue Subtract(ParsedValue left, ParsedValue right)
            {
                return left.IsKnown && right.IsKnown
                    ? ParsedValue.Known(Checked(left.Number - right.Number))
                    : ParsedValue.Unknown();
            }

            private static ParsedValue Multiply(ParsedValue left, ParsedValue right)
            {
                return left.IsKnown && right.IsKnown
                    ? ParsedValue.Known(Checked(left.Number * right.Number))
                    : ParsedValue.Unknown();
            }

            private static ParsedValue Divide(ParsedValue left, ParsedValue right)
            {
                return left.IsKnown && right.IsKnown
                    ? ParsedValue.Known(Checked(left.Number / right.Number))
                    : ParsedValue.Unknown();
            }

            private static ParsedValue Modulo(ParsedValue left, ParsedValue right)
            {
                return left.IsKnown && right.IsKnown
                    ? ParsedValue.Known(Checked(left.Number % right.Number))
                    : ParsedValue.Unknown();
            }

            private double ParseNumber()
            {
                int start = _position;
                bool dot = false;
                while (_position < _text.Length)
                {
                    char c = _text[_position];
                    if (char.IsDigit(c)) _position++;
                    else if (c == '.' && !dot) { dot = true; _position++; }
                    else break;
                }

                double value;
                if (!double.TryParse(
                    _text.Substring(start, _position - start),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out value))
                    throw new FormatException("Invalid number at position " + start + ".");
                return value;
            }

            private string ParseIdentifier()
            {
                SkipWhiteSpace();
                int start = _position;
                if (_position >= _text.Length ||
                    !(char.IsLetter(_text[_position]) || _text[_position] == '_'))
                    throw new FormatException("Expected a number, variable, or parenthesis at position " + _position + ".");
                _position++;
                while (_position < _text.Length &&
                       (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
                    _position++;
                return _text.Substring(start, _position - start);
            }

            private bool Take(char expected)
            {
                SkipWhiteSpace();
                if (_position >= _text.Length || _text[_position] != expected) return false;
                _position++;
                return true;
            }

            private void Require(char expected)
            {
                if (!Take(expected))
                    throw new FormatException("Expected '" + expected + "' at position " + _position + ".");
            }

            private void SkipWhiteSpace()
            {
                while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
            }

            private void CountToken()
            {
                if (++_tokens > 128) throw new FormatException("Expression has too many tokens.");
            }

            private static double Checked(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > int.MaxValue * 4.0)
                    throw new OverflowException("Expression produced an invalid value.");
                return value;
            }
        }
    }
}
