using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Caly.Rendering.Scene3D.Prc.Generator;

/// <summary>A description of the format the generator cannot turn into a reader.</summary>
public sealed class GeneratorException(string message) : Exception(message);

/// <summary>
/// Translates the expressions of prc.json (conditions and sizes, written as Rust for prc-rs) to C#.
/// Only the forms listed here are accepted: a reference to a field read earlier, a call on the parsing
/// context, a named constant, an integer, the usual operators. Anything else stops the generation,
/// because a guess here would be a reader that silently reads a file wrongly.
/// </summary>
public static partial class RustExpression
{
    private static readonly HashSet<string> CastTypes = ["u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "usize", "isize"];

    private static readonly HashSet<string> BinaryOperators = ["&&", "||", "==", "!=", ">=", "<=", "<", ">", "&", "|", "+", "-", "*"];

    [GeneratedRegex(@"\G\s*(?:(?<id>[A-Za-z_][A-Za-z_0-9]*(?:::[A-Za-z_][A-Za-z_0-9]*)*)|(?<num>0x[0-9A-Fa-f]+|[0-9]+)(?<suffix>[a-z][a-z0-9]*)?|(?<op>&&|\|\||==|!=|>=|<=|[!&|()<>+\-*.]))")]
    private static partial Regex Token();

    /// <summary>Translates one expression.</summary>
    /// <param name="rust">The expression of prc.json.</param>
    /// <param name="local">The C# name of the local holding a field of the structure read so far, or null.</param>
    /// <param name="contextCalls">C# for each method of the parsing context an expression may call.</param>
    /// <param name="constants">The value of each named constant an expression may use.</param>
    /// <param name="asCount">Whether the expression is a number of elements: it is then computed as 64-bit, so that it cannot overflow.</param>
    public static string Translate(string rust, Func<string, string?> local, IReadOnlyDictionary<string, string> contextCalls, IReadOnlyDictionary<string, long> constants, bool asCount)
    {
        var tokens = new List<(char Kind, string Text)>();
        int at = 0;
        string trimmed = rust.TrimEnd();
        while (at < trimmed.Length)
        {
            Match match = Token().Match(trimmed, at);
            if (!match.Success || match.Length == 0)
            {
                throw new GeneratorException($"Cannot read the expression '{rust}' at position {at}.");
            }

            if (match.Groups["id"].Success)
            {
                tokens.Add(('i', match.Groups["id"].Value));
            }
            else if (match.Groups["num"].Success)
            {
                string suffix = match.Groups["suffix"].Value;
                if (suffix.Length != 0 && !CastTypes.Contains(suffix))
                {
                    throw new GeneratorException($"Unknown number suffix '{suffix}' in '{rust}'.");
                }

                tokens.Add(('n', match.Groups["num"].Value));
            }
            else
            {
                tokens.Add(('o', match.Groups["op"].Value));
            }

            at = match.Index + match.Length;
        }

        var output = new List<(char Kind, string Text)>();
        string Wide(string text) => asCount ? "(long)" + text : text;
        bool Is(int index, char kind, string text) => index < tokens.Count && tokens[index].Kind == kind && tokens[index].Text == text;

        for (int i = 0; i < tokens.Count; i++)
        {
            (char kind, string text) = tokens[i];
            if (kind == 'n')
            {
                output.Add(('v', text));
            }
            else if (kind == 'o')
            {
                if (text == ".")
                {
                    throw new GeneratorException($"Unexpected member access in '{rust}'.");
                }

                if (text == "!" && Is(i + 1, 'o', "!"))
                {
                    i++;   // "!!x" is how prc.json writes "x is true"
                    continue;
                }

                output.Add(('o', text));
            }
            else if (text == "as")
            {
                if (i + 1 >= tokens.Count || tokens[i + 1].Kind != 'i' || !CastTypes.Contains(tokens[i + 1].Text))
                {
                    throw new GeneratorException($"Unknown cast in '{rust}'.");
                }

                i++;
            }
            else if (text == "self")
            {
                if (!Is(i + 1, 'o', "."))
                {
                    throw new GeneratorException($"'self' alone in '{rust}'.");
                }

                i++;
            }
            else if (text == "_ctx")
            {
                if (!Is(i + 1, 'o', ".") || i + 2 >= tokens.Count || tokens[i + 2].Kind != 'i')
                {
                    throw new GeneratorException($"Unknown use of the parsing context in '{rust}'.");
                }

                // A call of the context (`_ctx.name()`) or a value it holds (`_ctx.name`).
                bool isCall = Is(i + 3, 'o', "(") && Is(i + 4, 'o', ")");
                if (!contextCalls.TryGetValue(tokens[i + 2].Text, out string? call))
                {
                    throw new GeneratorException($"Unknown call on the parsing context, '{tokens[i + 2].Text}', in '{rust}'.");
                }

                output.Add(('v', Wide(call)));
                i += isCall ? 4 : 2;
            }
            else if (constants.TryGetValue(text, out long constant))
            {
                output.Add(('v', constant.ToString(CultureInfo.InvariantCulture)));
            }
            else if (text.Contains("::", StringComparison.Ordinal))
            {
                throw new GeneratorException($"Unknown constant '{text}' in '{rust}'.");
            }
            else
            {
                if (Is(i + 1, 'o', ".") && Is(i + 2, 'i', "value"))
                {
                    i += 2;
                }

                string name = local(text) ?? throw new GeneratorException($"'{text}' in '{rust}' is not a value read earlier in the same structure.");
                output.Add(('v', Wide(name)));
            }
        }

        // Shape check, so that a mistranslation cannot hide behind text that happens to compile.
        int depth = 0;
        bool expectValue = true;
        var result = new StringBuilder();
        foreach ((char kind, string text) in output)
        {
            if (kind == 'v')
            {
                if (!expectValue)
                {
                    throw new GeneratorException($"Two values without an operator in '{rust}'.");
                }

                result.Append(text);
                expectValue = false;
            }
            else if (text == "(")
            {
                if (!expectValue)
                {
                    throw new GeneratorException($"Unexpected '(' in '{rust}'.");
                }

                depth++;
                result.Append('(');
            }
            else if (text == ")")
            {
                if (expectValue || --depth < 0)
                {
                    throw new GeneratorException($"Unexpected ')' in '{rust}'.");
                }

                result.Append(')');
            }
            else if (text == "!")
            {
                if (!expectValue)
                {
                    throw new GeneratorException($"Unexpected '!' in '{rust}'.");
                }

                result.Append('!');
            }
            else if (BinaryOperators.Contains(text))
            {
                if (expectValue)
                {
                    throw new GeneratorException($"Operator '{text}' without a left side in '{rust}'.");
                }

                result.Append(' ').Append(text).Append(' ');
                expectValue = true;
            }
            else
            {
                throw new GeneratorException($"Unexpected '{text}' in '{rust}'.");
            }
        }

        if (expectValue || depth != 0)
        {
            throw new GeneratorException($"The expression '{rust}' is incomplete.");
        }

        return result.ToString();
    }
}
