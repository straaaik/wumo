using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private const int MaxGltfJsonBytes = 24 * 1024 * 1024;

    private static Mesh ReadGltf(Stream source, bool binary)
    {
        byte[]? payload = null;
        using JsonDocument document = binary ? ReadGlb(source, out payload) : ReadGltfDocument(source);
        JsonElement root = document.RootElement;
        JsonElement version = Get(Get(root, "asset"), "version");
        if (version.ValueKind != JsonValueKind.String || !version.ValueEquals("2.0"))
            throw new InvalidDataException("The glTF asset version is missing.");

        var scene = new GltfScene(root, payload);
        var output = new Mesh();
        scene.Flatten(output);
        return output;
    }

    private static JsonDocument ReadGltfDocument(Stream source)
    {
        if (source.Length > MaxGltfJsonBytes)
            throw new InvalidDataException("glTF JSON exceeds the size limit.");
        return JsonDocument.Parse(source);
    }

    private static JsonDocument ReadGlb(Stream source, out byte[]? payload)
    {
        payload = null;
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual("glTF"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != source.Length)
            throw new InvalidDataException("The GLB header is invalid.");

        Span<byte> chunk = stackalloc byte[8];
        source.ReadExactly(chunk);
        uint jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        if (!chunk[4..].SequenceEqual("JSON"u8) || jsonLength > MaxGltfJsonBytes || jsonLength > source.Length - source.Position)
            throw new InvalidDataException("The GLB JSON chunk is invalid.");

        byte[] json = new byte[jsonLength];
        source.ReadExactly(json);
        int chunkCount = 0;
        while (source.Position < source.Length)
        {
            source.ReadExactly(chunk);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
            if (length > source.Length - source.Position || ++chunkCount > 32)
                throw new InvalidDataException("A GLB chunk is invalid.");

            if (payload is null && chunk[4..].SequenceEqual("BIN\0"u8))
            {
                payload = new byte[length];
                source.ReadExactly(payload);
            }
            else
            {
                source.Seek(length, SeekOrigin.Current);
            }
        }

        return JsonDocument.Parse(json.AsMemory());
    }

    private sealed class GltfScene
    {
        private readonly JsonElement root;
        private readonly JsonElement nodes;
        private readonly Dictionary<int, byte[]> buffers = [];
        private readonly byte[]? payload;
        private long bufferBytes;
        private int nodeBudget = 250_000;

        internal GltfScene(JsonElement root, byte[]? payload)
        {
            this.root = root;
            this.payload = payload;
            nodes = Get(root, "nodes");
        }

        internal void Flatten(Mesh output)
        {
            int nodeCount = Count(nodes);
            if (nodeCount > nodeBudget)
                throw new InvalidDataException("glTF exceeds the node count limit.");

            JsonElement scenes = Get(root, "scenes");
            if (Count(scenes) > 0)
            {
                JsonElement scene = At(scenes, Number(Get(root, "scene"), 0));
                var active = new HashSet<int>();
                foreach (JsonElement node in Elements(Get(scene, "nodes")))
                    FlattenNode(Number(node, -1), Matrix4x4.Identity, output, active);
            }
            else if (nodeCount > 0)
            {
                var childNodes = new HashSet<int>();
                foreach (JsonElement node in Elements(nodes))
                {
                    foreach (JsonElement child in Elements(Get(node, "children")))
                        childNodes.Add(Number(child, -1));
                }

                var active = new HashSet<int>();
                for (int i = 0; i < nodeCount; i++)
                {
                    if (!childNodes.Contains(i))
                        FlattenNode(i, Matrix4x4.Identity, output, active);
                }
            }
            else
            {
                foreach (JsonElement mesh in Elements(Get(root, "meshes")))
                    FlattenMesh(mesh, Matrix4x4.Identity, output);
            }
        }

        private void FlattenNode(int index, Matrix4x4 parent, Mesh output, HashSet<int> active)
        {
            if (--nodeBudget < 0 || active.Count >= 64)
                throw new InvalidDataException("glTF exceeds the scene hierarchy limit.");

            JsonElement node = At(nodes, index);
            if (node.ValueKind != JsonValueKind.Object || !active.Add(index))
                return;

            Matrix4x4 world = GltfTransform(node) * parent;
            FlattenMesh(At(Get(root, "meshes"), Number(Get(node, "mesh"), -1)), world, output);
            foreach (JsonElement child in Elements(Get(node, "children")))
                FlattenNode(Number(child, -1), world, output, active);
            active.Remove(index);
        }

        private void FlattenMesh(JsonElement mesh, Matrix4x4 world, Mesh output)
        {
            foreach (JsonElement primitive in Elements(Get(mesh, "primitives")))
            {
                int mode = Number(Get(primitive, "mode"), 4);
                JsonElement attributes = Get(primitive, "attributes");
                if (mode is not (4 or 5 or 6)
                    || Access(Number(Get(attributes, "POSITION"), -1), "VEC3") is not GltfAccessor positions
                    || positions.Type != 5126)
                    continue;

                JsonElement indexAttribute = Get(primitive, "indices");
                GltfAccessor? indices = Access(Number(indexAttribute, -1), "SCALAR");
                if (indexAttribute.ValueKind != JsonValueKind.Undefined
                    && (indices is null || indices.Value.Type is not (5121 or 5123 or 5125)))
                    continue;

                int colorIndex = Number(Get(attributes, "COLOR_0"), -1);
                GltfAccessor? colors = Access(colorIndex, "VEC3") ?? Access(colorIndex, "VEC4");
                JsonElement material = At(Get(root, "materials"), Number(Get(primitive, "material"), -1));
                JsonElement baseColor = Get(Get(material, "pbrMetallicRoughness"), "baseColorFactor");
                Vector3 factor = Triplet(baseColor, Vector3.One);
                int count = indices?.Count ?? positions.Count;

                int start = mode == 4 ? 0 : 2;
                int step = mode == 4 ? 3 : 1;
                for (int i = start; i + (mode == 4 ? 2 : 0) < count; i += step)
                {
                    int a = mode == 4 ? i : mode == 5 ? i - 2 : 0;
                    int b = mode == 4 ? i + 1 : i - 1;
                    int c = mode == 4 ? i + 2 : i;
                    a = ReadIndex(indices, a);
                    b = ReadIndex(indices, b);
                    c = ReadIndex(indices, c);
                    if ((uint)a >= (uint)positions.Count || (uint)b >= (uint)positions.Count || (uint)c >= (uint)positions.Count)
                        continue;

                    int rgb = DefaultColor;
                    if (colors is GltfAccessor vertexColors && a < vertexColors.Count && b < vertexColors.Count && c < vertexColors.Count)
                    {
                        Vector3 average = (ReadGltfColor(vertexColors, a) + ReadGltfColor(vertexColors, b)
                            + ReadGltfColor(vertexColors, c)) / 3;
                        rgb = MaterialColor(average * factor);
                    }
                    else if (baseColor.ValueKind == JsonValueKind.Array)
                    {
                        rgb = MaterialColor(factor);
                    }

                    output.Add(Vector3.Transform(ReadVector(positions.Element(a)), world),
                        Vector3.Transform(ReadVector(positions.Element(b)), world),
                        Vector3.Transform(ReadVector(positions.Element(c)), world), rgb);
                }
            }
        }

        private GltfAccessor? Access(int index, string kind)
        {
            JsonElement accessor = At(Get(root, "accessors"), index);
            JsonElement typeName = Get(accessor, "type");
            if (accessor.ValueKind != JsonValueKind.Object || Get(accessor, "sparse").ValueKind != JsonValueKind.Undefined
                || typeName.ValueKind != JsonValueKind.String || !typeName.ValueEquals(kind))
                return null;

            JsonElement view = At(Get(root, "bufferViews"), Number(Get(accessor, "bufferView"), -1));
            if (view.ValueKind != JsonValueKind.Object)
                return null;

            int bufferIndex = Number(Get(view, "buffer"), -1);
            byte[]? data = LoadBuffer(bufferIndex);
            int bufferLength = Number(Get(At(Get(root, "buffers"), bufferIndex), "byteLength"), -1);
            if (data is null || bufferLength < 0 || data.Length < bufferLength)
                return null;

            int type = Number(Get(accessor, "componentType"), 0);
            int scalarBytes = type switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4, _ => 0 };
            if (scalarBytes == 0)
                return null;

            int width = kind switch { "VEC3" => 3, "VEC4" => 4, _ => 1 };
            int elementBytes = width * scalarBytes;
            int viewOffset = Number(Get(view, "byteOffset"), 0);
            int viewLength = Number(Get(view, "byteLength"), -1);
            int accessorOffset = Number(Get(accessor, "byteOffset"), 0);
            int stride = Number(Get(view, "byteStride"), elementBytes);
            int count = Number(Get(accessor, "count"), 0);
            if (viewOffset < 0 || viewLength < 0 || accessorOffset < 0 || stride < elementBytes || count < 1
                || count > MaxVertices * 3 || (long)viewOffset + viewLength > bufferLength
                || (long)accessorOffset + (long)(count - 1) * stride + elementBytes > viewLength)
                return null;

            return new GltfAccessor(data, viewOffset + accessorOffset, stride, count, type, width, scalarBytes);
        }

        private byte[]? LoadBuffer(int index)
        {
            if (buffers.TryGetValue(index, out byte[]? buffer))
                return buffer;

            JsonElement definition = At(Get(root, "buffers"), index);
            if (definition.ValueKind != JsonValueKind.Object)
                return null;

            JsonElement uri = Get(definition, "uri");
            if (uri.ValueKind == JsonValueKind.Undefined && index == 0)
                return payload;
            if (uri.ValueKind != JsonValueKind.String || buffers.Count >= 32)
                return null;

            string? value = uri.GetString();
            int comma = value?.IndexOf(',') ?? -1;
            if (value is null || comma < 0 || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || !value.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                return null;

            ReadOnlySpan<char> encoded = value.AsSpan(comma + 1);
            int maximum = (encoded.Length / 4 + 1) * 3;
            if (bufferBytes + maximum > MaxInputBytes)
                throw new InvalidDataException("glTF exceeds the decoded buffer size limit.");

            buffer = new byte[maximum];
            if (!Convert.TryFromBase64Chars(encoded, buffer, out int bytes))
                return null;
            Array.Resize(ref buffer, bytes);
            bufferBytes += bytes;
            buffers.Add(index, buffer);
            return buffer;
        }
    }

    private static Matrix4x4 GltfTransform(JsonElement node)
    {
        JsonElement matrix = Get(node, "matrix");
        if (Count(matrix) == 16)
        {
            Span<float> numbers = stackalloc float[16];
            for (int i = 0; i < numbers.Length; i++)
                numbers[i] = Real(matrix[i], 0);
            return new Matrix4x4(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6],
                numbers[7], numbers[8], numbers[9], numbers[10], numbers[11], numbers[12], numbers[13], numbers[14], numbers[15]);
        }

        Vector3 translation = Triplet(Get(node, "translation"), Vector3.Zero);
        Vector3 scale = Triplet(Get(node, "scale"), Vector3.One);
        JsonElement rotation = Get(node, "rotation");
        Quaternion quaternion = Count(rotation) == 4
            ? new Quaternion(Real(rotation[0], 0), Real(rotation[1], 0), Real(rotation[2], 0), Real(rotation[3], 1))
            : Quaternion.Identity;
        float magnitude = quaternion.LengthSquared();
        quaternion = magnitude > 1e-10f && float.IsFinite(magnitude) ? Quaternion.Normalize(quaternion) : Quaternion.Identity;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(quaternion) * Matrix4x4.CreateTranslation(translation);
    }

    private static int ReadIndex(GltfAccessor? indices, int position)
    {
        if (indices is not GltfAccessor accessor)
            return position;

        ReadOnlySpan<byte> bytes = accessor.Element(position);
        return accessor.Type switch
        {
            5121 => bytes[0],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            _ => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes))
        };
    }

    private static Vector3 ReadGltfColor(GltfAccessor accessor, int index)
    {
        ReadOnlySpan<byte> bytes = accessor.Element(index);
        return accessor.Type switch
        {
            5121 => new Vector3(bytes[0], bytes[1], bytes[2]) / 255,
            5123 => new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..])) / 65535,
            5126 => ReadVector(bytes),
            _ => Vector3.One
        };
    }

    private static int MaterialColor(Vector3 linear) => Rgb(Srgb(linear.X), Srgb(linear.Y), Srgb(linear.Z));

    private static float Srgb(float component)
    {
        float linear = Math.Clamp(FiniteOr(component, 0), 0, 1);
        return 255 * (linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f);
    }

    private static JsonElement Get(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out JsonElement property) ? property : default;

    private static int Count(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private static JsonElement At(JsonElement value, int index) => (uint)index < (uint)Count(value) ? value[index] : default;

    private static JsonElement.ArrayEnumerator Elements(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : default;

    private static int Number(JsonElement value, int fallback) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;

    private static float Real(JsonElement value, float fallback) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number) ? number : fallback;

    private static Vector3 Triplet(JsonElement array, Vector3 fallback) => Count(array) >= 3
        ? new Vector3(Real(array[0], fallback.X), Real(array[1], fallback.Y), Real(array[2], fallback.Z)) : fallback;

    private readonly record struct GltfAccessor(byte[] Data, int Offset, int Stride, int Count, int Type, int Width, int ScalarBytes)
    {
        internal ReadOnlySpan<byte> Element(int index) => Data.AsSpan(Offset + Stride * index, Width * ScalarBytes);
    }
}
