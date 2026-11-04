using MilkDrop.Eval;

namespace MilkDrop.PresetScript.Syntax;

/// <summary>
/// <c>.milkx</c> 里 C# 风格代码段的前端：源码 → <see cref="EvalNode"/> 树。
/// </summary>
/// <remarks>
/// <para>
/// <b>不是</b>"C# → EEL 文本转译"：这里直接产出表达式树，再交给 <c>MilkDrop.Eval</c> 现成的
/// <c>BytecodeCompiler</c> + <c>EvalVm</c> 执行。运行时不新增解释器，也不生成任何 EEL 文本。
/// </para>
/// <para>语法白名单见 plan.md §3：声明 / 赋值 / <c>if-else</c> / <c>repeat</c> / <c>do-while</c> / 表达式。</para>
/// </remarks>
public sealed class CsParser
{
    /// <summary>解析一段代码，产出可直接执行的树；空代码返回 null。</summary>
    /// <param name="source">代码段文本（不含 <c>[段名]</c> 行）。</param>
    /// <param name="context">变量槽位上下文（与同一次预设加载共用）。</param>
    public EvalNode? Parse(string source, CompileContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        return new State(CsLexer.Tokenize(source), context).ParseProgram();
    }

    /// <summary>递归下降的具体状态机（一次解析一个实例）。</summary>
    private sealed class State
    {
        private readonly List<CsToken> _tokens;
        private readonly CompileContext _ctx;
        private int _pos;

        public State(List<CsToken> tokens, CompileContext context)
        {
            _tokens = tokens;
            _ctx = context;
        }

        private CsToken Current => _tokens[_pos];

        /// <summary>往后看第 <paramref name="offset"/> 个记号（越界返回文件结尾）。</summary>
        private CsToken Peek(int offset)
        {
            int index = Math.Min(_pos + offset, _tokens.Count - 1);
            return _tokens[index];
        }

        public EvalNode? ParseProgram()
        {
            var statements = new List<EvalNode>();

            while (Current.Kind != CsTokenKind.End)
            {
                EvalNode? statement = ParseStatement();
                if (statement is not null)
                {
                    statements.Add(statement);
                }
            }

            return statements.Count == 0 ? null : _ctx.List(statements.ToArray());
        }

        // ---- 语句 ----

        /// <summary>
        /// 关键字是**上下文相关**的：EEL 里 <c>repeat</c> / <c>do</c> / <c>double</c> 都可以是普通变量名
        /// （实测有预设写了 <c>repeat = sin(time)*1+1;</c>），所以只有在特定后继记号处才当关键字。
        /// </summary>
        private EvalNode? ParseStatement()
        {
            if (Current.IsKeyword("double") && Peek(1).Kind == CsTokenKind.Identifier)
            {
                return ParseDeclaration();
            }

            if (Current.IsKeyword("if") && Peek(1).IsPunct("("))
            {
                return ParseIf();
            }

            if (Current.IsKeyword("repeat") && Peek(1).IsPunct("("))
            {
                return ParseRepeat();
            }

            if (Current.IsKeyword("do") && Peek(1).IsPunct("{"))
            {
                return ParseDoWhile();
            }

            if (Current.IsPunct("{"))
            {
                return ParseBlock();
            }

            if (Current.IsPunct(";"))
            {
                Advance();
                return null;
            }

            EvalNode expression = ParseExpression();
            Expect(";");
            return expression;
        }

        /// <summary><c>double x = ...;</c> —— 只是写法糖，变量仍然是 EEL 的全局槽位。</summary>
        private EvalNode ParseDeclaration()
        {
            Advance();
            CsToken name = ExpectIdentifier();

            EvalNode target = Variable(name.Text);

            if (TryConsume("="))
            {
                target = _ctx.Call("_set", target, ParseExpression());
            }

            Expect(";");
            return target;
        }

        private EvalNode ParseIf()
        {
            Advance();
            Expect("(");
            EvalNode condition = ParseExpression();
            Expect(")");

            EvalNode whenTrue = ParseBody();
            EvalNode whenFalse = _ctx.Constant(0);

            if (Current.IsKeyword("else"))
            {
                Advance();
                whenFalse = ParseBody();
            }

            return _ctx.Call("if", condition, whenTrue, whenFalse);
        }

