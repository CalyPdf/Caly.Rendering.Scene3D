using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Prc.Schema;

/// <summary>
/// Runs the schemas a PRC file carries. A file written in a later version of the format than a reader
/// knows describes, per entity type, the data that version added, as a small program of tokens; a reader
/// runs it at the end of each entity of that type to read past that data. Nearly every real file needs
/// this: they are written in versions no published text describes.
/// <para>
/// Only reading past is done: nothing the schemas read is kept. The program is untrusted input, so its
/// nesting and its work are bounded and every read is checked against the end of the data. The meaning
/// of the tokens follows ISO 14739-1 (clause 9) and the PDF Association's issues #406, #407 and #575;
/// the order of evaluation was checked against prc-rs (MIT; see THIRD-PARTY-NOTICES.md), which handles
/// the tokens the corpus uses. A token that asks for exact geometry to be read stops the reading as
/// unsupported: this reader has no exact geometry.
/// </para>
/// </summary>
internal sealed class PrcSchemaEvaluator
{
    /// <summary>The version of the format the generated readers read natively.</summary>
    public const uint NativeVersion = 8137;

    private const int MaxDepth = 64;

    private const uint DataBoolean = 0, DataDouble = 1, DataCharacter = 2, DataUnsignedInteger = 3, DataInteger = 4, DataString = 5,
        FatherType = 6, Vector2D = 7, Vector3D = 8, Extent1D = 9, Extent2D = 10, Extent3D = 11, PtrType = 12, PtrSurface = 13, PtrCurve = 14,
        For = 15, SimpleFor = 16, If = 17, Else = 18, BlockStart = 19, BlockVersion = 20, BlockEnd = 21,
        ValueDeclare = 22, ValueSet = 23, ValueDeclareAndSet = 24, Value = 25, ValueConstant = 26, ValueFor = 27, ValueCurveIs3D = 28,
        OperatorMult = 29, OperatorDiv = 30, OperatorAdd = 31, OperatorSub = 32, OperatorLt = 33, OperatorLe = 34, OperatorGt = 35,
        OperatorGe = 36, OperatorEq = 37, OperatorNeq = 38, Obsolete39 = 39, Obsolete40 = 40;

    private readonly Dictionary<uint, uint[]> _programs = [];
    private readonly Dictionary<uint, double> _variables = [];
    private readonly List<double> _stack = [];
    private readonly List<long> _loops = [];
    private readonly uint _nativeVersion;
    private long _steps;
    private long _startBit;

    public PrcSchemaEvaluator(uint nativeVersion = NativeVersion)
    {
        _nativeVersion = nativeVersion;
    }

    /// <summary>An evaluator that knows no schema: every evaluation reads nothing.</summary>
    public static PrcSchemaEvaluator None { get; } = new();

    /// <summary>Number of entity types that have a schema.</summary>
    public int Count => _programs.Count;

    /// <summary>Tokens outside the format's list met in parts that were passed over, not run.</summary>
    public int UnknownTokens { get; private set; }

    /// <summary>Sets the schema of an entity type; a later one for the same type replaces an earlier one.</summary>
    public void Add(uint entityType, uint[] tokens)
    {
        _programs[entityType] = tokens;
    }

    /// <summary>Reads what the file's schema says follows an entity of this type; nothing if it has none.</summary>
    public void Evaluate(ref PrcBitReader reader, uint entityType)
    {
        if (_programs.Count != 0)
        {
            _steps = 0;
            _startBit = reader.BitPosition;
            _variables.Clear();
            _loops.Clear();
            _stack.Clear();
            Evaluate(ref reader, entityType, 0);
        }
    }

    private void Evaluate(ref PrcBitReader reader, uint entityType, int depth)
    {
        if (!_programs.TryGetValue(entityType, out uint[]? tokens))
        {
            return;
        }

        if (depth > MaxDepth)
        {
            throw Invalid("The schemas of the PRC file refer to each other without end.");
        }

        int stackBase = _stack.Count;
        int position = 0;
        while (position < tokens.Length)
        {
            Step(ref reader, tokens, ref position, skip: false, depth + 1);
        }

        _stack.RemoveRange(stackBase, _stack.Count - stackBase);
    }

