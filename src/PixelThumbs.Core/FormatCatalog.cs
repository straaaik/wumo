namespace PixelThumbs;

public sealed record FormatInfo(string Extension, string Label, string Detail);

public static class FormatCatalog
{
    public const string HandlerCategory = "{E357FCCD-A995-4576-B01F-234630154E96}";
    // Windows can keep the previous COM class factory alive after its DLL path changes.
    private const string IdPrefix = "CDB49CA7-2710-4EEA-94E0-D9F361B839";
    public const string StlId = IdPrefix + "01";
    public const string ObjId = IdPrefix + "02";
    public const string ThreeMfId = IdPrefix + "03";
    public const string PlyId = IdPrefix + "04";
    public const string FusionId = IdPrefix + "05";
    public const string FusionAssemblyId = IdPrefix + "06";
    public const string StepId = IdPrefix + "07";
    public const string StpId = IdPrefix + "08";
    public const string IgesId = IdPrefix + "09";
    public const string IgsId = IdPrefix + "10";
    public const string GlbId = IdPrefix + "11";
    public const string GltfId = IdPrefix + "12";
    public const string FbxId = IdPrefix + "13";
    public const string FreeCadId = IdPrefix + "14";
    public const string GcodeId = IdPrefix + "15";
    public const string SolidPartId = IdPrefix + "16";
    public const string SolidAssemblyId = IdPrefix + "17";

    public static IReadOnlyList<(string Id, string Extension, string Label, string Detail, bool RequiresF3d)> Entries { get; } =
    [
        (StlId, ".stl", "STL", "Модель для 3D-печати · рендер", false),
        (ThreeMfId, ".3mf", "3MF", "Проект печати · превью или рендер", false),
        (ObjId, ".obj", "Wavefront OBJ", "Полигональная модель · рендер", false),
        (PlyId, ".ply", "PLY", "Полигональная модель · рендер", false),
        (FusionId, ".f3d", "Fusion", "Документ Autodesk · сохранённое превью", false),
        (FusionAssemblyId, ".f3z", "Fusion Assembly", "Сборка Autodesk · сохранённое превью", false),
        (StepId, ".step", "STEP", "CAD-модель · рендер F3D", true),
        (StpId, ".stp", "STP", "CAD-модель · рендер F3D", true),
        (IgesId, ".iges", "IGES", "CAD-модель · рендер F3D", true),
        (IgsId, ".igs", "IGS", "CAD-модель · рендер F3D", true),
        (GlbId, ".glb", "glTF Binary", "Сцена · рендер", false),
        (GltfId, ".gltf", "glTF", "Самодостаточная сцена · рендер", false),
        (FbxId, ".fbx", "FBX", "Модель · рендер F3D", true),
        (FreeCadId, ".fcstd", "FreeCAD", "Документ FreeCAD · сохранённое превью", false),
        (GcodeId, ".gcode", "G-code", "Файл принтера · сохранённое превью", false),
        (SolidPartId, ".sldprt", "SolidWorks Part", "Деталь SolidWorks · сохранённое превью", false),
        (SolidAssemblyId, ".sldasm", "SolidWorks Assembly", "Сборка SolidWorks · сохранённое превью", false)
    ];

    public static IReadOnlyList<FormatInfo> All { get; } =
        Entries.Select(entry => new FormatInfo(entry.Extension, entry.Label, entry.Detail)).ToArray();

    public static bool IsSupported(string extension) =>
        Entries.Any(entry => entry.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));

    public static bool RequiresExternalRenderer(string extension) =>
        Entries.Any(entry => entry.RequiresF3d && entry.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
}