        private EvalNode ParseRepeat()
        {
            Advance();
            Expect("(");
            EvalNode count = ParseExpression();
            Expect(")");

            return _ctx.Call("loop", count, ParseBody());
        }

        /// <summary>
        /// <c>do { ... } while (c);</c> → EEL 的 <c>while(exec2(..., c))</c>：
        /// 循环体和条件放在同一个序列里，序列最后一条的值就是条件。
        /// </summary>
        private EvalNode ParseDoWhile()
        {
            Advance();
            EvalNode body = ParseBody();

            if (!Current.IsKeyword("while"))
            {
                throw Error("do 块后面缺少 while");
            }

            Advance();
            Expect("(");
            EvalNode condition = ParseExpression();
            Expect(")");
            Expect(";");

            var sequence = new List<EvalNode>();
            Flatten(body, sequence);
            sequence.Add(condition);

            return _ctx.Call("while", _ctx.List(sequence.ToArray()));
        }

        private EvalNode ParseBlock()
        {
            Expect("{");

            var statements = new List<EvalNode>();

            while (!Current.IsPunct("}"))
            {
                if (Current.Kind == CsTokenKind.End)
                {
                    throw Error("代码块没有闭合");
                }

                EvalNode? statement = ParseStatement();
                if (statement is not null)
                {
                    statements.Add(statement);
                }
            }

            Expect("}");

            return statements.Count switch
            {
                0 => _ctx.Constant(0),
                1 => statements[0],
                _ => _ctx.List(statements.ToArray()),
            };
        }

        /// <summary>语句、块都可以当分支体。</summary>
        private EvalNode ParseBody() => Current.IsPunct("{") ? ParseBlock() : ParseStatement() ?? _ctx.Constant(0);

        // ---- 表达式（优先级从低到高）----

        private EvalNode ParseExpression() => ParseAssignment();

        private EvalNode ParseAssignment()
        {
            EvalNode left = ParseTernary();

            if (Current.Kind == CsTokenKind.Punct && IsAssignmentOperator(Current.Text))
            {
                string op = Current.Text;
                Advance();

                // 右结合
                EvalNode right = ParseAssignment();
                return _ctx.Call(NameOfAssignment(op), left, right);
            }

            return left;
        }

        private EvalNode ParseTernary()
        {
            EvalNode condition = ParseComparison();

            if (TryConsume("?"))
            {
                EvalNode whenTrue = ParseExpression();
                Expect(":");
                EvalNode whenFalse = ParseExpression();
                return _ctx.Call("if", condition, whenTrue, whenFalse);
            }

            return condition;
        }

        private EvalNode ParseComparison()
        {
            EvalNode left = ParseAdditive();

            while (Current.Kind == CsTokenKind.Punct
                && Current.Text is "==" or "!=" or "<" or ">" or "<=" or ">=")
            {
                string op = Current.Text;
                Advance();

                EvalNode right = ParseAdditive();
                left = _ctx.Call(NameOfComparison(op), left, right);
            }

            return left;
        }

        private EvalNode ParseAdditive()
        {
            EvalNode left = ParseMultiplicative();

            while (Current.Kind == CsTokenKind.Punct && Current.Text is "+" or "-")
            {
                string op = Current.Text;
                Advance();

                EvalNode right = ParseMultiplicative();
                left = _ctx.Call(op == "+" ? "_add" : "_sub", left, right);
            }

            return left;
        }

        private EvalNode ParseMultiplicative()
        {
            EvalNode left = ParseUnary();

            while (Current.Kind == CsTokenKind.Punct && Current.Text is "*" or "/" or "%")
            {
                string op = Current.Text;
                Advance();

                EvalNode right = ParseUnary();
                left = _ctx.Call(op switch
                {
                    "*" => "_mul",
                    "/" => "_div",
                    _ => "_mod",
                }, left, right);
            }

            return left;
        }

