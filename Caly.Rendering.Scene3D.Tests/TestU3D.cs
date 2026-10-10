using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Caly.Rendering.Scene3D.Tests;

/// <summary>
/// Hand-assembles small U3D files for tests. Everything is written uncompressed, which is a valid encoding
/// for declaration blocks (plain values are the stream's bytes while no compressed symbol has been written).
/// </summary>
internal sealed class TestU3D
{
    private readonly MemoryStream _file = new();

    private readonly bool _nested;

    /// <summary>Profile bit: every "compressed" value in the file is written plainly.</summary>
    public const uint NoCompression = 4;

    public TestU3D(uint profile = 0)
    {
        // File header: version 0.0, declaration size and file size patched in ToArray.
        var data = new Data().I16(0).I16(0).U32(profile).U32(0).U64(0).U32(106);
        Block(0x00443355, data);
    }

    private TestU3D(bool nested)
    {
        _nested = nested;
    }

    public const uint NodeChain = 0, ModelResourceChain = 1, TextureChain = 2;

    public const uint Lit = 1, AlphaTest = 2, UseVertexColor = 4;

    /// <summary>One texture layer of a shader, in a given shader channel (0 – 7).</summary>
    public sealed record Layer(int Channel, string Texture)
    {
        public float Intensity { get; init; } = 1;

        /// <summary>As written in a file: 0 replace, 1 add, 2 multiply, 3 blend.</summary>
        public byte BlendFunction { get; init; } = 2;

        public byte BlendSource { get; init; } = 1;

        public float BlendConstant { get; init; } = 0.5f;

        public byte Mode { get; init; }

        public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;

        public byte Repeat { get; init; } = 3;
    }

    /// <summary>A modifier chain block containing the blocks <paramref name="content"/> adds.</summary>
    public TestU3D ModifierChain(string name, uint type, Action<TestU3D> content)
    {
        var nested = new TestU3D(nested: true);
        content(nested);
        byte[] blocks = nested._file.ToArray();

        var data = new Data().String(name).U32(type).U32(0);
        data.Pad().U32((uint)CountBlocks(blocks)).Bytes(blocks);
        return Block(0xFFFFFF14, data);
    }

    public TestU3D Material(string name, Vector3 ambient, Vector3 diffuse, Vector3 specular, Vector3 emissive, float reflectivity, float opacity)
    {
        return Block(0xFFFFFF54, new Data().String(name).U32(0x3F).Vector(ambient).Vector(diffuse).Vector(specular).Vector(emissive).F32(reflectivity).F32(opacity));
    }

    public TestU3D Shader(string name, string material, uint attributes = Lit, float alphaReference = 0, uint alphaFunction = 0x617, params Layer[] layers)
    {
        uint channels = 0;
        foreach (Layer layer in layers)
        {
            channels |= 1u << layer.Channel;
        }

        var data = new Data().String(name).U32(attributes).F32(alphaReference).U32(alphaFunction).U32(0x606).U32(1).U32(channels).U32(0).String(material);
        foreach (Layer layer in layers.OrderBy(l => l.Channel))
        {
            data.String(layer.Texture).F32(layer.Intensity).U8(layer.BlendFunction).U8(layer.BlendSource).F32(layer.BlendConstant).U8(layer.Mode)
                .Matrix(layer.Transform).Matrix(Matrix4x4.CreateScale(7)).U8(layer.Repeat);
        }

        return Block(0xFFFFFF53, data);
    }

    /// <summary>A shading modifier: one list of shader names per renderable element.</summary>
    public TestU3D ShadingModifier(string name, uint attributes, params string[][] shaderLists)
    {
        var data = new Data().String(name).U32(1).U32(attributes).U32((uint)shaderLists.Length);
        foreach (string[] list in shaderLists)
        {
            data.U32((uint)list.Length);
            foreach (string shader in list)
            {
                data.String(shader);
            }
        }

        return Block(0xFFFFFF45, data);
    }

    /// <summary>A texture declaration; an image with URLs is an external reference.</summary>
    public TestU3D TextureDeclaration(string name, uint width, uint height, byte type, params (byte Compression, byte Channels, uint ByteCount, string[]? Urls)[] images)
    {
        var data = new Data().String(name).U32(height).U32(width).U8(type).U32((uint)images.Length);
        foreach ((byte compression, byte channels, uint byteCount, string[]? urls) in images)
        {
            data.U8(compression).U8(channels);
            if (urls is null)
            {
                data.U16(0).U32(byteCount);
            }
            else
            {
                data.U16(1).U32((uint)urls.Length);
                foreach (string url in urls)
                {
                    data.String(url);
                }
            }
        }

        return Block(0xFFFFFF55, data);
    }

