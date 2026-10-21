using System.Globalization;

namespace Caly.Rendering.Scene3D.Prc.Tessellation;

/// <summary>
/// A single-stroke font for the text of markups: printable ASCII and the signs drawings use most (degree, plus-minus, diameter,
/// times, micro, squared, cubed); any other character is a box. A file names its fonts by family and size only, and gives each
/// text the box it fills, so any font laid out in that box keeps the writer's layout; this one is lines, as the rest of a markup is.
/// <para>
/// Drawn for this library. A glyph is on a grid where capitals are <see cref="CapHeight"/> high on the baseline y = 0, small
/// letters 8, descenders reach -4; it is its width, then strokes separated by ';', each a run of points "x,y" and of arcs
/// "(cx,cy,rx,ry,from,to)" (an ellipse's centre and radii, from one angle to another in degrees, counterclockwise when the
/// second is larger), joined in order.
/// </para>
/// </summary>
internal static class PrcStrokeFont
{
    /// <summary>The height of capitals, in the units of the glyphs.</summary>
    public const float CapHeight = 12;

    /// <summary>The space after a glyph, in the units of the glyphs.</summary>
    private const float Gap = 2.5f;

    /// <summary>A glyph: how far the next one starts, and its strokes, each a run of points (x, y pairs).</summary>
    public sealed record Glyph(float Advance, float[][] Strokes);

