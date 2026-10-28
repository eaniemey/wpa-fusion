using System.Globalization;

namespace WpaFusion.Tma.Expressions;

/// <summary>
/// A compiled metric formula. Supports perfmon expressions plus SEP EDP's C-style ternary and
/// bracketed min/max expressions. Conditional branches are evaluated lazily.
/// </summary>
public sealed class Formula
{
    private readonly Node _root;

    private Formula(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public string Text { get; }

    public static Formula Parse(string text) => new(text, new Parser(Lexer.Tokenize(text)).ParseAll());

    /// <summary>Evaluates with <paramref name="resolve"/> supplying identifier values (it may throw).</summary>
    public double Evaluate(Func<string, double> resolve) => _root.Eval(resolve);

    private abstract class Node
    {
        public abstract double Eval(Func<string, double> resolve);
    }

    private sealed class Num : Node
    {
        private readonly double _v;

        public Num(double v) => _v = v;

        public override double Eval(Func<string, double> resolve) => _v;
    }

    private sealed class Ident : Node
    {
        private readonly string _name;

        public Ident(string name) => _name = name;

        public override double Eval(Func<string, double> resolve) => resolve(_name);
    }

    private sealed class Unary : Node
    {
        private readonly string _op;
        private readonly Node _x;

        public Unary(string op, Node x)
        {
            _op = op;
            _x = x;
        }

        public override double Eval(Func<string, double> resolve)
        {
            var v = _x.Eval(resolve);
            return _op == "-" ? -v : (Truthy(v) ? 0 : 1);
        }
    }

    private sealed class Binary : Node
    {
        private readonly string _op;
        private readonly Node _l;
        private readonly Node _r;

        public Binary(string op, Node l, Node r)
        {
            _op = op;
            _l = l;
            _r = r;
        }

        public override double Eval(Func<string, double> resolve)
        {
            // Short-circuit logic like Python.
            if (_op is "and" or "&")
            {
                return Truthy(_l.Eval(resolve)) && Truthy(_r.Eval(resolve)) ? 1 : 0;
            }

            if (_op is "or" or "|")
            {
                return Truthy(_l.Eval(resolve)) || Truthy(_r.Eval(resolve)) ? 1 : 0;
            }

            var a = _l.Eval(resolve);
            var b = _r.Eval(resolve);
            return _op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => a / b,
                "<" => a < b ? 1 : 0,
                ">" => a > b ? 1 : 0,
                "<=" => a <= b ? 1 : 0,
                ">=" => a >= b ? 1 : 0,
                "==" => a == b ? 1 : 0,
                "!=" => a != b ? 1 : 0,
                _ => throw new InvalidOperationException($"Unknown operator '{_op}'."),
            };
        }
    }

    private sealed class Conditional : Node
    {
        private readonly Node _then;
        private readonly Node _cond;
        private readonly Node _else;

        public Conditional(Node then, Node cond, Node @else)
        {
            _then = then;
            _cond = cond;
            _else = @else;
        }

        public override double Eval(Func<string, double> resolve) =>
            Truthy(_cond.Eval(resolve)) ? _then.Eval(resolve) : _else.Eval(resolve);
    }

    private sealed class Call : Node
    {
        private readonly string _fn;
        private readonly Node[] _args;

        public Call(string fn, Node[] args)
        {
            _fn = fn;
            _args = args;
        }

        public override double Eval(Func<string, double> resolve)
        {
            var values = _args.Select(a => a.Eval(resolve)).ToArray();
            return _fn switch
            {
                "min" => values.Min(),
                "max" => values.Max(),
                "abs" when values.Length == 1 => Math.Abs(values[0]),
                _ => throw new FormatException($"Unknown function '{_fn}' with {values.Length} arguments."),
            };
        }
    }

    private static bool Truthy(double v) => v != 0 && !double.IsNaN(v);

    private enum Kind
    {
        Number,
        Ident,
        Op,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text);

    private static class Lexer
    {
        private static readonly string[] Ops = { "<=", ">=", "==", "!=", "+", "-", "*", "/", "(", ")", "[", "]", ",", "?", ":", ".", "<", ">", "&", "|" };

