using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.Prc.Bits;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>The bit encodings of compressed B-rep (ISO 14739-1 draft 7.9.20; PDF Association issues #706, #582, #666).</summary>
public sealed class PrcBrepBitsTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 12)]
    [InlineData(true, 13)]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(false, 7)]
    [InlineData(false, 14)]
    public void ACompressedEntityTypeReadsBackAsWritten(bool isCurve, byte value)
    {
        byte[] bits = new PrcBitWriter().CompressedEntityType(isCurve, value).Bits(0b101, 3).ToArray();
        var r = new PrcBitReader(bits);
        Assert.Equal(new PrcCompressedEntity(isCurve, value), r.ReadCompressedEntityType());
        Assert.Equal(0b101u, r.ReadBits(3));
    }

    [Fact]
    public void ACurveTypeIsTwoBitsAfterItsFlagAndFourForTheRareOnes()
    {
        Assert.Equal(3, new PrcBitWriter().CompressedEntityType(true, 1).BitLength);
        Assert.Equal(5, new PrcBitWriter().CompressedEntityType(true, 13).BitLength);
        Assert.Equal(5, new PrcBitWriter().CompressedEntityType(false, 9).BitLength);
    }

    [Fact]
    public void AnUnknownFaceTypeIsRefused()
    {
        byte[] bits = new PrcBitWriter().Bit(false).Bits(15, 4).ToArray();
        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            r.ReadCompressedEntityType();
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Fact]
    public void AnUnknownRareCurveTypeIsRefused()
    {
        byte[] bits = new PrcBitWriter().Bit(true).Bits(3, 2).Bits(2, 2).ToArray();
        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            r.ReadCompressedEntityType();
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.25)]
    [InlineData(-1.25)]
    [InlineData(1000.0)]
    public void AVariableBitDoubleIsAMultipleOfTheTolerance(double value)
    {
        const double tolerance = 0.25;
        byte[] bits = new PrcBitWriter().DoubleWithVariableBitNumber(value, 14, tolerance).ToArray();
        var r = new PrcBitReader(bits);
        Assert.Equal(value, r.ReadDoubleWithVariableBitNumber(14, tolerance));
        Assert.Equal(14, r.BitPosition);
    }

    [Fact]
    public void AVariableBitDoubleOfOneBitIsASignedZero()
    {
        byte[] bits = new PrcBitWriter().Bit(true).Bit(false).ToArray();
        var r = new PrcBitReader(bits);
        Assert.Equal(0.0, r.ReadDoubleWithVariableBitNumber(1, 0.25));
        Assert.Equal(0.0, r.ReadDoubleWithVariableBitNumber(1, 0.25));
        Assert.Equal(2, r.BitPosition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void AVariableBitDoubleOfNoBitsOrTooManyIsRefused(int bitCount)
    {
        byte[] bits = new byte[16];
        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            r.ReadDoubleWithVariableBitNumber(bitCount, 0.25);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 6)]
    [InlineData(1.5, -2.25, 0.5, 6 + 3 * 5)]
    public void ACompressedPointReadsBackAndTakesTheBitsItsWidthSays(double x, double y, double z, int expectedBits)
    {
        const double tolerance = 0.25;
        PrcBitWriter w = new PrcBitWriter().CompressedPoint(x, y, z, tolerance);
        Assert.Equal(expectedBits, w.BitLength);
        var r = new PrcBitReader(w.ToArray());
        Assert.Equal(new PrcVector(x, y, z), r.ReadCompressedPoint(tolerance));
        Assert.Equal(expectedBits, r.BitPosition);
    }

    [Fact]
    public void AWideCompressedPointIsThreeDoubles()
    {
        byte[] bits = new PrcBitWriter().Bits(31, 6).Double(1e12).Double(-3).Double(0.5).ToArray();
        var r = new PrcBitReader(bits);
        Assert.Equal(new PrcVector(1e12, -3, 0.5), r.ReadCompressedPoint(0.001));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ACompressedPointWithAToleranceThatIsNoLengthIsRefused(double tolerance)
    {
        byte[] bits = new PrcBitWriter().Bits(3, 6).Bits(0, 9).ToArray();
        Scene3DException e = Assert.Throws<Scene3DException>(() =>
        {
            var r = new PrcBitReader(bits);
            r.ReadCompressedPoint(tolerance);
        });
        Assert.Equal(Scene3DError.InvalidValue, e.Error);
    }
}