    public TestU3D TextureContinuation(string name, uint imageIndex, ReadOnlySpan<byte> imageData)
    {
        return Block(0xFFFFFF5C, new Data().String(name).U32(imageIndex).Bytes(imageData));
    }

    /// <summary>
    /// A CLOD mesh declaration. Each shading is (attributes: 1 diffuse, 2 specular; texture layer count).
    /// </summary>
    /// <summary>A bone of a skeleton description (ECMA-363 9.6.1.1.6).</summary>
    public readonly record struct Bone(string Name, string Parent, float Length, Vector3 Displacement, Quaternion Rotation);

    public TestU3D MeshDeclaration(string name, uint attributes, uint faces, uint positions, uint normals, uint diffuse, uint specular, uint texCoords,
        (uint Attributes, uint Layers)[] shadings, uint minimumResolution, Bone[]? bones = null)
    {
        var data = new Data().String(name).U32(0).U32(attributes).U32(faces).U32(positions).U32(normals).U32(diffuse).U32(specular).U32(texCoords).U32((uint)shadings.Length);
        for (int i = 0; i < shadings.Length; i++)
        {
            data.U32(shadings[i].Attributes).U32(shadings[i].Layers);
            for (uint layer = 0; layer < shadings[i].Layers; layer++)
            {
                data.U32(2);
            }

            data.U32((uint)i);
        }

        data.U32(minimumResolution).U32(positions);
        data.U32(1000).U32(1000).U32(1000);                       // quality factors
        data.F32(1).F32(1).F32(1).F32(1).F32(1);                  // inverse quantisation
        data.F32(0.9f).F32(0.9f).F32(0.985f);                     // normal parameters
        bones ??= [];
        data.U32((uint)bones.Length);
        foreach (Bone bone in bones)
        {
            // As the reference decoder reads them: attributes come before the length.
            data.String(bone.Name).String(bone.Parent).U32(0).F32(bone.Length).Vector(bone.Displacement);
            data.F32(bone.Rotation.W).F32(bone.Rotation.X).F32(bone.Rotation.Y).F32(bone.Rotation.Z);
            data.F32(0).F32(0).F32(0).F32(0).F32(0).F32(0);        // rotation constraints
        }

        return Block(0xFFFFFF31, data);
    }

    /// <summary>
    /// A CLOD base mesh continuation for a file with the <see cref="NoCompression"/> profile. Each face is
    /// its shading id followed by the indices of its three corners in file order (position, normal unless
    /// excluded, diffuse and specular if the shading has them, one texture coordinate per layer).
    /// </summary>
    public TestU3D BaseMesh(string name, Vector3[] positions, Vector3[] normals, Vector4[] diffuse, Vector4[] specular, Vector4[] texCoords, params uint[][] faces)
    {
        var data = new Data().String(name).U32(0).U32((uint)faces.Length).U32((uint)positions.Length).U32((uint)normals.Length)
            .U32((uint)diffuse.Length).U32((uint)specular.Length).U32((uint)texCoords.Length);
        foreach (Vector3 v in positions)
        {
            data.Vector(v);
        }

        foreach (Vector3 v in normals)
        {
            data.Vector(v);
        }

        foreach (Vector4[] array in new[] { diffuse, specular, texCoords })
        {
            foreach (Vector4 v in array)
            {
                data.F32(v.X).F32(v.Y).F32(v.Z).F32(v.W);
            }
        }

        foreach (uint[] face in faces)
        {
            foreach (uint value in face)
            {
                data.U32(value);
            }
        }

        return Block(0xFFFFFF3B, data);
    }

    /// <summary>A progressive mesh continuation; only its name is meaningful.</summary>
    public TestU3D ProgressiveMesh(string name)
    {
        return Block(0xFFFFFF3C, new Data().String(name).U32(0).U32(0).U32(0));
    }

    private static int CountBlocks(byte[] blocks)
    {
        int count = 0;
        for (int position = 0; position + 12 <= blocks.Length; count++)
        {
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(blocks.AsSpan(position + 4));
            position += 12 + (int)((dataSize + 3) & ~3u);
        }

        return count;
    }

