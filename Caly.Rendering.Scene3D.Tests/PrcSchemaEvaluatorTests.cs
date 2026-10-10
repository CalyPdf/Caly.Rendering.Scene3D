using Caly.Rendering.Scene3D.Prc.Bits;
using Caly.Rendering.Scene3D.Prc.Schema;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// The schemas a PRC file carries: small programs that say what a later version of the format added to
/// an entity, run to read past it. The programs come from the file, so they are tested as hostile too.
/// </summary>
public class PrcSchemaEvaluatorTests
{
    private const uint Boolean = 0, Double = 1, Character = 2, Unsigned = 3, Integer = 4, String = 5, FatherType = 6,
        Vector2D = 7, Vector3D = 8, Extent1D = 9, Extent2D = 10, Extent3D = 11, PtrType = 12, PtrSurface = 13, PtrCurve = 14,
        For = 15, SimpleFor = 16, If = 17, Else = 18, BlockStart = 19, BlockVersion = 20, BlockEnd = 21,
        Declare = 22, Set = 23, DeclareAndSet = 24, Value = 25, Constant = 26, ValueFor = 27, CurveIs3D = 28,
        Mult = 29, Div = 30, Add = 31, Sub = 32, Lt = 33, Le = 34, Gt = 35, Ge = 36, Eq = 37, Neq = 38;

    private const uint Entity = 700;

    /// <summary>Runs a schema over data and returns how many bits it read.</summary>
    private static long Run(uint[] schema, PrcBitWriter data, uint nativeVersion = 8137)
    {
        var evaluator = new PrcSchemaEvaluator(nativeVersion);
        evaluator.Add(Entity, schema);
        return Run(evaluator, data);
    }

    private static long Run(PrcSchemaEvaluator evaluator, PrcBitWriter data)
    {
        // A marker after the data shows a read too far as a wrong position, not as an exception.
        var reader = new PrcBitReader([.. data.ToArray(), 0xFF, 0xFF, 0xFF, 0xFF]);
        evaluator.Evaluate(ref reader, Entity);
        return reader.BitPosition;
    }

    private static Scene3DError Fails(uint[] schema, PrcBitWriter data)
    {
        return Assert.Throws<Scene3DException>(() =>
        {
            var evaluator = new PrcSchemaEvaluator();
            evaluator.Add(Entity, schema);
            var reader = new PrcBitReader(data.ToArray());
            evaluator.Evaluate(ref reader, Entity);
        }).Error;
    }

    [Fact]
    public void WithoutASchemaNothingIsRead()
    {
        var reader = new PrcBitReader([0xFF]);
        PrcSchemaEvaluator.None.Evaluate(ref reader, Entity);
        Assert.Equal(0, reader.BitPosition);

        var evaluator = new PrcSchemaEvaluator();
        evaluator.Add(Entity + 1, [BlockStart, Boolean, BlockEnd]);
        evaluator.Evaluate(ref reader, Entity);
        Assert.Equal(0, reader.BitPosition);
        Assert.Equal(1, evaluator.Count);
    }