        public static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    var start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                    {
                        i++;
                    }

                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '-' || s[i + 1] == '+'))
                    {
                        i += 2;
                        while (i < s.Length && char.IsDigit(s[i]))
                        {
                            i++;
                        }
                    }

                    tokens.Add(new Token(Kind.Number, s[start..i]));
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '#')
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.' || s[i] == '#'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(Kind.Ident, s[start..i]));
                    continue;
                }

                var op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0)
                    ?? throw new FormatException($"Unexpected character '{c}' at {i} in formula '{s}'.");
                tokens.Add(new Token(Kind.Op, op));
                i += op.Length;
            }

            tokens.Add(new Token(Kind.End, string.Empty));
            return tokens;
        }
    }

    private sealed class Parser
    {
        private readonly List<Token> _t;
        private int _p;

        public Parser(List<Token> tokens) => _t = tokens;

        private Token Peek => _t[_p];

        public Node ParseAll()
        {
            var node = Expr();
            if (Peek.Kind != Kind.End)
            {
                throw new FormatException($"Unexpected '{Peek.Text}' at token {_p}.");
            }

            return node;
        }

        private bool IsWord(string w) => Peek.Kind == Kind.Ident && Peek.Text == w;

        private bool IsOp(string o) => Peek.Kind == Kind.Op && Peek.Text == o;

        private Token Next() => _t[_p++];

        private Node Expr()
        {
            var then = Or();
            if (IsOp("?"))
            {
                Next();
                var whenTrue = Expr();
                Expect(":");
                return new Conditional(whenTrue, then, Expr());
            }

            if (IsWord("if"))
            {
                Next();
                var cond = Or();
                if (!IsWord("else"))
                {
                    throw new FormatException("Expected 'else'.");
                }

                Next();
                return new Conditional(then, cond, Expr());
            }

            return then;
        }

        private Node Or()
        {
            var l = And();
            while (IsWord("or") || IsOp("|"))
            {
                var op = Next().Text;
                l = new Binary(op, l, And());
            }

            return l;
        }

        private Node And()
        {
            var l = Not();
            while (IsWord("and") || IsOp("&"))
            {
                var op = Next().Text;
                l = new Binary(op, l, Not());
            }

            return l;
        }

        private Node Not()
        {
            if (IsWord("not"))
            {
                Next();
                return new Unary("not", Not());
            }

            return Comparison();
        }

        private Node Comparison()
        {
            var l = Sum();
            while (Peek.Kind == Kind.Op && Peek.Text is "<" or ">" or "<=" or ">=" or "==" or "!=")
            {
                var op = Next().Text;
                l = new Binary(op, l, Sum());
            }

            return l;
        }

        private Node Sum()
        {
            var l = Term();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Next().Text;
                l = new Binary(op, l, Term());
            }

            return l;
        }

        private Node Term()
        {
            var l = UnaryExpr();
            while (IsOp("*") || IsOp("/"))
            {
                var op = Next().Text;
                l = new Binary(op, l, UnaryExpr());
            }

            return l;
        }

        private Node UnaryExpr()
        {
            if (IsOp("-"))
            {
                Next();
                return new Unary("-", UnaryExpr());
            }

            if (IsOp("+"))
            {
                Next();
                return UnaryExpr();
            }

            return Primary();
        }

        private Node Primary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case Kind.Number:
                    return new Num(double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture));
                case Kind.Ident when IsOp("("):
                    {
                        Next();
                        var args = new List<Node>();
                        if (!IsOp(")"))
                        {
                            args.Add(Expr());
                            while (IsOp(","))
                            {
                                Next();
                                args.Add(Expr());
                            }
                        }

                        Expect(")");
                        return new Call(t.Text, args.ToArray());
                    }

                case Kind.Ident:
                    return new Ident(t.Text);
                case Kind.Op when t.Text == "(":
                    {
                        var inner = Expr();
                        Expect(")");
                        return inner;
                    }
                case Kind.Op when t.Text == "[":
                    {
                        var args = new List<Node>();
                        if (!IsOp("]"))
                        {
                            args.Add(Expr());
                            while (IsOp(","))
                            {
                                Next();
                                args.Add(Expr());
                            }
                        }

                        Expect("]");
                        Expect(".");
                        var aggregate = Next();
                        if (aggregate.Kind != Kind.Ident || aggregate.Text is not ("max" or "min"))
                        {
                            throw new FormatException($"Expected 'max' or 'min' after bracketed expression but found '{aggregate.Text}'.");
                        }

                        return new Call(aggregate.Text, args.ToArray());
                    }

                default:
                    throw new FormatException($"Unexpected '{t.Text}'.");
            }
        }

        private void Expect(string op)
        {
            if (!IsOp(op))
            {
                throw new FormatException($"Expected '{op}' but found '{Peek.Text}'.");
            }

            Next();
        }
    }
}