    /// <summary>
    /// A motion resource for a file with the <see cref="NoCompression"/> profile. Keys are given as the
    /// motion should play and written as the format does: the first and last in full, the others as
    /// quantised differences from the key before, as that key is reconstructed.
    /// </summary>
    public TestU3D MotionResource(string name, float timeQuant, float rotationQuant, params (string Track, float DisplacementQuant, float ScaleQuant, SceneKeyFrame[] Keys)[] tracks)
    {
        var data = new Data().String(name).U32((uint)tracks.Length).F32(timeQuant).F32(rotationQuant);
        foreach ((string track, float displacementQuant, float scaleQuant, SceneKeyFrame[] keys) in tracks)
        {
            data.String(track).U32((uint)keys.Length).F32(displacementQuant).F32(scaleQuant);

            float time = 0;
            Vector3 location = Vector3.Zero, scale = Vector3.One;
            Quaternion rotation = Quaternion.Identity;
            for (int i = 0; i < keys.Length; i++)
            {
                SceneKeyFrame key = keys[i];
                bool edge = i == 0 || i == keys.Length - 1;
                if (edge)
                {
                    data.F32(key.Time - time).Vector(key.Location - location);
                    data.F32(key.Rotation.W).F32(key.Rotation.X).F32(key.Rotation.Y).F32(key.Rotation.Z);
                    data.Vector(key.Scale - scale);
                    (time, location, rotation, scale) = (key.Time, key.Location, key.Rotation, key.Scale);
                    continue;
                }

                time += Difference(data, key.Time - time, timeQuant);
                location += DifferenceVector(data, key.Location - location, displacementQuant);

                // The difference is the key's rotation relative to the one before; keep its real part >= 0.
                Quaternion difference = Quaternion.Multiply(Quaternion.Conjugate(rotation), key.Rotation);
                if (difference.W < 0)
                {
                    difference = Quaternion.Negate(difference);
                }

                uint b = (uint)MathF.Round(MathF.Abs(difference.X) / rotationQuant), c = (uint)MathF.Round(MathF.Abs(difference.Y) / rotationQuant), d = (uint)MathF.Round(MathF.Abs(difference.Z) / rotationQuant);
                int signs = (difference.X < 0 ? 2 : 0) | (difference.Y < 0 ? 4 : 0) | (difference.Z < 0 ? 8 : 0);
                data.U8((byte)signs).U32(b).U32(c).U32(d);

                float rb = (signs & 2) != 0 ? -b * rotationQuant : b * rotationQuant, rc = (signs & 4) != 0 ? -c * rotationQuant : c * rotationQuant, rd = (signs & 8) != 0 ? -d * rotationQuant : d * rotationQuant;
                float ra = (float)Math.Sqrt(Math.Abs(1.0 - (double)rb * rb - (double)rc * rc - (double)rd * rd));
                rotation = Quaternion.Multiply(rotation, new Quaternion(rb, rc, rd, ra));
                scale += DifferenceVector(data, key.Scale - scale, scaleQuant);
            }
        }

        return Block(0xFFFFFF56, data);
    }

    // Writes a quantised difference as a sign byte and a count; returns the difference as the reader will have it.
    private static float Difference(Data data, float value, float quant)
    {
        uint count = (uint)MathF.Round(MathF.Abs(value) / quant);
        data.U8((byte)(value < 0 ? 1 : 0)).U32(count);
        return value < 0 ? -count * quant : count * quant;
    }

    private static Vector3 DifferenceVector(Data data, Vector3 value, float quant)
    {
        uint x = (uint)MathF.Round(MathF.Abs(value.X) / quant), y = (uint)MathF.Round(MathF.Abs(value.Y) / quant), z = (uint)MathF.Round(MathF.Abs(value.Z) / quant);
        int signs = (value.X < 0 ? 1 : 0) | (value.Y < 0 ? 2 : 0) | (value.Z < 0 ? 4 : 0);
        data.U8((byte)signs).U32(x).U32(y).U32(z);
        return new Vector3((signs & 1) != 0 ? -x * quant : x * quant, (signs & 2) != 0 ? -y * quant : y * quant, (signs & 4) != 0 ? -z * quant : z * quant);
    }

