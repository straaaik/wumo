using System.Numerics;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private static void AddPolygon(Mesh mesh, List<Vertex> vertices, ReadOnlySpan<int> face)
    {
        if (face.Length < 3)
            return;
        if (face.Length == 3)
        {
            mesh.Add(vertices[face[0]], vertices[face[1]], vertices[face[2]]);
            return;
        }

        Span<int> indices = face.Length <= 64 ? stackalloc int[face.Length] : new int[face.Length];
        int count = 0;
        foreach (int index in face)
        {
            Vector3 point = vertices[index].Position;
            if (!IsValid(point))
                return;
            if (count == 0 || point != vertices[indices[count - 1]].Position)
                indices[count++] = index;
        }
        if (count > 1 && vertices[indices[0]].Position == vertices[indices[count - 1]].Position)
            count--;
        if (count < 3)
            return;

        int axis = ProjectionAxis(vertices, indices[..count], out int winding);
        if (winding == 0)
            return;

        Span<PolygonPoint> points = count <= 64 ? stackalloc PolygonPoint[count] : new PolygonPoint[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 point = vertices[indices[i]].Position;
            points[i] = axis switch
            {
                0 => new(point.Y, point.Z),
                1 => new(point.Z, point.X),
                _ => new(point.X, point.Y)
            };
        }

        bool convex = true;
        for (int i = 0; i < count && convex; i++)
            convex = winding * Turn(points[i], points[(i + 1) % count], points[(i + 2) % count]) >= 0;
        if (convex)
        {
            for (int i = 2; i < count; i++)
                mesh.Add(vertices[indices[0]], vertices[indices[i - 1]], vertices[indices[i]]);
            return;
        }

        Span<PolygonLinks> links = count <= 64 ? stackalloc PolygonLinks[count] : new PolygonLinks[count];
        for (int i = 0; i < count; i++)
            links[i] = new((i + count - 1) % count, (i + 1) % count);

        Span<int> triangles = count <= 64 ? stackalloc int[(count - 2) * 3] : new int[(count - 2) * 3];
        int testsRemaining = count * count;
        int written = 0;
        int corner = 0;
        for (int remaining = count; remaining > 3; remaining--)
        {
            int scanned = 0;
            while (!IsEar(points, links, corner, winding, ref testsRemaining))
            {
                if (++scanned == remaining || testsRemaining < 0)
                    return;
                corner = links[corner].Next;
            }

            var (previous, next) = links[corner];
            triangles[written++] = previous;
            triangles[written++] = corner;
            triangles[written++] = next;
            links[previous].Next = next;
            links[next].Previous = previous;
            corner = next;
        }
        triangles[written++] = links[corner].Previous;
        triangles[written++] = corner;
        triangles[written] = links[corner].Next;
        for (int i = 0; i < triangles.Length; i += 3)
            mesh.Add(vertices[indices[triangles[i]]], vertices[indices[triangles[i + 1]]], vertices[indices[triangles[i + 2]]]);
    }

    private static bool IsEar(ReadOnlySpan<PolygonPoint> points, ReadOnlySpan<PolygonLinks> links,
        int corner, int winding, ref int testsRemaining)
    {
        var (previous, next) = links[corner];
        PolygonPoint a = points[previous], b = points[corner], c = points[next];
        if (winding * Turn(a, b, c) <= 0)
            return false;

        double minX = Math.Min(a.X, Math.Min(b.X, c.X)), maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        double minY = Math.Min(a.Y, Math.Min(b.Y, c.Y)), maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        for (int i = links[next].Next; i != previous; i = links[i].Next)
        {
            if (--testsRemaining < 0)
                return false;
            PolygonPoint point = points[i];
            if (point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY &&
                winding * Turn(a, b, point) >= 0 && winding * Turn(b, c, point) >= 0 && winding * Turn(c, a, point) >= 0)
                return false;
        }
        return true;
    }

    private static int ProjectionAxis(List<Vertex> vertices, ReadOnlySpan<int> indices, out int winding)
    {
        Vector3 origin = vertices[indices[0]].Position;
        double x = 0, y = 0, z = 0;
        for (int i = 2; i < indices.Length; i++)
        {
            Vector3 a = vertices[indices[i - 1]].Position;
            Vector3 b = vertices[indices[i]].Position;
            double ax = (double)a.X - origin.X, ay = (double)a.Y - origin.Y, az = (double)a.Z - origin.Z;
            double bx = (double)b.X - origin.X, by = (double)b.Y - origin.Y, bz = (double)b.Z - origin.Z;
            x += ay * bz - az * by;
            y += az * bx - ax * bz;
            z += ax * by - ay * bx;
        }

        if (Math.Abs(x) >= Math.Abs(y) && Math.Abs(x) >= Math.Abs(z))
        {
            winding = Math.Sign(x);
            return 0;
        }
        if (Math.Abs(y) >= Math.Abs(z))
        {
            winding = Math.Sign(y);
            return 1;
        }
        winding = Math.Sign(z);
        return 2;
    }

    private static double Turn(PolygonPoint a, PolygonPoint b, PolygonPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private readonly record struct PolygonPoint(double X, double Y);
    private record struct PolygonLinks(int Previous, int Next);
}
