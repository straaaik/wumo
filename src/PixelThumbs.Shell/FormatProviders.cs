using System.Runtime.InteropServices;

namespace PixelThumbs;

[ComVisible(true), Guid(FormatCatalog.StlId), ClassInterface(ClassInterfaceType.None)]
public sealed class StlThumbnailProvider() : ThumbnailProvider(".stl");

[ComVisible(true), Guid(FormatCatalog.ObjId), ClassInterface(ClassInterfaceType.None)]
public sealed class ObjThumbnailProvider() : ThumbnailProvider(".obj");

[ComVisible(true), Guid(FormatCatalog.ThreeMfId), ClassInterface(ClassInterfaceType.None)]
public sealed class ThreeMfThumbnailProvider() : ThumbnailProvider(".3mf");

[ComVisible(true), Guid(FormatCatalog.PlyId), ClassInterface(ClassInterfaceType.None)]
public sealed class PlyThumbnailProvider() : ThumbnailProvider(".ply");

[ComVisible(true), Guid(FormatCatalog.FusionId), ClassInterface(ClassInterfaceType.None)]
public sealed class FusionThumbnailProvider() : ThumbnailProvider(".f3d");

[ComVisible(true), Guid(FormatCatalog.FusionAssemblyId), ClassInterface(ClassInterfaceType.None)]
public sealed class FusionAssemblyThumbnailProvider() : ThumbnailProvider(".f3z");

[ComVisible(true), Guid(FormatCatalog.StepId), ClassInterface(ClassInterfaceType.None)]
public sealed class StepThumbnailProvider() : ThumbnailProvider(".step");

[ComVisible(true), Guid(FormatCatalog.StpId), ClassInterface(ClassInterfaceType.None)]
public sealed class StpThumbnailProvider() : ThumbnailProvider(".stp");

[ComVisible(true), Guid(FormatCatalog.IgesId), ClassInterface(ClassInterfaceType.None)]
public sealed class IgesThumbnailProvider() : ThumbnailProvider(".iges");

[ComVisible(true), Guid(FormatCatalog.IgsId), ClassInterface(ClassInterfaceType.None)]
public sealed class IgsThumbnailProvider() : ThumbnailProvider(".igs");

[ComVisible(true), Guid(FormatCatalog.GlbId), ClassInterface(ClassInterfaceType.None)]
public sealed class GlbThumbnailProvider() : ThumbnailProvider(".glb");

[ComVisible(true), Guid(FormatCatalog.GltfId), ClassInterface(ClassInterfaceType.None)]
public sealed class GltfThumbnailProvider() : ThumbnailProvider(".gltf");

[ComVisible(true), Guid(FormatCatalog.FbxId), ClassInterface(ClassInterfaceType.None)]
public sealed class FbxThumbnailProvider() : ThumbnailProvider(".fbx");

[ComVisible(true), Guid(FormatCatalog.FreeCadId), ClassInterface(ClassInterfaceType.None)]
public sealed class FreeCadThumbnailProvider() : ThumbnailProvider(".fcstd");

[ComVisible(true), Guid(FormatCatalog.GcodeId), ClassInterface(ClassInterfaceType.None)]
public sealed class GcodeThumbnailProvider() : ThumbnailProvider(".gcode");

[ComVisible(true), Guid(FormatCatalog.SolidPartId), ClassInterface(ClassInterfaceType.None)]
public sealed class SolidPartThumbnailProvider() : ThumbnailProvider(".sldprt");

[ComVisible(true), Guid(FormatCatalog.SolidAssemblyId), ClassInterface(ClassInterfaceType.None)]
public sealed class SolidAssemblyThumbnailProvider() : ThumbnailProvider(".sldasm");