    /// <summary>
    /// A bone weight modifier (ECMA-363 9.7.4) for a file with the <see cref="NoCompression"/> profile: per
    /// position its bones and weights; the last weight is not written, it is what is left of one.
    /// </summary>
    public TestU3D BoneWeights(string chainName, float inverseQuant, params (uint Bone, float Weight)[][] positions)
    {
        var data = new Data().String(chainName).U32(0).U32(1).F32(inverseQuant).U32((uint)positions.Length);
        foreach ((uint Bone, float Weight)[] influences in positions)
        {
            data.U32((uint)influences.Length);
            foreach ((uint bone, _) in influences)
            {
                data.U32(bone);
            }

            for (int i = 0; i < influences.Length - 1; i++)
            {
                data.U32((uint)MathF.Round(influences[i].Weight / inverseQuant));
            }
        }

        return Block(0xFFFFFF44, data);
    }

    /// <summary>An animation modifier (ECMA-363 9.7.3) for the node named <paramref name="chainName"/>.</summary>
    public TestU3D AnimationModifier(string chainName, uint attributes, float timeScale, float blendTime, params (string Motion, uint Attributes, float TimeOffset, float TimeScale)[] motions)
    {
        var data = new Data().String(chainName).U32(0).U32(attributes).F32(timeScale).U32((uint)motions.Length);
        foreach ((string motion, uint motionAttributes, float offset, float scale) in motions)
        {
            data.String(motion).U32(motionAttributes).F32(offset).F32(scale);
        }

        data.F32(blendTime);
        return Block(0xFFFFFF43, data);
    }

    public sealed class Data
    {
        private readonly MemoryStream _stream = new();

        public Data U8(byte value) { _stream.WriteByte(value); return this; }

        public Data I16(short value) => Write(BitConverter.GetBytes(value));

        public Data U16(ushort value) => Write(BitConverter.GetBytes(value));

        public Data U32(uint value) => Write(BitConverter.GetBytes(value));

        public Data U64(ulong value) => Write(BitConverter.GetBytes(value));

        public Data F32(float value) => Write(BitConverter.GetBytes(value));

        public Data String(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            U16((ushort)bytes.Length);
            return Write(bytes);
        }

        public Data Matrix(Matrix4x4 m)
        {
            return F32(m.M11).F32(m.M12).F32(m.M13).F32(m.M14).F32(m.M21).F32(m.M22).F32(m.M23).F32(m.M24)
                .F32(m.M31).F32(m.M32).F32(m.M33).F32(m.M34).F32(m.M41).F32(m.M42).F32(m.M43).F32(m.M44);
        }

        public Data Vector(Vector3 v) => F32(v.X).F32(v.Y).F32(v.Z);

        /// <summary>Zero bytes up to the next 32-bit boundary.</summary>
        public Data Pad()
        {
            while (_stream.Length % 4 != 0)
            {
                _stream.WriteByte(0);
            }

            return this;
        }

        public Data Bytes(ReadOnlySpan<byte> bytes) => Write(bytes.ToArray());

        public byte[] ToArray() => _stream.ToArray();

        private Data Write(byte[] bytes)
        {
            _stream.Write(bytes);
            return this;
        }
    }

    public TestU3D Block(uint type, Data data) => Block(type, data.ToArray());

    public TestU3D Block(uint type, byte[] data)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
        _file.Write(header);
        _file.Write(data);
        while (_file.Length % 4 != 0)
        {
            _file.WriteByte(0);
        }

        return this;
    }

    public TestU3D GroupNode(string name, params (string Parent, Matrix4x4 Matrix)[] parents)
    {
        return Block(0xFFFFFF21, Parents(new Data().String(name), parents));
    }

    public TestU3D ModelNode(string name, string resource, params (string Parent, Matrix4x4 Matrix)[] parents)
    {
        return Block(0xFFFFFF22, Parents(new Data().String(name), parents).String(resource).U32(1));
    }

    private static Data Parents(Data data, (string Parent, Matrix4x4 Matrix)[] parents)
    {
        data.U32((uint)parents.Length);
        foreach ((string parent, Matrix4x4 matrix) in parents)
        {
            data.String(parent).Matrix(matrix);
        }

        return data;
    }

    public byte[] ToArray()
    {
        if (_nested)
        {
            throw new InvalidOperationException("Only the outermost builder makes a file.");
        }

        byte[] file = _file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12 + 8), (uint)file.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12 + 12), (ulong)file.Length);
        return file;
    }
}
