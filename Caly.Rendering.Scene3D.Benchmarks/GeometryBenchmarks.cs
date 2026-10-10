using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace Caly.Rendering.Scene3D.Benchmarks;

[MemoryDiagnoser]
public class GeometryBenchmarks
{
    private float[] _positionsSmall = null!;
    private float[] _positionsLarge = null!;
    private Matrix4x4 _matrix;
    private Matrix4x4 _viewProjection;
    private Vector3 _min;
    private Vector3 _max;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(42);
        _positionsSmall = new float[300]; // 100 vertices
        for (int i = 0; i < _positionsSmall.Length; i++)
        {
            _positionsSmall[i] = (float)(rnd.NextDouble() * 200.0 - 100.0);
        }

        _positionsLarge = new float[30_000]; // 10,000 vertices
        for (int i = 0; i < _positionsLarge.Length; i++)
        {
            _positionsLarge[i] = (float)(rnd.NextDouble() * 200.0 - 100.0);
        }

        _matrix = Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(10, 20, 30);
        _viewProjection = _matrix * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 1.33f, 0.1f, 1000f);
        _min = new Vector3(-10, -20, -5);
        _max = new Vector3(15, 25, 35);
    }

    [Benchmark(Baseline = true)]
    public SceneBounds ComputeBounds_Small_Scalar()
    {
        return ComputeBounds_Scalar(_positionsSmall);
    }

    [Benchmark]
    public SceneBounds ComputeBounds_Small_Simd()
    {
        return ComputeBounds_Simd(_positionsSmall);
    }

    [Benchmark]
    public SceneBounds ComputeBounds_Large_Scalar()
    {
        return ComputeBounds_Scalar(_positionsLarge);
    }

    [Benchmark]
    public SceneBounds ComputeBounds_Large_Simd()
    {
        return ComputeBounds_Simd(_positionsLarge);
    }

    [Benchmark]
    public bool TryWorldBounds_Scalar()
    {
        return TryWorldBounds_Scalar(_min, _max, _matrix, out _, out _);
    }

    [Benchmark]
    public bool TryWorldBounds_Simd()
    {
        return TryWorldBounds_Simd(_min, _max, _matrix, out _, out _);
    }

    [Benchmark]
    public bool IsOutside_Scalar()
    {
        return IsOutside_Scalar(_min, _max, _viewProjection);
    }

    [Benchmark]
    public bool IsOutside_Simd()
    {
        return IsOutside_Simd(_min, _max, _viewProjection);
    }

    private static SceneBounds ComputeBounds_Scalar(ReadOnlySpan<float> positions)
    {
        if (positions.Length < 3)
        {
            return SceneBounds.Empty;
        }

        var min = new Vector3(positions[0], positions[1], positions[2]);
        Vector3 max = min;
        for (int i = 3; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new SceneBounds { Min = min, Max = max, IsEmpty = false };
    }

    private static SceneBounds ComputeBounds_Simd(ReadOnlySpan<float> positions)
    {
        if (positions.Length < 3)
        {
            return SceneBounds.Empty;
        }

        if (Vector128.IsHardwareAccelerated && positions.Length >= 8)
        {
            ref float ptr = ref MemoryMarshal.GetReference(positions);
            var minVec = Vector128.Create(ptr, Unsafe.Add(ref ptr, 1), Unsafe.Add(ref ptr, 2), 0f);
            var maxVec = minVec;

            int i = 3;
            int limit = positions.Length - 4;
            for (; i <= limit; i += 3)
            {
                var cur = Vector128.LoadUnsafe(ref ptr, (nuint)i);
                minVec = Vector128.Min(minVec, cur);
                maxVec = Vector128.Max(maxVec, cur);
            }

            var min = new Vector3(minVec[0], minVec[1], minVec[2]);
            var max = new Vector3(maxVec[0], maxVec[1], maxVec[2]);

            for (; i + 2 < positions.Length; i += 3)
            {
                var p = new Vector3(Unsafe.Add(ref ptr, i), Unsafe.Add(ref ptr, i + 1), Unsafe.Add(ref ptr, i + 2));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return new SceneBounds { Min = min, Max = max, IsEmpty = false };
        }

        return ComputeBounds_Scalar(positions);
    }

    private static bool TryWorldBounds_Scalar(Vector3 localMin, Vector3 localMax, in Matrix4x4 world, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = Vector3.Transform(
                new Vector3((corner & 1) == 0 ? localMin.X : localMax.X, (corner & 2) == 0 ? localMin.Y : localMax.Y, (corner & 4) == 0 ? localMin.Z : localMax.Z),
                world);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z);
    }

    private static bool TryWorldBounds_Simd(Vector3 localMin, Vector3 localMax, in Matrix4x4 world, out Vector3 min, out Vector3 max)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            var rowX = Vector128.Create(world.M11, world.M12, world.M13, 0f);
            var rowY = Vector128.Create(world.M21, world.M22, world.M23, 0f);
            var rowZ = Vector128.Create(world.M31, world.M32, world.M33, 0f);
            var rowT = Vector128.Create(world.M41, world.M42, world.M43, 0f);

            Vector3 c = (localMin + localMax) * 0.5f;
            Vector3 e = (localMax - localMin) * 0.5f;

            var newCenter = rowT + rowX * c.X + rowY * c.Y + rowZ * c.Z;
            var newExtent = Vector128.Abs(rowX) * e.X + Vector128.Abs(rowY) * e.Y + Vector128.Abs(rowZ) * e.Z;

            var minVec = newCenter - newExtent;
            var maxVec = newCenter + newExtent;

            min = new Vector3(minVec[0], minVec[1], minVec[2]);
            max = new Vector3(maxVec[0], maxVec[1], maxVec[2]);

            return float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z);
        }

        return TryWorldBounds_Scalar(localMin, localMax, world, out min, out max);
    }

    private static bool IsOutside_Scalar(Vector3 min, Vector3 max, in Matrix4x4 viewProjection)
    {
        int outside = 0b111111;
        for (int corner = 0; corner < 8 && outside != 0; corner++)
        {
            Vector4 clip = Vector4.Transform(
                new Vector4((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z, 1),
                viewProjection);

            int beyond = (clip.X < -clip.W ? 1 : 0) | (clip.X > clip.W ? 2 : 0)
                | (clip.Y < -clip.W ? 4 : 0) | (clip.Y > clip.W ? 8 : 0)
                | (clip.Z < -clip.W ? 16 : 0) | (clip.Z > clip.W ? 32 : 0);
            outside &= beyond;
        }

        return outside != 0;
    }

    private static bool IsOutside_Simd(Vector3 min, Vector3 max, in Matrix4x4 viewProjection)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            var rowX = Vector128.Create(viewProjection.M11, viewProjection.M12, viewProjection.M13, viewProjection.M14);
            var rowY = Vector128.Create(viewProjection.M21, viewProjection.M22, viewProjection.M23, viewProjection.M24);
            var rowZ = Vector128.Create(viewProjection.M31, viewProjection.M32, viewProjection.M33, viewProjection.M34);
            var rowW = Vector128.Create(viewProjection.M41, viewProjection.M42, viewProjection.M43, viewProjection.M44);

            int outside = 0b111111;
            for (int corner = 0; corner < 8 && outside != 0; corner++)
            {
                float px = (corner & 1) == 0 ? min.X : max.X;
                float py = (corner & 2) == 0 ? min.Y : max.Y;
                float pz = (corner & 4) == 0 ? min.Z : max.Z;

                var clip = rowW + rowX * px + rowY * py + rowZ * pz;
                float cx = clip[0], cy = clip[1], cz = clip[2], cw = clip[3];

                int beyond = (cx < -cw ? 1 : 0) | (cx > cw ? 2 : 0)
                    | (cy < -cw ? 4 : 0) | (cy > cw ? 8 : 0)
                    | (cz < -cw ? 16 : 0) | (cz > cw ? 32 : 0);
                outside &= beyond;
            }

            return outside != 0;
        }

        return IsOutside_Scalar(min, max, viewProjection);
    }
}
