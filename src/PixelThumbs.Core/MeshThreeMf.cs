using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private const int MaxModelXmlBytes = 64 * 1024 * 1024;

    private static Mesh Read3mf(Stream source)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 20_000)
            throw new InvalidDataException("3MF exceeds the archive entry limit.");

        string root = Find3mfRoot(archive) ?? "3D/3dmodel.model";
        var package = new ModelPackage(archive);
        var mesh = new Mesh();
        ModelPart? part = package.Load(root);
        if (part is null)
            return mesh;

        var active = new HashSet<(string Path, int Id)>();
        int remainingVisits = 250_000;
        foreach (ModelReference item in part.Build)
        {
            Flatten3mf(package, part, item, Matrix4x4.CreateScale(part.MillimetersPerUnit), mesh, active,
                ref remainingVisits);
        }

        return mesh;
    }

    private static string? Find3mfRoot(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("_rels/.rels");
        if (entry is null || entry.Length > 512 * 1024)
            return null;

        using Stream stream = entry.Open();
        using XmlReader reader = Read3mfXml(stream, 512 * 1024);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                && reader.GetAttribute("Type")?.EndsWith("/3dmodel", StringComparison.OrdinalIgnoreCase) == true
                && reader.GetAttribute("TargetMode") != "External")
            {
                return ResolvePartPath("", reader.GetAttribute("Target"));
            }
        }

        return null;
    }

    private static string? ResolvePartPath(string from, string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return from;
        string path = Uri.UnescapeDataString(uri);
        if (path.Length > 2048 || path.IndexOfAny([':', '\\', '?', '#', '\0']) >= 0)
            return null;

        if (path[0] != '/' && from.LastIndexOf('/') is int slash && slash >= 0)
            path = from[..(slash + 1)] + path;

        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    private static XmlReader Read3mfXml(Stream stream, int maxCharacters) => XmlReader.Create(stream,
        new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true
        });

    private static ModelPart? Read3mfPart(ZipArchiveEntry entry, string path)
    {
        using Stream stream = entry.Open();
        using XmlReader reader = Read3mfXml(stream, MaxModelXmlBytes);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "model")
            return null;

        var part = new ModelPart(path, Millimeters(reader.GetAttribute("unit")));
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.Depth == 2 && reader.LocalName == "object" && Read3mfInt(reader, "id", 0) is int id && id > 0)
                part.Objects.TryAdd(id, Read3mfObject(reader));
            else if (reader.Depth == 2 && reader.LocalName == "item" && Read3mfReference(reader) is ModelReference item)
                part.Build.Add(item);
            else if (reader.Depth == 2 && reader.LocalName is "basematerials" or "colorgroup"
                && Read3mfInt(reader, "id", 0) is int group && group > 0)
                part.Colors.TryAdd(group, Read3mfColors(reader));
        }

        return part;
    }

    private static ModelObject Read3mfObject(XmlReader parent)
    {
        var model = new ModelObject(Read3mfInt(parent, "pid", -1), Read3mfInt(parent, "pindex", -1));
        using XmlReader reader = parent.ReadSubtree();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName)
            {
                case "vertex" when reader.Depth == 3:
                    if (model.Vertices.Count >= MaxVertices)
                        throw new InvalidDataException("3MF exceeds the vertex count limit.");
                    bool validX = TryFloat(reader.GetAttribute("x"), out float x);
                    bool validY = TryFloat(reader.GetAttribute("y"), out float y);
                    bool validZ = TryFloat(reader.GetAttribute("z"), out float z);
                    model.Vertices.Add(validX && validY && validZ ? new Vector3(x, y, z) : new Vector3(float.NaN));
                    break;
                case "triangle" when reader.Depth == 3:
                    if (model.Faces.Count >= MaxTriangles)
                        throw new InvalidDataException("3MF exceeds the triangle count limit.");
                    int p1 = Read3mfInt(reader, "p1", model.PropertyIndex);
                    model.Faces.Add(new ModelFace(Read3mfInt(reader, "v1", -1), Read3mfInt(reader, "v2", -1),
                        Read3mfInt(reader, "v3", -1), Read3mfInt(reader, "pid", model.PropertyId), p1,
                        Read3mfInt(reader, "p2", p1), Read3mfInt(reader, "p3", p1)));
                    break;
                case "component" when reader.Depth == 2:
                    if (Read3mfReference(reader) is ModelReference component)
                        model.Components.Add(component);
                    break;
            }
        }

        return model;
    }

    private static int[] Read3mfColors(XmlReader parent)
    {
        using XmlReader reader = parent.ReadSubtree();
        var colors = new List<int>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.LocalName is not ("base" or "color"))
                continue;

            if (colors.Count >= 65_536)
                throw new InvalidDataException("3MF exceeds the palette size limit.");
            colors.Add(Parse3mfColor(reader.GetAttribute("displaycolor") ?? reader.GetAttribute("color")));
        }

        return colors.ToArray();
    }

    private static int Parse3mfColor(string? value)
    {
        ReadOnlySpan<char> color = value.AsSpan().Trim();
        if (color.Length is not (7 or 9) || color[0] != '#'
            || !int.TryParse(color[1..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return DefaultColor;

        return rgb;
    }

    private static ModelReference? Read3mfReference(XmlReader reader)
    {
        int id = Read3mfInt(reader, "objectid", 0);
        if (id <= 0 || !Try3mfMatrix(reader.GetAttribute("transform"), out Matrix4x4 matrix))
            return null;

        string? path = reader.GetAttribute("path", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06");
        return new ModelReference(id, path, matrix);
    }

    private static bool Try3mfMatrix(string? attribute, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (string.IsNullOrWhiteSpace(attribute))
            return true;

        ReadOnlySpan<char> line = attribute.AsSpan();
        Span<float> values = stackalloc float[12];
        for (int i = 0; i < values.Length; i++)
        {
            if (!TryFloat(TakeToken(ref line), out values[i]))
                return false;
        }

        if (!TakeToken(ref line).IsEmpty)
            return false;

        matrix = new Matrix4x4(values[0], values[1], values[2], 0, values[3], values[4], values[5], 0,
            values[6], values[7], values[8], 0, values[9], values[10], values[11], 1);
        return true;
    }

    private static void Flatten3mf(ModelPackage package, ModelPart parent, ModelReference reference, Matrix4x4 parentWorld,
        Mesh output, HashSet<(string Path, int Id)> active, ref int remainingVisits)
    {
        if (--remainingVisits < 0 || active.Count >= 48)
            throw new InvalidDataException("3MF exceeds the component hierarchy limit.");

        string? path = ResolvePartPath(parent.Path, reference.Path);
        if (path is null || package.Load(path) is not ModelPart part
            || !part.Objects.TryGetValue(reference.Id, out ModelObject? item) || !active.Add((part.Path, reference.Id)))
            return;

        Matrix4x4 world = Matrix4x4.CreateScale(part.MillimetersPerUnit / parent.MillimetersPerUnit)
            * reference.Transform * parentWorld;

        if (item.Faces.Count > 0)
        {
            foreach (ModelFace face in item.Faces)
            {
                if ((uint)face.A >= (uint)item.Vertices.Count || (uint)face.B >= (uint)item.Vertices.Count
                    || (uint)face.C >= (uint)item.Vertices.Count)
                    continue;

                int color = DefaultColor;
                if (part.Colors.TryGetValue(face.PropertyId, out int[]? palette))
                    color = AverageColors(FindColor(palette, face.P1), FindColor(palette, face.P2), FindColor(palette, face.P3));

                output.Add(ZUp(Vector3.Transform(item.Vertices[face.A], world)),
                    ZUp(Vector3.Transform(item.Vertices[face.B], world)),
                    ZUp(Vector3.Transform(item.Vertices[face.C], world)), color);
            }
        }

        foreach (ModelReference component in item.Components)
            Flatten3mf(package, part, component, world, output, active, ref remainingVisits);

        active.Remove((part.Path, reference.Id));
    }

    private static int FindColor(int[] palette, int index) => (uint)index < (uint)palette.Length ? palette[index] : DefaultColor;

    private static int Read3mfInt(XmlReader reader, string name, int fallback) =>
        TryInt(reader.GetAttribute(name), out int value) ? value : fallback;

    private static float Millimeters(string? unit) => unit switch
    {
        "micron" => 0.001f,
        "centimeter" => 10,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000,
        _ => 1
    };

    private sealed class ModelPackage(ZipArchive archive)
    {
        private readonly Dictionary<string, ModelPart?> parts = new(StringComparer.OrdinalIgnoreCase);
        private long xmlBytes;

        internal ModelPart? Load(string path)
        {
            if (parts.TryGetValue(path, out ModelPart? model))
                return model;
            if (parts.Count >= 64)
                throw new InvalidDataException("3MF exceeds the model part count limit.");

            ZipArchiveEntry? entry = archive.GetEntry(path);
            if (entry is not null)
            {
                if (entry.Length < 0 || entry.Length > MaxModelXmlBytes - xmlBytes)
                    throw new InvalidDataException("3MF exceeds the unpacked model size limit.");
                xmlBytes += entry.Length;
                model = Read3mfPart(entry, path);
            }

            parts.Add(path, model);
            return model;
        }
    }

    private sealed record ModelPart(string Path, float MillimetersPerUnit)
    {
        internal Dictionary<int, ModelObject> Objects { get; } = [];
        internal Dictionary<int, int[]> Colors { get; } = [];
        internal List<ModelReference> Build { get; } = [];
    }

    private sealed record ModelObject(int PropertyId, int PropertyIndex)
    {
        internal List<Vector3> Vertices { get; } = [];
        internal List<ModelFace> Faces { get; } = [];
        internal List<ModelReference> Components { get; } = [];
    }

    private readonly record struct ModelReference(int Id, string? Path, Matrix4x4 Transform);

    private readonly record struct ModelFace(int A, int B, int C, int PropertyId, int P1, int P2, int P3);
}
