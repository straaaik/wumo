using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private const int MaxFaceVertices = 4096;

    private static Mesh ReadStl(Stream source)
    {
        var mesh = new Mesh();
        using var reader = new StreamReader(source, leaveOpen: true);
        Span<Vector3> triangle = stackalloc Vector3[3];
        int nextVertex = 0;
        while (reader.ReadLine() is string text)
        {
            ReadOnlySpan<char> line = text.AsSpan();
            ReadOnlySpan<char> kind = TakeToken(ref line);
            if (kind.Equals("facet", StringComparison.OrdinalIgnoreCase))
                nextVertex = 0;
            if (!kind.Equals("vertex", StringComparison.OrdinalIgnoreCase) || !TryPoint(ref line, out Vector3 point))
                continue;

            triangle[nextVertex++] = ZUp(point);
            if (nextVertex == 3)
            {
                mesh.Add(triangle[0], triangle[1], triangle[2]);
                nextVertex = 0;
            }
        }

        return mesh;
    }

    private static Mesh ReadObj(Stream source)
    {
        var mesh = new Mesh();
        var vertices = new List<Vertex>();
        using var reader = new StreamReader(source, leaveOpen: true);
        Span<float> extra = stackalloc float[4];
        var face = new int[MaxFaceVertices];
        while (reader.ReadLine() is string text)
        {
            ReadOnlySpan<char> line = text.AsSpan();
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];

            ReadOnlySpan<char> kind = TakeToken(ref line);
            if (kind.SequenceEqual("v"))
            {
                if (vertices.Count >= MaxVertices)
                    throw new InvalidDataException("OBJ exceeds the vertex count limit.");
                if (!TryPoint(ref line, out Vector3 point))
                {
                    vertices.Add(new Vertex(new Vector3(float.NaN)));
                    continue;
                }

                int count = 0;
                while (count < extra.Length && TryFloat(TakeToken(ref line), out extra[count]))
                    count++;
                int color = count >= 3 ? ObjColor(extra[(count - 3)..count]) : DefaultColor;
                vertices.Add(new Vertex(point, color));
            }
            else if (kind.SequenceEqual("f"))
            {
                int count = ReadObjFace(line, vertices.Count, face);
                AddPolygon(mesh, vertices, face.AsSpan(0, count));
            }
        }

        return mesh;
    }

    private static int ReadObjFace(ReadOnlySpan<char> line, int vertexCount, int[] indices)
    {
        int count = 0;
        for (ReadOnlySpan<char> token = TakeToken(ref line); !token.IsEmpty; token = TakeToken(ref line))
        {
            int slash = token.IndexOf('/');
            if (slash >= 0)
                token = token[..slash];

            if (!TryInt(token, out int index) || count == indices.Length || index == 0)
                return 0;

            index = index > 0 ? index - 1 : vertexCount + index;
            if ((uint)index >= (uint)vertexCount)
                return 0;

            indices[count++] = index;
        }

        return count;
    }

    private static int ObjColor(ReadOnlySpan<float> channels)
    {
        float scale = Math.Max(channels[0], Math.Max(channels[1], channels[2])) <= 1 ? 255 : 1;
        return Rgb(channels[0] * scale, channels[1] * scale, channels[2] * scale);
    }

    private static bool TryPoint(ref ReadOnlySpan<char> line, out Vector3 point)
    {
        if (TryFloat(TakeToken(ref line), out float x) && TryFloat(TakeToken(ref line), out float y)
            && TryFloat(TakeToken(ref line), out float z))
        {
            point = new Vector3(x, y, z);
            return true;
        }

        point = default;
        return false;
    }

    private static Mesh ReadPly(Stream source)
    {
        if (ReadPlyHeaderLine(source) != "ply")
            throw new InvalidDataException("The PLY header is missing.");

        var elements = new List<PlyElement>();
        string? encoding = null;
        for (int i = 0; i < 1024; i++)
        {
            string header = ReadPlyHeaderLine(source);
            ReadOnlySpan<char> line = header.AsSpan();
            ReadOnlySpan<char> kind = TakeToken(ref line);
            if (kind.SequenceEqual("end_header"))
                return ReadPlyData(source, encoding, elements);
            if (kind.SequenceEqual("format"))
                encoding = TakeToken(ref line).ToString();
            else if (kind.SequenceEqual("element"))
            {
                string name = TakeToken(ref line).ToString();
                if (!TryInt(TakeToken(ref line), out int count) || count < 0 || count > MaxVertices)
                    throw new InvalidDataException("A PLY element has an invalid count.");
                elements.Add(new PlyElement(name, count));
            }
            else if (kind.SequenceEqual("property") && elements.Count > 0)
            {
                string type = TakeToken(ref line).ToString();
                PlyScalar lengthType = default;
                if (type == "list")
                {
                    lengthType = PlyType(TakeToken(ref line).ToString());
                    type = TakeToken(ref line).ToString();
                }

                elements[^1].Properties.Add(new PlyProperty(TakeToken(ref line).ToString(), PlyType(type), lengthType));
            }
        }

        throw new InvalidDataException("The PLY header exceeds the line count limit.");
    }

    private static string ReadPlyHeaderLine(Stream source)
    {
        Span<byte> bytes = stackalloc byte[4096];
        for (int i = 0; i < bytes.Length; i++)
        {
            int value = source.ReadByte();
            if (value < 0)
                break;
            if (value == '\n')
                return Encoding.ASCII.GetString(bytes[..i]).TrimEnd('\r');
            bytes[i] = (byte)value;
        }

        throw new InvalidDataException("A PLY header line is invalid or too long.");
    }

    private static Mesh ReadPlyData(Stream source, string? encoding, List<PlyElement> elements)
    {
        if (encoding is not ("ascii" or "binary_little_endian" or "binary_big_endian"))
            throw new InvalidDataException("The PLY encoding is invalid.");

        bool bigEndian = encoding == "binary_big_endian";
        using var textReader = encoding == "ascii" ? new StreamReader(source, leaveOpen: true) : null;
        var mesh = new Mesh();
        var vertices = new List<Vertex>();
        var face = new int[MaxFaceVertices];

        foreach (PlyElement element in elements)
        {
            if (element.Name == "vertex")
            {
                if ((long)vertices.Count + element.Count > MaxVertices)
                    throw new InvalidDataException("PLY exceeds the vertex count limit.");
                vertices.EnsureCapacity(vertices.Count + element.Count);
            }

            for (int row = 0; row < element.Count; row++)
            {
                ReadOnlySpan<char> line = (textReader?.ReadLine()).AsSpan();
                Vector3 point = default;
                Vector3 rgb = new(-1);
                int indexedVertices = 0;
                foreach (PlyProperty property in element.Properties)
                {
                    if (property.LengthType != PlyScalar.None)
                    {
                        double declared = ReadPlyNumber(source, textReader is not null, bigEndian, property.LengthType, ref line);
                        if (declared < 0 || declared > MaxFaceVertices || declared != Math.Truncate(declared))
                            throw new InvalidDataException("A PLY property list exceeds the size limit.");

                        int count = (int)declared;
                        bool indices = element.Name == "face" && property.Name is "vertex_indices" or "vertex_index";
                        for (int index = 0; index < count; index++)
                        {
                            double value = ReadPlyNumber(source, textReader is not null, bigEndian, property.ValueType, ref line);
                            if (indices)
                                face[index] = value >= 0 && value < vertices.Count && value == Math.Truncate(value) ? (int)value : -1;
                        }

                        if (indices)
                            indexedVertices = count;
                        continue;
                    }

                    float data = (float)ReadPlyNumber(source, textReader is not null, bigEndian, property.ValueType, ref line);
                    if (element.Name != "vertex")
                        continue;

                    float color = property.ValueType is PlyScalar.Float or PlyScalar.Double && data <= 1 ? data * 255 : data;
                    switch (property.Name)
                    {
                        case "x": point.X = data; break;
                        case "y": point.Y = data; break;
                        case "z": point.Z = data; break;
                        case "red": rgb.X = color; break;
                        case "green": rgb.Y = color; break;
                        case "blue": rgb.Z = color; break;
                    }
                }

                if (element.Name == "vertex")
                {
                    int color = rgb.X >= 0 && rgb.Y >= 0 && rgb.Z >= 0 ? Rgb(rgb.X, rgb.Y, rgb.Z) : DefaultColor;
                    vertices.Add(new Vertex(point, color));
                }
                else if (indexedVertices >= 3 && Array.IndexOf(face, -1, 0, indexedVertices) < 0)
                {
                    AddPolygon(mesh, vertices, face.AsSpan(0, indexedVertices));
                }
            }
        }

        return mesh;
    }

    private static double ReadPlyNumber(Stream source, bool ascii, bool bigEndian, PlyScalar type, ref ReadOnlySpan<char> line)
    {
        if (ascii)
        {
            if (double.TryParse(TakeToken(ref line), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number))
                return number;
            throw new InvalidDataException("A PLY property is invalid.");
        }

        int size = type switch
        {
            PlyScalar.Byte or PlyScalar.SByte => 1,
            PlyScalar.Short or PlyScalar.UShort => 2,
            PlyScalar.Int or PlyScalar.UInt or PlyScalar.Float => 4,
            PlyScalar.Double => 8,
            _ => throw new InvalidDataException("A PLY property type is invalid.")
        };
        Span<byte> bytes = stackalloc byte[8];
        source.ReadExactly(bytes[..size]);
        if (bigEndian)
            bytes[..size].Reverse();

        return type switch
        {
            PlyScalar.Byte => bytes[0],
            PlyScalar.SByte => unchecked((sbyte)bytes[0]),
            PlyScalar.Short => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            PlyScalar.UShort => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            PlyScalar.Int => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            PlyScalar.UInt => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            PlyScalar.Float => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes)
        };
    }

    private static PlyScalar PlyType(string type) => type switch
    {
        "char" or "int8" => PlyScalar.SByte,
        "uchar" or "uint8" => PlyScalar.Byte,
        "short" or "int16" => PlyScalar.Short,
        "ushort" or "uint16" => PlyScalar.UShort,
        "int" or "int32" => PlyScalar.Int,
        "uint" or "uint32" => PlyScalar.UInt,
        "float" or "float32" => PlyScalar.Float,
        "double" or "float64" => PlyScalar.Double,
        _ => throw new InvalidDataException("A PLY property type is invalid.")
    };

    private enum PlyScalar { None, SByte, Byte, Short, UShort, Int, UInt, Float, Double }

    private sealed record PlyElement(string Name, int Count)
    {
        internal List<PlyProperty> Properties { get; } = [];
    }

    private readonly record struct PlyProperty(string Name, PlyScalar ValueType, PlyScalar LengthType);
}