        private EvalNode ParseUnary()
        {
            if (TryConsume("-"))
            {
                return _ctx.Call("_neg", ParseUnary());
            }

            if (TryConsume("+"))
            {
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private EvalNode ParsePrimary()
        {
            CsToken token = Current;

            switch (token.Kind)
            {
                case CsTokenKind.Number:
                    Advance();
                    return _ctx.Constant(token.Number);

                case CsTokenKind.Identifier:
                    return ParseIdentifier();

                case CsTokenKind.Punct when token.Text == "(":
                {
                    Advance();
                    EvalNode inner = ParseExpression();
                    Expect(")");
                    return inner;
                }

                default:
                    throw Error($"这里需要一个表达式，却遇到 {token}");
            }
        }

        private EvalNode ParseIdentifier()
        {
            CsToken name = Current;
            Advance();

            if (Current.IsPunct("("))
            {
                Advance();

                var args = new List<EvalNode>();
                if (!Current.IsPunct(")"))
                {
                    do
                    {
                        args.Add(ParseExpression());
                    }
                    while (TryConsume(","));
                }

                Expect(")");

                return CallFunction(name, args);
            }

            if (Current.IsPunct("["))
            {
                Advance();
                EvalNode index = ParseExpression();
                Expect("]");

                return name.Text switch
                {
                    "megabuf" or "_mem" => _ctx.Call("megabuf", index),
                    "gmegabuf" or "_gmem" => _ctx.Call("gmegabuf", index),
                    _ => throw Error($"{name.Text} 不支持下标（只有 megabuf / gmegabuf 可以）"),
                };
            }

            return Variable(name.Text);
        }

        private EvalNode CallFunction(CsToken name, List<EvalNode> args)
        {
            // 打印器输出的别名：
            //   and/or   → EEL 的 _and/_or（短路布尔）
            //   bitand/bitor → EEL 的 & / |（整数位运算，内部名是 /*and*/ /*or*/）
            string functionName = name.Text switch
            {
                "and" => "_and",
                "or" => "_or",
                "bitand" => "/*and*/",
                "bitor" => "/*or*/",
                _ => name.Text,
            };

            FunctionDef? function = _ctx.FindFunction(functionName);
            if (function is null)
            {
                throw Error($"未知函数 '{name.Text}'");
            }

            if (function.ArgCount != args.Count)
            {
                throw Error($"函数 '{name.Text}' 需要 {function.ArgCount} 个参数，实际给了 {args.Count} 个");
            }

            return _ctx.Call(function, args.ToArray());
        }

        private EvalNode Variable(string name) => _ctx.VariableNode(_ctx.RegisterVariable(name));

        // ---- 辅助 ----

        private void Advance()
        {
            if (_pos < _tokens.Count - 1)
            {
                _pos++;
            }
        }

        private bool TryConsume(string punct)
        {
            if (!Current.IsPunct(punct))
            {
                return false;
            }

            Advance();
            return true;
        }

        private void Expect(string punct)
        {
            if (!TryConsume(punct))
            {
                throw Error($"这里需要 '{punct}'，却遇到 {Current}");
            }
        }

        private CsToken ExpectIdentifier()
        {
            if (Current.Kind != CsTokenKind.Identifier)
            {
                throw Error($"这里需要一个标识符，却遇到 {Current}");
            }

            CsToken token = Current;
            Advance();
            return token;
        }

        private CompileException Error(string message)
            => new(message, Current.Line, Current.Column, Current.Column + 1);

        private static bool IsAssignmentOperator(string text)
            => text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "&=" or "|=";

        private static string NameOfAssignment(string op) => op switch
        {
            "=" => "_set",
            "+=" => "_addop",
            "-=" => "_subop",
            "*=" => "_mulop",
            "/=" => "_divop",
            "%=" => "_modop",
            "^=" => "_powop",
            "&=" => "_andop",
            _ => "_orop",
        };

        private static string NameOfComparison(string op) => op switch
        {
            "==" => "equal",
            "!=" => "_noteq",
            "<" => "below",
            ">" => "above",
            "<=" => "_beleq",
            _ => "_aboeq",
        };

        /// <summary>把 List 节点摊平成语句数组（do-while 需要把循环体和条件装进同一个序列）。</summary>
        private static void Flatten(EvalNode node, List<EvalNode> into)
        {
            if (node.Op == EvalOpCode.List)
            {
                for (ListNode? item = node.List; item is not null; item = item.Next)
                {
                    Flatten(item.Expr, into);
                }

                return;
            }

            into.Add(node);
        }
    }
}
