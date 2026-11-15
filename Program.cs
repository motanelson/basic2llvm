using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BasicToLLVM
{
    // BASIC to LLVM IR compiler - console edition.
    // Usage: BasicToLLVM.exe input.bas output.ll

    internal sealed class Token
    {
        public string Type { get; }
        public string Value { get; }
        public string Upper { get; }
        public int Line { get; }

        public Token(string type, string value, int line)
        {
            Type = type;
            Value = value;
            Upper = value.ToUpperInvariant();
            Line = line;
        }
    }

    internal sealed class Lexer
    {
        private readonly string source;
        private int pos;
        private int line = 1;

        private static readonly HashSet<string> Prohibited =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN",
                "GOSUB", "RETURN", "DEF", "FN"
            };

        public Lexer(string sourceText)
        {
            source = sourceText ?? "";
        }

        private char? Peek()
        {
            return pos < source.Length ? source[pos] : (char?)null;
        }

        private char Next()
        {
            char ch = source[pos++];
            if (ch == '\n')
                line++;
            return ch;
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();

            while (pos < source.Length)
            {
                char ch = source[pos];

                if (ch == ' ' || ch == '\t' || ch == '\r')
                {
                    Next();
                    continue;
                }

                if (ch == '\n')
                {
                    tokens.Add(new Token("NEWLINE", "", line));
                    Next();
                    continue;
                }

                // Apostrophe comments.
                if (ch == '\'')
                {
                    while (Peek() != null && Peek() != '\n')
                        Next();
                    continue;
                }

                // String literals.
                if (ch == '"')
                {
                    Next();
                    var value = new StringBuilder();

                    while (Peek() != null && Peek() != '"' && Peek() != '\n')
                        value.Append(Next());

                    if (Peek() == '"')
                        Next();

                    tokens.Add(new Token("STRING", value.ToString(), line));
                    continue;
                }

                // Integer literals.
                if (char.IsDigit(ch))
                {
                    var number = new StringBuilder();
                    while (Peek() != null && char.IsDigit(Peek().Value))
                        number.Append(Next());

                    tokens.Add(new Token("NUMBER", number.ToString(), line));
                    continue;
                }

                // Identifiers and reserved words.
                if (char.IsLetter(ch) || ch == '_')
                {
                    var ident = new StringBuilder();
                    while (Peek() != null &&
                           (char.IsLetterOrDigit(Peek().Value) || Peek() == '_'))
                        ident.Append(Next());

                    string name = ident.ToString();
                    string upper = name.ToUpperInvariant();

                    if (upper == "REM")
                    {
                        while (Peek() != null && Peek() != '\n')
                            Next();
                        continue;
                    }

                    if (Prohibited.Contains(upper))
                        throw new Exception(
                            $"Line {line}: Keyword \"{upper}\" is not supported in this dialect.");

                    tokens.Add(new Token("IDENT", name, line));
                    continue;
                }

                // Multi-character relational operators.
                if (ch == '<')
                {
                    Next();
                    if (Peek() == '=')
                    {
                        Next();
                        tokens.Add(new Token("<=", "", line));
                    }
                    else if (Peek() == '>')
                    {
                        Next();
                        tokens.Add(new Token("<>", "", line));
                    }
                    else
                    {
                        tokens.Add(new Token("<", "", line));
                    }
                    continue;
                }

                if (ch == '>')
                {
                    Next();
                    if (Peek() == '=')
                    {
                        Next();
                        tokens.Add(new Token(">=", "", line));
                    }
                    else
                    {
                        tokens.Add(new Token(">", "", line));
                    }
                    continue;
                }

                // Single-character tokens.
                if ("+-*/%()=:,".IndexOf(ch) >= 0)
                {
                    tokens.Add(new Token(ch.ToString(), "", line));
                    Next();
                    continue;
                }

                throw new Exception($"Line {line}: Unexpected character '{ch}'");
            }

            tokens.Add(new Token("EOF", "", line));
            return tokens;
        }
    }

    internal sealed class Expr
    {
        public string Type;
        public long Number;
        public string Name;
        public string Op;
        public Expr Left;
        public Expr Right;

        public static Expr Constant(long value) =>
            new Expr { Type = "const", Number = value };

        public static Expr Variable(string name) =>
            new Expr { Type = "var", Name = name };

        public static Expr Unary(string op, Expr expr) =>
            new Expr { Type = "unary_neg", Op = op, Right = expr };

        public static Expr Binary(string op, Expr left, Expr right) =>
            new Expr { Type = "binop", Op = op, Left = left, Right = right };
    }

    internal sealed class Condition
    {
        public Expr Left;
        public Expr Right;
        public string Op;
    }

    internal sealed class LLVMCompiler
    {
        private readonly List<Token> tokens;
        private int idx;
        private readonly HashSet<string> variables = new HashSet<string>();
        private readonly Dictionary<string, string> stringTable =
            new Dictionary<string, string>();
        private int stringCounter;
        private int tempRegCounter = 1;
        private int labelBlockCounter = 1;
        private readonly List<string> instructions = new List<string>();
        private readonly HashSet<string> declaredLabels =
            new HashSet<string>();

        private bool blockTerminated;

        public LLVMCompiler(List<Token> inputTokens)
        {
            tokens = inputTokens;
        }

        private Token Peek()
        {
            return tokens[idx];
        }

        private Token Next()
        {
            return tokens[idx++];
        }

        private Token Match(string type)
        {
            if (Peek().Type == type)
                return Next();
            return null;
        }

        private Token Expect(string type)
        {
            Token token = Match(type);
            if (token == null)
            {
                Token current = Peek();
                string found = string.IsNullOrEmpty(current.Value)
                    ? current.Type
                    : current.Value;
                throw new Exception(
                    $"Line {current.Line}: Expected '{type}' but found '{found}'");
            }
            return token;
        }

        private string GetStringGlobal(string value)
        {
            if (stringTable.TryGetValue(value, out string existing))
                return existing;

            string name = ".str." + stringCounter++;
            stringTable[value] = name;
            return name;
        }

        private string FreshReg()
        {
            // Use named LLVM temporaries instead of numeric slots (%1, %2, ...).
            // The dot also keeps these names distinct from BASIC variable names,
            // whose identifiers cannot contain a dot.
            return "%tmp." + tempRegCounter++;
        }

        private void Emit(string instruction)
        {
            if (!blockTerminated)
                instructions.Add("  " + instruction);
        }

        private void EmitLabel(string labelName)
        {
            if (!blockTerminated)
                Emit("br label %" + labelName);

            instructions.Add(labelName + ":");
            blockTerminated = false;
        }

        // Expression parser with operator precedence.
        private Expr ParsePrimary()
        {
            Token token = Peek();

            if (token.Type == "NUMBER")
            {
                Next();
                if (!long.TryParse(token.Value, out long number))
                    throw new Exception($"Line {token.Line}: Integer literal is out of range.");
                return Expr.Constant(number);
            }

            if (token.Type == "IDENT")
            {
                Next();
                string name = token.Value.ToLowerInvariant();
                variables.Add(name);
                return Expr.Variable(name);
            }

            if (Match("(") != null)
            {
                Expr expression = ParseExpression(0);
                Expect(")");
                return expression;
            }

            if (Match("-") != null)
                return Expr.Unary("-", ParsePrimary());

            throw new Exception(
                $"Line {token.Line}: Unexpected token in expression: '{(string.IsNullOrEmpty(token.Value) ? token.Type : token.Value)}'");
        }

        private int GetPrecedence(string type)
        {
            switch (type)
            {
                case "*":
                case "/":
                case "%":
                    return 20;
                case "+":
                case "-":
                    return 10;
                default:
                    return 0;
            }
        }

        private Expr ParseExpression(int minPrecedence = 0)
        {
            Expr left = ParsePrimary();

            while (true)
            {
                string op = Peek().Type;
                int precedence = GetPrecedence(op);
                if (precedence <= minPrecedence)
                    break;

                Next();
                Expr right = ParseExpression(precedence);
                left = Expr.Binary(op, left, right);
            }

            return left;
        }

        private string GenerateExprIR(Expr node)
        {
            if (node.Type == "const")
                return node.Number.ToString();

            if (node.Type == "var")
            {
                string reg = FreshReg();
                Emit($"{reg} = load i64, i64* %{node.Name}, align 8");
                return reg;
            }

            if (node.Type == "unary_neg")
            {
                string value = GenerateExprIR(node.Right);
                string reg = FreshReg();
                Emit($"{reg} = sub nsw i64 0, {value}");
                return reg;
            }

            if (node.Type == "binop")
            {
                string left = GenerateExprIR(node.Left);
                string right = GenerateExprIR(node.Right);
                string reg = FreshReg();

                string llvmOp;
                switch (node.Op)
                {
                    case "+": llvmOp = "add nsw"; break;
                    case "-": llvmOp = "sub nsw"; break;
                    case "*": llvmOp = "mul nsw"; break;
                    case "/": llvmOp = "sdiv"; break;
                    case "%": llvmOp = "srem"; break;
                    default: throw new Exception("Unknown arithmetic operator.");
                }

                Emit($"{reg} = {llvmOp} i64 {left}, {right}");
                return reg;
            }

            throw new Exception("Unknown expression AST node");
        }

        private Condition ParseCondition()
        {
            Expr left = ParseExpression(0);
            Token op = Peek();

            string[] valid = { "=", "<>", "<", "<=", ">", ">=" };
            if (!valid.Contains(op.Type))
                throw new Exception(
                    $"Line {op.Line}: Expected relational operator, got '{op.Type}'");

            Next();
            Expr right = ParseExpression(0);
            return new Condition { Left = left, Op = op.Type, Right = right };
        }

        public string CompileProgram()
        {
            // First pass: discover labels.
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (tokens[i].Type == "IDENT" && tokens[i + 1].Type == ":")
                    declaredLabels.Add(tokens[i].Value.ToLowerInvariant());
            }

            // Main parser loop.
            while (Peek().Type != "EOF")
            {
                if (Match("NEWLINE") != null)
                    continue;

                Token token = Peek();

                // Label declaration.
                if (token.Type == "IDENT" &&
                    idx + 1 < tokens.Count &&
                    tokens[idx + 1].Type == ":")
                {
                    string label = token.Value.ToLowerInvariant();
                    Next();
                    Next();
                    EmitLabel("lbl_" + label);
                    continue;
                }

                if (token.Type == "IDENT")
                {
                    string upper = token.Upper;

                    if (upper == "PRINT")
                    {
                        Next();
                        ParsePrint();
                        continue;
                    }

                    if (upper == "INPUT")
                    {
                        Next();
                        string variable = Expect("IDENT").Value.ToLowerInvariant();
                        variables.Add(variable);
                        Emit("call i32 (i8*, ...) @scanf(i8* getelementptr inbounds ([5 x i8], [5 x i8]* @.fmt_scan, i32 0, i32 0), i64* %" + variable + ")");
                        continue;
                    }

                    if (upper == "GOTO")
                    {
                        Next();
                        string target = Expect("IDENT").Value.ToLowerInvariant();
                        Emit("br label %lbl_" + target);
                        blockTerminated = true;
                        continue;
                    }

                    if (upper == "IF")
                    {
                        Next();
                        ParseIf();
                        continue;
                    }

                    if (upper == "LET")
                    {
                        Next();
                        ParseAssignment();
                        continue;
                    }

                    if (upper == "END" || upper == "STOP")
                    {
                        Next();
                        Emit("ret i32 0");
                        blockTerminated = true;
                        continue;
                    }

                    // Implicit assignment: variable = expression.
                    if (idx + 1 < tokens.Count && tokens[idx + 1].Type == "=")
                    {
                        ParseAssignment();
                        continue;
                    }
                }

                throw new Exception(
                    $"Line {token.Line}: Unrecognized statement or syntax '{(string.IsNullOrEmpty(token.Value) ? token.Type : token.Value)}'");
            }

            if (!blockTerminated)
                Emit("ret i32 0");

            return AssembleLLVM();
        }

        private void ParsePrint()
        {
            bool first = true;

            while (Peek().Type != "NEWLINE" && Peek().Type != "EOF")
            {
                if (!first && Match(",") != null)
                {
                    // Comma separator.
                }

                Token token = Peek();

                if (token.Type == "STRING")
                {
                    Next();
                    string value = token.Value + "\n";
                    string globalName = GetStringGlobal(value);
                    int stringLength = Encoding.UTF8.GetByteCount(token.Value) + 2;

                    Emit($"call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([{stringLength} x i8], [{stringLength} x i8]* @{globalName}, i32 0, i32 0))");
                }
                else
                {
                    string value = GenerateExprIR(ParseExpression(0));
                    Emit("call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([6 x i8], [6 x i8]* @.fmt_int_nl, i32 0, i32 0), i64 " + value + ")");
                }

                first = false;

                if (Peek().Type == "NEWLINE" || Peek().Type == "EOF")
                    break;
                if (Peek().Type != ",")
                    break;
            }
        }

        private void ParseAssignment()
        {
            Token identifier = Expect("IDENT");
            string name = identifier.Value.ToLowerInvariant();
            variables.Add(name);

            Expect("=");
            Expr expression = ParseExpression(0);
            string result = GenerateExprIR(expression);
            Emit($"store i64 {result}, i64* %{name}, align 8");
        }

        private void ParseIf()
        {
            Condition condition = ParseCondition();

            Token thenToken = Expect("IDENT");
            if (thenToken.Upper != "THEN")
                throw new Exception($"Line {thenToken.Line}: Expected 'THEN' after condition");

            if (Peek().Type == "IDENT" && Peek().Upper == "GOTO")
                Next();

            string target = Expect("IDENT").Value.ToLowerInvariant();

            string left = GenerateExprIR(condition.Left);
            string right = GenerateExprIR(condition.Right);
            string conditionRegister = FreshReg();

            string comparison;
            switch (condition.Op)
            {
                case "=": comparison = "eq"; break;
                case "<>": comparison = "ne"; break;
                case "<": comparison = "slt"; break;
                case "<=": comparison = "sle"; break;
                case ">": comparison = "sgt"; break;
                case ">=": comparison = "sge"; break;
                default: throw new Exception("Unknown relational operator.");
            }

            Emit($"{conditionRegister} = icmp {comparison} i64 {left}, {right}");

            string continuation = "if_cont_" + labelBlockCounter++;
            Emit($"br i1 {conditionRegister}, label %lbl_{target}, label %{continuation}");
            blockTerminated = true;
            instructions.Add(continuation + ":");
            blockTerminated = false;
        }

        private static string EscapeLLVMString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            var result = new StringBuilder();

            foreach (byte b in bytes)
            {
                if (b == 10)
                    result.Append("\\0A");
                else if (b >= 32 && b <= 126 && b != 34 && b != 92)
                    result.Append((char)b);
                else
                    result.Append("\\").Append(b.ToString("X2"));
            }

            result.Append("\\00");
            return result.ToString();
        }

        private string AssembleLLVM()
        {
            var lines = new List<string>
            {
                "; --- Generated by BASIC to LLVM Compiler ---",
                "source_filename = \"program.bas\"",
                "target datalayout = \"e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128\"",
                "target triple = \"x86_64-pc-linux-gnu\"",
                "",
                "@.fmt_int_nl = private unnamed_addr constant [6 x i8] c\"%lld\\0A\\00\", align 1",
                "@.fmt_scan = private unnamed_addr constant [5 x i8] c\"%lld\\00\", align 1"
            };

            // String constants.
            foreach (KeyValuePair<string, string> item in stringTable)
            {
                int length = Encoding.UTF8.GetByteCount(item.Key) + 1;
                lines.Add($"@{item.Value} = private unnamed_addr constant [{length} x i8] c\"{EscapeLLVMString(item.Key)}\", align 1");
            }

            lines.Add("");
            lines.Add("declare i32 @printf(i8*, ...)");
            lines.Add("declare i32 @scanf(i8*, ...)");
            lines.Add("");
            lines.Add("define i32 @main() {");
            lines.Add("entry:");

            // Allocate and initialize all variables in the entry block.
            foreach (string variable in variables.OrderBy(v => v, StringComparer.Ordinal))
                lines.Add($"  %{variable} = alloca i64, align 8");

            if (variables.Count > 0)
            {
                lines.Add("  ; Initialize variables to 0");
                foreach (string variable in variables.OrderBy(v => v, StringComparer.Ordinal))
                    lines.Add($"  store i64 0, i64* %{variable}, align 8");
            }

            lines.AddRange(instructions);
            lines.Add("}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    internal static class Program
    {
        private static readonly Dictionary<string, string> Presets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["factorial"] =
@"REM Factorial Calculation (Label-Only BASIC)
PRINT ""Factorial Calculator""
PRINT ""Enter a positive number:""
INPUT n

fact = 1
i = 1

loop:
IF i > n THEN GOTO finish
fact = fact * i
i = i + 1
GOTO loop

finish:
PRINT ""Factorial result is:""
PRINT fact
END",

                ["fibonacci"] =
@"REM Fibonacci Sequence Generator
PRINT ""Fibonacci Generator""
PRINT ""Enter how many terms to generate:""
INPUT count

a = 0
b = 1
idx = 0

print_loop:
IF idx >= count THEN GOTO done
PRINT a
next_val = a + b
a = b
b = next_val
idx = idx + 1
GOTO print_loop

done:
PRINT ""Done!""
END",

                ["collatz"] =
@"REM Collatz Conjecture (3N + 1)
PRINT ""Enter starting number:""
INPUT n
steps = 0

collatz_loop:
PRINT n
IF n = 1 THEN GOTO done
steps = steps + 1

REM Check if even (n % 2 == 0)
rem_val = n % 2
IF rem_val = 0 THEN GOTO is_even

REM Odd step
n = 3 * n + 1
GOTO collatz_loop

is_even:
n = n / 2
GOTO collatz_loop

done:
PRINT ""Total steps taken:""
PRINT steps
END",

                ["gcd"] =
@"REM Greatest Common Divisor (Euclidean)
PRINT ""Enter first number (A):""
INPUT a
PRINT ""Enter second number (B):""
INPUT b

euclid:
IF b = 0 THEN GOTO finish
temp = b
b = a % b
a = temp
GOTO euclid

finish:
PRINT ""GCD is:""
PRINT a
END"
            };

        private static void PrintUsage()
        {
            Console.WriteLine("BASIC to LLVM IR Compiler");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  BasicToLLVM <input.bas> <output.ll>");
            Console.WriteLine("  BasicToLLVM --example factorial <output.ll>");
            Console.WriteLine();
            Console.WriteLine("Examples: factorial, fibonacci, collatz, gcd");
        }

        private static int Main(string[] args)
        {
            Console.BackgroundColor = ConsoleColor.White;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Clear();
            if (args.Length == 0 || args.Length > 3)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                string source;
                string outputPath;

                if (args.Length == 3 &&
                    args[0].Equals("--example", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Presets.TryGetValue(args[1], out source))
                    {
                        Console.Error.WriteLine("Unknown example: " + args[1]);
                        return 1;
                    }
                    outputPath = args[2];
                }
                else if (args.Length == 2)
                {
                    source = System.IO.File.ReadAllText(args[0], Encoding.UTF8);
                    outputPath = args[1];
                }
                else
                {
                    PrintUsage();
                    return 1;
                }

                var lexer = new Lexer(source);
                List<Token> tokens = lexer.Tokenize();
                var compiler = new LLVMCompiler(tokens);
                string llvm = compiler.CompileProgram();

                System.IO.File.WriteAllText(outputPath, llvm + Environment.NewLine, new UTF8Encoding(false));

                int lineCount = llvm.Split('\n').Length;
                Console.WriteLine("Compilation successful.");
                Console.WriteLine("Input:  " + (args[0] == "--example" ? "example " + args[1] : args[0]));
                Console.WriteLine("Output: " + outputPath);
                Console.WriteLine("LLVM IR: " + lineCount + " lines generated.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Compilation failed:");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