    [Fact]
    public void TheWriterOfDoublesAgreesWithTheReader()
    {
        double[] values = [0, 1, -1, 0.5, 2, 1e-300, 1e300, Math.PI, -123456.789, 25.4, double.Epsilon, -0.0];
        var writer = new PrcBitWriter();
        foreach (double value in values)
        {
            writer.Double(value);
        }

        var reader = new PrcBitReader(writer.ToArray());
        foreach (double value in values)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        }
    }

    [Fact]
    public void EachKindOfDataIsReadAsItsOwnEncoding()
    {
        uint[] schema = [BlockStart, Boolean, Double, Character, Unsigned, Integer, String, Vector2D, Vector3D, Extent1D, Extent2D, Extent3D, BlockEnd];
        var data = new PrcBitWriter().Bit(true).Double(4.5).Bits(200, 8).Unsigned(70000).Integer(-70000).String("a name");
        for (int i = 0; i < 2 + 3 + 2 + 4 + 6; i++)
        {
            data.Double(i * 1.25);
        }

        Assert.Equal(data.BitLength, Run(schema, data));
    }

    /// <summary>
    /// What a version added is read only by a reader older than that version: a newer one has those
    /// fields in its own description of the entity.
    /// </summary>
    [Theory]
    [InlineData(8136u, false)]
    [InlineData(8137u, false)]
    [InlineData(8138u, true)]
    [InlineData(10001u, true)]
    public void AVersionBlockIsReadOnlyByAnOlderReader(uint blockVersion, bool read)
    {
        uint[] schema = [BlockStart, BlockVersion, blockVersion, Integer, Integer, BlockEnd, BlockEnd];
        var data = new PrcBitWriter().Integer(-1).Integer(-2);

        Assert.Equal(read ? data.BitLength : 0, Run(schema, data));
    }

    [Fact]
    public void VersionBlocksFollowEachOther()
    {
        // The shape of real schemas: what each version added, oldest first.
        uint[] schema = [BlockStart, BlockVersion, 7000, Double, BlockEnd, BlockVersion, 8200, Unsigned, BlockEnd, BlockVersion, 9000, String, Boolean, BlockEnd, BlockEnd];
        var data = new PrcBitWriter().Unsigned(300).String("added in 9000").Bit(true);

        Assert.Equal(data.BitLength, Run(schema, data));
        Assert.Equal(0, Run(schema, data, nativeVersion: 9000));
        Assert.Equal(new PrcBitWriter().String("added in 9000").Bit(true).BitLength, Run(schema, new PrcBitWriter().String("added in 9000").Bit(true), nativeVersion: 8200));
    }

    [Fact]
    public void AConditionChoosesWhatFollows()
    {
        // prc-rs's own test programs.
        uint[] withoutBlocks = [If, Eq, Boolean, Constant, 1, Double, Else, String];
        var yes = new PrcBitWriter().Bit(true).Double(456.789);
        var no = new PrcBitWriter().Bit(false).String("test-string");
        Assert.Equal(yes.BitLength, Run(withoutBlocks, yes));
        Assert.Equal(no.BitLength, Run(withoutBlocks, no));

        uint[] withBlocks = [If, Eq, Constant, 1, Boolean, BlockStart, Double, Double, BlockEnd, Else, BlockStart, String, String, BlockEnd];
        yes = new PrcBitWriter().Bit(true).Double(-4).Double(8);
        no = new PrcBitWriter().Bit(false).String("test-string1").String("test-string2");
        Assert.Equal(yes.BitLength, Run(withBlocks, yes));
        Assert.Equal(no.BitLength, Run(withBlocks, no));

        // Without an else, what follows the condition is read either way.
        uint[] noElse = [BlockStart, If, Boolean, Double, Unsigned, BlockEnd];
        yes = new PrcBitWriter().Bit(true).Double(3).Unsigned(9);
        no = new PrcBitWriter().Bit(false).Unsigned(9);
        Assert.Equal(yes.BitLength, Run(noElse, yes));
        Assert.Equal(no.BitLength, Run(noElse, no));
    }

    [Theory]
    [InlineData(Lt, 2, 3, true)]
    [InlineData(Lt, 3, 3, false)]
    [InlineData(Le, 3, 3, true)]
    [InlineData(Le, 4, 3, false)]
    [InlineData(Gt, 4, 3, true)]
    [InlineData(Gt, 3, 3, false)]
    [InlineData(Ge, 3, 3, true)]
    [InlineData(Ge, 2, 3, false)]
    [InlineData(Eq, 3, 3, true)]
    [InlineData(Eq, -3, 3, false)]
    [InlineData(Neq, -3, 3, true)]
    [InlineData(Neq, 3, 3, false)]
    public void ComparisonsTakeTheirOperandsInOrder(uint comparison, int left, int right, bool expected)
    {
        uint[] schema = [BlockStart, If, comparison, Integer, Integer, Double, BlockEnd];
        var data = new PrcBitWriter().Integer(left).Integer(right);
        long withoutDouble = data.BitLength;
        data.Double(1.5);

        Assert.Equal(expected ? data.BitLength : withoutDouble, Run(schema, data));
    }

    [Theory]
    [InlineData(Add, 4, 2, 6)]
    [InlineData(Sub, 4, 2, 2)]
    [InlineData(Mult, 4, 2, 8)]
    [InlineData(Div, 4, 2, 2)]
    [InlineData(Sub, 2, 4, 0)]
    public void ArithmeticGivesTheCountOfALoop(uint operation, int left, int right, int expectedCount)
    {
        // The example of 9.3.16, generalised: for (operation, an integer of the data, a constant) read a boolean.
        uint[] schema = [BlockStart, For, operation, Integer, Constant, (uint)right, Boolean, BlockEnd];
        var data = new PrcBitWriter().Integer(left);
        long expected = data.BitLength + expectedCount;
        data.Bits(0, 16);

        Assert.Equal(expected, Run(schema, data));
    }

    [Fact]
    public void ASimpleLoopTakesItsCountFromTheData()
    {
        // The example of 9.3.17: the count is an unsigned integer; the first turn reads one double more.
        uint[] schema = [SimpleFor, BlockStart, Integer, Double, If, Eq, ValueFor, Constant, 0, Double, BlockEnd];
        var data = new PrcBitWriter().Unsigned(3).Integer(-5).Double(1).Double(2).Integer(6).Double(3).Integer(7).Double(4);
        Assert.Equal(data.BitLength, Run(schema, data));

        // 200 as an unsigned integer is ten bits; read as a signed one it would be minus 56.
        data = new PrcBitWriter().Unsigned(200);
        for (int i = 0; i < 200; i++)
        {
            data.Bit(i % 3 == 0);
        }

        Assert.Equal(data.BitLength, Run([BlockStart, SimpleFor, Boolean, BlockEnd], data));

        // No turns: the body is passed over, and what follows it is read.
        data = new PrcBitWriter().Unsigned(0).String("after");
        Assert.Equal(data.BitLength, Run([BlockStart, SimpleFor, BlockStart, Double, Double, BlockEnd, String, BlockEnd], data));
    }

    [Fact]
    public void LoopsNest()
    {
        // Rows of varying length: for each row, a count and that many integers, then the row's number of booleans.
        uint[] schema = [BlockStart, SimpleFor, BlockStart, SimpleFor, Integer, For, ValueFor, Boolean, BlockEnd, Unsigned, BlockEnd];
        var data = new PrcBitWriter().Unsigned(3)
            .Unsigned(2).Integer(1).Integer(2)
            .Unsigned(0).Bit(true)
            .Unsigned(1).Integer(3).Bit(true).Bit(false)
            .Unsigned(77);

        Assert.Equal(data.BitLength, Run(schema, data));
    }

    [Fact]
    public void ValuesAreKeptAndUsed()
    {
        // 9.3.25: value 6 = value 5 + value 2, used as a count.
        uint[] schema =
        [
            BlockStart,
            DeclareAndSet, 5, Constant, 2,
            DeclareAndSet, 2, Integer,
            DeclareAndSet, 6, Add, Value, 5, Value, 2,
            For, Value, 6, Boolean,
            Declare, 9,
            For, Value, 9, Double,
            Set, 9, Unsigned,
            For, Value, 9, Boolean,
            BlockEnd,
        ];
        var data = new PrcBitWriter().Integer(3).Bits(0b10101, 5).Unsigned(4).Bits(0b1111, 4);

        Assert.Equal(data.BitLength, Run(schema, data));
    }

    [Fact]
    public void AFatherTypeIsReadByItsOwnSchema()
    {
        var evaluator = new PrcSchemaEvaluator();
        evaluator.Add(Entity, [BlockStart, FatherType, 55, Unsigned, PtrType, 801, BlockEnd]);
        evaluator.Add(55, [BlockStart, BlockVersion, 9000, String, BlockEnd, BlockEnd]);
        evaluator.Add(801, [BlockStart, Boolean, Boolean, BlockEnd]);
        var data = new PrcBitWriter().String("of the father").Unsigned(12).Bits(0b10, 2);
        Assert.Equal(data.BitLength, Run(evaluator, data));

        // A father without a schema adds nothing.
        evaluator = new PrcSchemaEvaluator();
        evaluator.Add(Entity, [BlockStart, FatherType, 55, Unsigned, BlockEnd]);
        data = new PrcBitWriter().Unsigned(12);
        Assert.Equal(data.BitLength, Run(evaluator, data));
    }

    [Fact]
    public void ObsoleteTokensAndTheirOperandMeanNothing()
    {
        // From a real file (and prc-rs's test): block, obsolete 39 with its operand, a version block.
        Assert.Equal(0, Run([19, 39, 1, 20, 8137, 0, 21, 21], new PrcBitWriter()));

        var data = new PrcBitWriter().Unsigned(5).Bit(true);
        Assert.Equal(data.BitLength, Run([BlockStart, 39, Double, Unsigned, 40, String, Boolean, BlockEnd], data));
    }

    [Fact]
    public void WhatIsPassedOverReadsNothing()
    {
        // Every construct, inside a block of a version this reader knows.
        uint[] schema =
        [
            BlockStart,
            BlockVersion, 8000,
            Boolean, Double, Character, Unsigned, Integer, String, Vector2D, Vector3D, Extent1D, Extent2D, Extent3D,
            FatherType, 55, PtrType, 172, PtrSurface, PtrCurve,
            For, Add, Integer, Constant, 6, Double,
            SimpleFor, BlockStart, Integer, If, Eq, ValueFor, Constant, 0, Double, BlockEnd,
            If, CurveIs3D, Vector3D, Else, Vector2D,
            Declare, 1, Set, 1, Integer, DeclareAndSet, 2, Value, 1,
            39, 7, 40, 8,
            BlockVersion, 9000, String, BlockEnd,
            BlockEnd,
            Unsigned,
            BlockEnd,
        ];
        var data = new PrcBitWriter().Unsigned(1234);

        Assert.Equal(data.BitLength, Run(schema, data));
    }

    [Fact]
    public void ExactGeometryInADisplayedEntityIsRefused()
    {
        Assert.Equal(Scene3DError.Unsupported, Fails([BlockStart, PtrSurface, BlockEnd], new PrcBitWriter().Bits(0, 32)));
        Assert.Equal(Scene3DError.Unsupported, Fails([BlockStart, PtrCurve, BlockEnd], new PrcBitWriter().Bits(0, 32)));
        Assert.Equal(Scene3DError.Unsupported, Fails([BlockStart, PtrType, 172, BlockEnd], new PrcBitWriter().Bits(0, 32)));
        Assert.Equal(Scene3DError.Unsupported, Fails([BlockStart, If, CurveIs3D, Vector3D, BlockEnd], new PrcBitWriter().Bits(0, 32)));
        Assert.Equal(Scene3DError.Unsupported, Fails([BlockStart, 41, BlockEnd], new PrcBitWriter().Bits(0, 32)));
    }

    /// <summary>Review focus: the program is input like any other.</summary>
    [Theory]
    [InlineData(new uint[] { If })]
    [InlineData(new uint[] { If, Boolean })]
    [InlineData(new uint[] { BlockStart, Boolean })]
    [InlineData(new uint[] { BlockVersion })]
    [InlineData(new uint[] { BlockVersion, 9000, Boolean })]
    [InlineData(new uint[] { For, Constant })]
    [InlineData(new uint[] { For, Constant, 2 })]
    [InlineData(new uint[] { SimpleFor })]
    [InlineData(new uint[] { DeclareAndSet, 4 })]
    [InlineData(new uint[] { DeclareAndSet, 4, BlockStart, BlockEnd })]   // sets a value to nothing
    [InlineData(new uint[] { For, Value, 4, Boolean })]                   // a value never set
    [InlineData(new uint[] { If, ValueFor, Boolean })]                    // no loop to count
    [InlineData(new uint[] { Eq, Boolean })]
    [InlineData(new uint[] { For, Div, Constant, 4, Constant, 0, Boolean })]   // no count comes of a division by zero
    [InlineData(new uint[] { FatherType })]
    [InlineData(new uint[] { FatherType, Entity })]                       // its own father
    [InlineData(new uint[] { PtrType, Entity })]
    [InlineData(new uint[] { 39 })]
    public void ABrokenProgramFailsCleanly(uint[] schema)
    {
        Assert.Equal(Scene3DError.InvalidValue, Fails(schema, new PrcBitWriter().Bits(0xFFFFFFFF, 32).Bits(0xFFFFFFFF, 32)));
    }

    [Fact]
    public void ATextIsNotANumber()
    {
        Assert.Equal(Scene3DError.InvalidValue, Fails([For, String, Boolean], new PrcBitWriter().String("seven").Bits(0, 32)));
        Assert.Equal(Scene3DError.InvalidValue, Fails([For, Vector2D, Boolean], new PrcBitWriter().Double(1).Double(2).Bits(0, 32)));

        // As a condition it is not true.
        var data = new PrcBitWriter().String("yes").Unsigned(3);
        Assert.Equal(data.BitLength, Run([BlockStart, If, String, Double, Unsigned, BlockEnd], data));
    }

    [Fact]
    public void DeepNestingIsRefusedNotOverflowed()
    {
        var schema = new uint[200_000];
        Array.Fill(schema, If);
        Assert.Equal(Scene3DError.InvalidValue, Fails(schema, new PrcBitWriter().Bits(0, 32)));

        Array.Fill(schema, BlockStart);
        Assert.Equal(Scene3DError.InvalidValue, Fails(schema, new PrcBitWriter().Bits(0, 32)));
    }

    [Fact]
    public void ALoopThatReadsNothingEnds()
    {
        // Four thousand million turns of nothing, three deep.
        uint[] schema = [For, Constant, 4_000_000_000, For, Constant, 4_000_000_000, For, Constant, 4_000_000_000, BlockStart, BlockEnd];
        long before = Environment.TickCount64;
        Assert.Equal(Scene3DError.InvalidValue, Fails(schema, new PrcBitWriter().Bits(0, 32)));
        Assert.True(Environment.TickCount64 - before < 5000);
    }

    [Fact]
    public void ALoopLongerThanTheDataIsTruncation()
    {
        Assert.Equal(Scene3DError.Truncated, Fails([For, Constant, 4_000_000_000, Double], new PrcBitWriter().Double(1).Double(2)));
        Assert.Equal(Scene3DError.Truncated, Fails([SimpleFor, String], new PrcBitWriter().Unsigned(100_000).String("one")));
    }
}