    /// <summary>Runs, or with <paramref name="skip"/> passes over, the instruction at <paramref name="position"/>.</summary>
    private void Step(ref PrcBitReader reader, uint[] tokens, ref int position, bool skip, int depth)
    {
        if (depth > MaxDepth)
        {
            throw Invalid("A schema of the PRC file is nested too deeply.");
        }

        if (position >= tokens.Length)
        {
            throw Invalid("A schema of the PRC file ends inside an instruction.");
        }

        // Every instruction that reads takes at least one bit; the rest is the program's own structure.
        if (++_steps > 1_000_000 + 64 * (reader.BitPosition - _startBit))
        {
            throw Invalid("A schema of the PRC file runs without reading anything.");
        }

        uint token = tokens[position++];
        switch (token)
        {
            case DataBoolean:
                if (!skip)
                {
                    _stack.Add(reader.ReadBoolean() ? 1 : 0);
                }

                break;

            case DataDouble:
                if (!skip)
                {
                    _stack.Add(reader.ReadDouble());
                }

                break;

            case DataCharacter:
                if (!skip)
                {
                    _stack.Add(reader.ReadUnsignedCharacter());
                }

                break;

            case DataUnsignedInteger:
                if (!skip)
                {
                    _stack.Add(reader.ReadUnsignedInteger());
                }

                break;

            case DataInteger:
                if (!skip)
                {
                    _stack.Add(reader.ReadInteger());
                }

                break;

            case DataString:
                if (!skip)
                {
                    reader.SkipString();
                    _stack.Add(double.NaN);
                }

                break;

            case FatherType:
            {
                uint father = Operand(tokens, ref position);
                if (!skip)
                {
                    Evaluate(ref reader, father, depth);
                }

                break;
            }

            case Vector2D:
            case Extent1D:
                ReadDoubles(ref reader, skip, 2, 1);
                break;

            case Vector3D:
                ReadDoubles(ref reader, skip, 3, 1);
                break;

            case Extent2D:
                ReadDoubles(ref reader, skip, 4, 2);
                break;

            case Extent3D:
                ReadDoubles(ref reader, skip, 6, 2);
                break;

            case PtrType:
            {
                // An entity of the type the next token names: one the schemas define, or one of the
                // format's own, which are all exact geometry, representation items or markup.
                uint type = Operand(tokens, ref position);
                if (!skip)
                {
                    if (!_programs.ContainsKey(type))
                    {
                        throw Geometry();
                    }

                    Evaluate(ref reader, type, depth);
                }

                break;
            }

            case PtrSurface:
            case PtrCurve:
            case ValueCurveIs3D:
                if (!skip)
                {
                    throw Geometry();
                }

                break;

            case For:
                if (skip)
                {
                    Step(ref reader, tokens, ref position, true, depth + 1);
                    Step(ref reader, tokens, ref position, true, depth + 1);
                }
                else
                {
                    Step(ref reader, tokens, ref position, false, depth + 1);
                    Loop(ref reader, tokens, ref position, Pop(), depth);
                }

                break;

            case SimpleFor:
                if (skip)
                {
                    Step(ref reader, tokens, ref position, true, depth + 1);
                }
                else
                {
                    // The count is in the data, an unsigned integer (9.3.17 and issue #575).
                    Loop(ref reader, tokens, ref position, reader.ReadUnsignedInteger(), depth);
                }

                break;

            case If:
                if (skip)
                {
                    Step(ref reader, tokens, ref position, true, depth + 1);
                    Step(ref reader, tokens, ref position, true, depth + 1);
                    if (position < tokens.Length && tokens[position] == Else)
                    {
                        Step(ref reader, tokens, ref position, true, depth + 1);
                    }
                }
                else
                {
                    Step(ref reader, tokens, ref position, false, depth + 1);
                    double condition = Pop();
                    bool isTrue = condition != 0 && !double.IsNaN(condition);
                    Step(ref reader, tokens, ref position, !isTrue, depth + 1);
                    if (position < tokens.Length && tokens[position] == Else)
                    {
                        Step(ref reader, tokens, ref position, isTrue, depth + 1);
                    }
                }

                break;

            case Else:
                Step(ref reader, tokens, ref position, skip, depth + 1);
                break;

            case BlockStart:
                Block(ref reader, tokens, ref position, skip, depth);
                break;

            case BlockEnd:
                break;

            case BlockVersion:
            {
                // What a version of the format added. A reader that already knows that version has
                // read it as fields of its own, and passes over the block.
                uint version = Operand(tokens, ref position);
                Block(ref reader, tokens, ref position, skip || version <= _nativeVersion, depth);
                break;
            }

            case ValueDeclare:
            {
                uint variable = Operand(tokens, ref position);
                if (!skip)
                {
                    _variables[variable] = 0;
                }

                break;
            }

            case ValueSet:
            case ValueDeclareAndSet:
            {
                uint variable = Operand(tokens, ref position);
                Step(ref reader, tokens, ref position, skip, depth + 1);
                if (!skip)
                {
                    _variables[variable] = Pop();
                }

                break;
            }

            case Value:
            {
                uint variable = Operand(tokens, ref position);
                if (!skip)
                {
                    if (!_variables.TryGetValue(variable, out double value))
                    {
                        throw Invalid("A schema of the PRC file uses a variable it never set.");
                    }

                    _stack.Add(value);
                }

                break;
            }

            case ValueConstant:
            {
                uint constant = Operand(tokens, ref position);
                if (!skip)
                {
                    _stack.Add(constant);
                }

                break;
            }

            case ValueFor:
                if (!skip)
                {
                    if (_loops.Count == 0)
                    {
                        throw Invalid("A schema of the PRC file asks for the counter of a loop outside any loop.");
                    }

                    _stack.Add(_loops[^1]);
                }

                break;

            case >= OperatorMult and <= OperatorNeq:
                Step(ref reader, tokens, ref position, skip, depth + 1);
                Step(ref reader, tokens, ref position, skip, depth + 1);
                if (!skip)
                {
                    double right = Pop(), left = Pop();
                    _stack.Add(token switch
                    {
                        OperatorMult => left * right,
                        OperatorDiv => left / right,
                        OperatorAdd => left + right,
                        OperatorSub => left - right,
                        OperatorLt => left < right ? 1 : 0,
                        OperatorLe => left <= right ? 1 : 0,
                        OperatorGt => left > right ? 1 : 0,
                        OperatorGe => left >= right ? 1 : 0,
                        OperatorEq => left == right ? 1 : 0,
                        _ => left != right ? 1 : 0,
                    });
                }

                break;

            case Obsolete39:
            case Obsolete40:
                // An obsolete token of older files: it and the index after it mean nothing (issue #407).
                Operand(tokens, ref position);
                break;

            default:
                // Not a token of the format. Passed over it costs nothing; run, nobody can say how much
                // of the data it stands for.
                if (!skip)
                {
                    throw new Scene3DException(Scene3DError.Unsupported, $"A schema of the PRC file uses an instruction ({token}) this reader does not know.");
                }

                UnknownTokens++;
                break;
        }
    }