    private static readonly Dictionary<char, string> Definitions = new()
    {
        [' '] = "3|",
        ['!'] = "0|0,12 0,3;0,0.7 0,0",
        ['"'] = "2|0,12 0,9;2,12 2,9",
        ['#'] = "7.5|2,0 3,12;5,0 6,12;0,4 7,4;0.5,8 7.5,8",
        ['$'] = "6|(3,8.5,3,2.5,20,270) (3,3.5,3,2.5,90,-160);3,13 3,-1",
        ['%'] = "8|(1.5,10,1.5,2,0,360);(6.5,2,1.5,2,0,360);0,0 8,12",
        ['&'] = "8|8,0 2,8.5 (3,10.25,1.5,1.75,210,380) 0.5,3.5 (3,2.5,2.5,2.5,160,320) 7,5",
        ['\''] = "0|0,12 0,9",
        ['('] = "2|(4,6,4,7,120,240)",
        [')'] = "2|(-2,6,4,7,60,-60)",
        ['*'] = "6|3,12 3,6;0.4,10.5 5.6,7.5;0.4,7.5 5.6,10.5",
        ['+'] = "6|3,1 3,7;0,4 6,4",
        [','] = "1|1,1 0,-2",
        ['-'] = "4|0,4 4,4",
        ['.'] = "0|0,0 0,0.7",
        ['/'] = "5|0,-1 5,12",
        ['0'] = "6|(3,6,3,6,0,360)",
        ['1'] = "6|1,10 3.5,12 3.5,0",
        ['2'] = "6|(3,9,3,3,150,-40) 0,0 6,0",
        ['3'] = "6|(3,9,3,3,150,-90) (3,3,3,3,90,-150)",
        ['4'] = "6|4.5,0 4.5,12 0,3.5 6,3.5",
        ['5'] = "6|6,12 0.5,12 0.2,6.5 (3,4,3,4,140,-150)",
        ['6'] = "6|(3,6,3,6,60,180) (3,3.5,3,3.5,180,-180)",
        ['7'] = "6|0,12 6,12 2,0",
        ['8'] = "6|(3,9,2.6,3,0,360);(3,3,3,3,0,360)",
        ['9'] = "6|(3,8.5,3,3.5,0,360);6,8.5 (3,6,3,6,0,-120)",
        [':'] = "0|0,0 0,0.7;0,6.5 0,7.2",
        [';'] = "1|1,0.7 1,0 0,-2;1,6.5 1,7.2",
        ['<'] = "6|6,8 0,4 6,0",
        ['='] = "6|0,5.5 6,5.5;0,2.5 6,2.5",
        ['>'] = "6|0,8 6,4 0,0",
        ['?'] = "6|(3,9,3,3,160,-70) 3,4 3,3;3,0.7 3,0",
        ['@'] = "11|(5.5,6,2.5,3,0,360);8,9 8,3.5 (9.5,3.5,1.5,1.5,180,360) (5.5,6,5.5,6,0,300)",
        ['A'] = "8|0,0 4,12 8,0;1.5,4.5 6.5,4.5",
        ['B'] = "7|0,0 0,12 4.5,12 (4.5,9,2.5,3,90,-90) 0,6;0,6 4.5,6 (4.5,3,2.5,3,90,-90) 0,0",
        ['C'] = "8|(4,6,4,6,40,320)",
        ['D'] = "7|0,0 0,12 3,12 (3,6,4,6,90,-90) 0,0",
        ['E'] = "7|7,12 0,12 0,0 7,0;0,6 5,6",
        ['F'] = "7|7,12 0,12 0,0;0,6 5,6",
        ['G'] = "8|(4,6,4,6,40,350) 8,5 5,5",
        ['H'] = "7|0,0 0,12;7,0 7,12;0,6 7,6",
        ['I'] = "0|0,0 0,12",
        ['J'] = "6|6,12 6,3 (3,3,3,3,0,-180)",
        ['K'] = "7|0,0 0,12;7,12 0,4.5;2.5,7 7,0",
        ['L'] = "6|0,12 0,0 6,0",
        ['M'] = "9|0,0 0,12 4.5,4 9,12 9,0",
        ['N'] = "7|0,0 0,12 7,0 7,12",
        ['O'] = "8|(4,6,4,6,0,360)",
        ['P'] = "7|0,0 0,12 4,12 (4,8.75,3,3.25,90,-90) 0,5.5",
        ['Q'] = "8|(4,6,4,6,0,360);5,3 8.5,-0.5",
        ['R'] = "7|0,0 0,12 4,12 (4,8.75,3,3.25,90,-90) 0,5.5;3.5,5.5 7,0",
        ['S'] = "7|(3.5,9,3.5,3,20,270) (3.5,3,3.5,3,90,-160)",
        ['T'] = "8|0,12 8,12;4,12 4,0",
        ['U'] = "7|0,12 0,3.5 (3.5,3.5,3.5,3.5,180,360) 7,12",
        ['V'] = "8|0,12 4,0 8,12",
        ['W'] = "11|0,12 2.75,0 5.5,12 8.25,0 11,12",
        ['X'] = "7|0,12 7,0;0,0 7,12",
        ['Y'] = "8|0,12 4,6 8,12;4,6 4,0",
        ['Z'] = "7|0,12 7,12 0,0 7,0",
        ['['] = "3|3,13 0,13 0,-1 3,-1",
        ['\\'] = "5|0,12 5,-1",
        [']'] = "3|0,13 3,13 3,-1 0,-1",
        ['^'] = "6|0,8 3,12 6,8",
        ['_'] = "7|0,-1.5 7,-1.5",
        ['`'] = "2|0,12 2,10",
        ['a'] = "6|(3,4,3,4,0,360);6,8 6,0",
        ['b'] = "6|0,12 0,0;(3,4,3,4,0,360)",
        ['c'] = "6|(3,4,3,4,40,320)",
        ['d'] = "6|6,12 6,0;(3,4,3,4,0,360)",
        ['e'] = "6|0,4 6,4 (3,4,3,4,0,320)",
        ['f'] = "4|4,11.5 (3,10,1.5,2,60,180) 1.5,0;0,8 4,8",
        ['g'] = "6|(3,4,3,4,0,360);6,8 6,-1 (3,-1,3,3,0,-160)",
        ['h'] = "6|0,12 0,0;0,5 (3,5,3,3,180,0) 6,0",
        ['i'] = "0|0,0 0,8;0,10.5 0,11.5",
        ['j'] = "3|3,8 3,-1 (1.5,-1,1.5,3,0,-160);3,10.5 3,11.5",
        ['k'] = "6|0,12 0,0;6,8 0,2.5;2,4 6,0",
        ['l'] = "0|0,12 0,0",
        ['m'] = "10|0,8 0,0;0,5.5 (2.5,5.5,2.5,2.5,180,0) 5,0;5,5.5 (7.5,5.5,2.5,2.5,180,0) 10,0",
        ['n'] = "6|0,8 0,0;0,5 (3,5,3,3,180,0) 6,0",
        ['o'] = "6|(3,4,3,4,0,360)",
        ['p'] = "6|0,8 0,-4;(3,4,3,4,0,360)",
        ['q'] = "6|6,8 6,-4;(3,4,3,4,0,360)",
        ['r'] = "4.5|0,8 0,0;0,5 (3,5,3,3,180,60)",
        ['s'] = "5|(2.5,6,2.5,2,20,270) (2.5,2,2.5,2,90,-160)",
        ['t'] = "4|1.5,11 1.5,2 (3,2,1.5,2,180,300);0,8 4,8",
        ['u'] = "6|0,8 0,3 (3,3,3,3,180,360);6,8 6,0",
        ['v'] = "6|0,8 3,0 6,8",
        ['w'] = "9|0,8 2.25,0 4.5,8 6.75,0 9,8",
        ['x'] = "6|0,8 6,0;0,0 6,8",
        ['y'] = "6|0,8 3,0;6,8 1.5,-4",
        ['z'] = "6|0,8 6,8 0,0 6,0",
        ['{'] = "4|4,13 2.5,12.5 2,11 2,7.5 0,6 2,4.5 2,1 2.5,-0.5 4,-1",
        ['|'] = "0|0,13 0,-1",
        ['}'] = "4|0,13 1.5,12.5 2,11 2,7.5 4,6 2,4.5 2,1 1.5,-0.5 0,-1",
        ['~'] = "7|0,4.5 1.5,6 3.5,5 5.5,4 7,5.5",
        ['°'] = "4|(2,10,2,2,0,360)",
        ['±'] = "6|3,4 3,10;0,7 6,7;0,1 6,1",
        ['×'] = "6|0,7 6,1;0,1 6,7",
        ['Ø'] = "8|(4,6,4,6,0,360);0,-1 8,13",
        ['ø'] = "6|(3,4,3,4,0,360);0,-1 6,9",
        ['µ'] = "6|0,8 0,-4;0,3 (3,3,3,3,180,360);6,8 6,0",
        ['²'] = "4|(2,11,1.8,1.5,150,-40) 0.2,7.5 4,7.5",
        ['³'] = "4|(2,11.2,1.8,1.2,150,-90) (2,8.8,1.8,1.3,90,-150)",
    };

