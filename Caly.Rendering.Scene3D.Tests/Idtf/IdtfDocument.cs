using System.Globalization;
using System.Numerics;
using System.Text;

namespace Caly.Rendering.Scene3D.Tests.Idtf;

/// <summary>
/// One element of an IDTF file: <c>KEYWORD arg… { children and numbers }</c>. IDTF is the text format the
/// U3D sample converter compiles into .u3d, so it is the ground truth for what a sample file contains.
/// </summary>
internal sealed class IdtfElement
{
    public required string Keyword { get; init; }

    public List<string> Arguments { get; } = [];

    public List<IdtfElement> Children { get; } = [];

    /// <summary>Bare numbers appearing directly inside the element's braces.</summary>
    public List<double> Numbers { get; } = [];

    public string Argument => Arguments.Count > 0 ? Arguments[0] : "";

    public IdtfElement? Find(string keyword) => Children.FirstOrDefault(c => c.Keyword == keyword);

    public IdtfElement Get(string keyword) => Find(keyword) ?? throw new KeyNotFoundException($"{Keyword} has no {keyword}");

    public IEnumerable<IdtfElement> All(string keyword) => Children.Where(c => c.Keyword == keyword);

    public string Text(string keyword) => Get(keyword).Argument;

    public string? TextOrNull(string keyword) => Find(keyword)?.Argument;

    public double Number(string keyword) => double.Parse(Get(keyword).Argument, CultureInfo.InvariantCulture);

    public double? NumberOrNull(string keyword) => Find(keyword) is { } e ? double.Parse(e.Argument, CultureInfo.InvariantCulture) : null;

    public float[] Floats(string keyword) => Get(keyword).Arguments.Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToArray();

    public Vector3 Vector(string keyword)
    {
        float[] v = Floats(keyword);
        return new Vector3(v[0], v[1], v[2]);
    }

    public override string ToString() => $"{Keyword} {string.Join(' ', Arguments)}";
}

internal sealed class IdtfDocument
{
    public IdtfElement Root { get; }

    private IdtfDocument(IdtfElement root)
    {
        Root = root;
    }

    public static IdtfDocument Load(string path) => Parse(File.ReadAllText(path));

    public IEnumerable<IdtfElement> Nodes => Root.All("NODE");

    /// <summary>The resources of one list, for example "MODEL", "LIGHT", "VIEW", "SHADER", "MATERIAL", "TEXTURE".</summary>
    public IEnumerable<IdtfElement> Resources(string list)
    {
        return Root.All("RESOURCE_LIST").Where(l => l.Argument == list).SelectMany(l => l.All("RESOURCE"));
    }

    public IEnumerable<IdtfElement> Modifiers(string type) => Root.All("MODIFIER").Where(m => m.Argument == type);

    public static IdtfDocument Parse(string text)
    {
        var tokens = Tokenize(text);
        var root = new IdtfElement { Keyword = "ROOT" };
        int position = 0;
        ParseContent(tokens, ref position, root, topLevel: true);
        return new IdtfDocument(root);
    }

    private static void ParseContent(List<Token> tokens, ref int position, IdtfElement parent, bool topLevel)
    {
        while (position < tokens.Count)
        {
            Token token = tokens[position];
            if (token.Kind == TokenKind.Close)
            {
                if (topLevel)
                {
                    throw new FormatException("Unbalanced '}' in IDTF.");
                }

                position++;
                return;
            }

            if (token.Kind == TokenKind.Number)
            {
                parent.Numbers.Add(double.Parse(token.Text, CultureInfo.InvariantCulture));
                position++;
                continue;
            }

            if (token.Kind != TokenKind.Word)
            {
                throw new FormatException($"Unexpected token '{token.Text}' in IDTF.");
            }

            var element = new IdtfElement { Keyword = token.Text };
            position++;
            while (position < tokens.Count && tokens[position].Kind is TokenKind.String or TokenKind.Number)
            {
                element.Arguments.Add(tokens[position].Text);
                position++;
            }

            if (position < tokens.Count && tokens[position].Kind == TokenKind.Open)
            {
                position++;
                ParseContent(tokens, ref position, element, topLevel: false);
            }

            parent.Children.Add(element);
        }

        if (!topLevel)
        {
            throw new FormatException("Missing '}' in IDTF.");
        }
    }

    private enum TokenKind { Word, String, Number, Open, Close }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '{')
            {
                tokens.Add(new Token(TokenKind.Open, "{"));
                i++;
            }
            else if (c == '}')
            {
                tokens.Add(new Token(TokenKind.Close, "}"));
                i++;
            }
            else if (c == '"')
            {
                // A backslash escapes the character after it (the samples use \\ and \").
                var value = new StringBuilder();
                int end = i + 1;
                while (end < text.Length && text[end] != '"')
                {
                    if (text[end] == '\\' && end + 1 < text.Length)
                    {
                        end++;
                    }

                    value.Append(text[end]);
                    end++;
                }

                if (end >= text.Length)
                {
                    throw new FormatException("Unterminated string in IDTF.");
                }

                tokens.Add(new Token(TokenKind.String, value.ToString()));
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
                {
                    i++;
                }

                string word = text.Substring(start, i - start);
                bool number = char.IsDigit(word[0]) || ((word[0] == '-' || word[0] == '+' || word[0] == '.') && word.Length > 1);
                tokens.Add(new Token(number ? TokenKind.Number : TokenKind.Word, word));
            }
        }

        return tokens;
    }
}