    /// <summary>The instructions up to the block's end, and the end itself.</summary>
    private void Block(ref PrcBitReader reader, uint[] tokens, ref int position, bool skip, int depth)
    {
        while (true)
        {
            if (position >= tokens.Length)
            {
                throw Invalid("A block of a schema of the PRC file has no end.");
            }

            if (tokens[position] == BlockEnd)
            {
                position++;
                return;
            }

            Step(ref reader, tokens, ref position, skip, depth + 1);
        }
    }

    /// <summary>Runs the instruction at <paramref name="position"/> a number of times, and leaves the position after it.</summary>
    private void Loop(ref PrcBitReader reader, uint[] tokens, ref int position, double count, int depth)
    {
        if (!double.IsFinite(count))
        {
            // A division by zero, or a text or a vector taken for a number.
            throw Invalid("A loop of a schema of the PRC file has no number of turns.");
        }

        if (count < 1)
        {
            Step(ref reader, tokens, ref position, true, depth + 1);
            return;
        }

        int body = position;
        long iterations = (long)Math.Min(count, long.MaxValue / 2);
        _loops.Add(0);
        for (long i = 0; i < iterations; i++)
        {
            int stackBefore = _stack.Count;
            _loops[^1] = i;
            position = body;
            Step(ref reader, tokens, ref position, false, depth + 1);
            _stack.RemoveRange(stackBefore, _stack.Count - stackBefore);
        }

        _loops.RemoveAt(_loops.Count - 1);
    }

    private void ReadDoubles(ref PrcBitReader reader, bool skip, int count, int pushed)
    {
        if (skip)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            reader.ReadDouble();
        }

        for (int i = 0; i < pushed; i++)
        {
            _stack.Add(double.NaN);
        }
    }

    private double Pop()
    {
        if (_stack.Count == 0)
        {
            throw Invalid("A schema of the PRC file uses a value it never produced.");
        }

        double value = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return value;
    }

    private static uint Operand(uint[] tokens, ref int position)
    {
        if (position >= tokens.Length)
        {
            throw Invalid("A schema of the PRC file ends inside an instruction.");
        }

        return tokens[position++];
    }

    private static Scene3DException Invalid(string message) => new(Scene3DError.InvalidValue, message);

    private static Scene3DException Geometry() => new(Scene3DError.Unsupported, "A schema of the PRC file adds exact geometry to an entity that is displayed.");
}