    private const string Box = "6|0,0 6,0 6,12 0,12 0,0";

    private static readonly Glyph BoxGlyph = Parse(Box);

    // Parsed once: a hundred small glyphs.
    private static readonly Dictionary<char, Glyph> Glyphs = ParseAll();

    /// <summary>The glyph of a character: a box for one the font does not have.</summary>
    public static Glyph Of(char c) => Glyphs.TryGetValue(c, out Glyph? glyph) ? glyph : BoxGlyph;

    /// <summary>How wide a text is in the units of the glyphs: the advances of its characters.</summary>
    public static float Width(string text)
    {
        float width = 0;
        foreach (char c in text)
        {
            width += Of(c).Advance;
        }

        return width;
    }

    private static Dictionary<char, Glyph> ParseAll()
    {
        var glyphs = new Dictionary<char, Glyph>(Definitions.Count);
        foreach (KeyValuePair<char, string> definition in Definitions)
        {
            glyphs.Add(definition.Key, Parse(definition.Value));
        }

        return glyphs;
    }

    private static Glyph Parse(string definition)
    {
        int bar = definition.IndexOf('|', StringComparison.Ordinal);
        float width = float.Parse(definition.AsSpan(0, bar), CultureInfo.InvariantCulture);
        var strokes = new List<float[]>();
        foreach (string stroke in definition[(bar + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var points = new List<float>();
            foreach (string token in stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Trim('(', ')').Split(',');
                var n = new float[parts.Length];
                for (int k = 0; k < parts.Length; k++)
                {
                    n[k] = float.Parse(parts[k], CultureInfo.InvariantCulture);
                }
                if (token[0] != '(')
                {
                    points.Add(n[0]);
                    points.Add(n[1]);
                    continue;
                }

                // An arc, in steps of at most 22.5 degrees.
                float from = n[4], to = n[5];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / 22.5f));
                for (int s = 0; s <= steps; s++)
                {
                    double angle = (from + ((to - from) * s / steps)) * Math.PI / 180;
                    points.Add(n[0] + (n[2] * (float)Math.Cos(angle)));
                    points.Add(n[1] + (n[3] * (float)Math.Sin(angle)));
                }
            }

            if (points.Count >= 4)
            {
                strokes.Add([.. points]);
            }
        }

        return new Glyph(width + Gap, [.. strokes]);
    }
}
