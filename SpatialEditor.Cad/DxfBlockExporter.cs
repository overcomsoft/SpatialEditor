using System.Globalization;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Header;
using netDxf.Tables;
using NetTopologySuite.Geometries;
using DxfAttribute = netDxf.Entities.Attribute;

namespace SpatialEditor.Cad;

/// <summary>
/// One equipment placement to export: the block it instantiates, where it sits, how it is
/// rotated/scaled, and its geometry in world coordinates (used to derive the block definition).
/// </summary>
public sealed record CadExportInstance(
    string BlockName,
    string? Layer,
    double InsertX,
    double InsertY,
    double RotationDegrees,
    double ScaleX,
    double ScaleY,
    Geometry WorldGeometry);

/// <summary>
/// Writes edited equipment as a DXF: one BLOCK definition per distinct block name (geometry in the
/// block's own local coordinates) and one INSERT per instance. Each INSERT carries the equipment
/// name, rotation and position point as block attributes (ATTRIBs) in addition to its native
/// insertion point / rotation, so downstream CAD tools can read them as plain attribute data.
/// </summary>
public sealed class DxfBlockExporter
{
    public const string NameTag = "NAME";
    public const string RotationTag = "ROTATION";
    public const string PositionXTag = "POS_X";
    public const string PositionYTag = "POS_Y";
    private const string DefaultLayerName = "Equipment";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Exports the instances to <paramref name="filePath"/> and returns how many INSERTs were written.</summary>
    public int Export(string filePath, IReadOnlyList<CadExportInstance> instances)
    {
        var document = new DxfDocument(DxfVersion.AutoCad2010);
        var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        var written = 0;

        foreach (var instance in instances)
        {
            if (instance.WorldGeometry.IsEmpty || Math.Abs(instance.ScaleX) < 1e-12 || Math.Abs(instance.ScaleY) < 1e-12)
            {
                continue;
            }

            var blockName = SanitizeName(instance.BlockName);
            if (!blocks.TryGetValue(blockName, out var block))
            {
                block = CreateBlock(blockName, instance);
                blocks.Add(blockName, block);
            }

            var layerName = SanitizeName(string.IsNullOrWhiteSpace(instance.Layer) ? DefaultLayerName : instance.Layer);
            if (!layers.TryGetValue(layerName, out var layer))
            {
                layer = new Layer(layerName);
                layers.Add(layerName, layer);
            }

            var insert = new Insert(block, new Vector3(instance.InsertX, instance.InsertY, 0))
            {
                Layer = layer,
                Rotation = instance.RotationDegrees,
                Scale = new Vector3(instance.ScaleX, instance.ScaleY, 1)
            };
            SetAttribute(insert, NameTag, instance.BlockName);
            SetAttribute(insert, RotationTag, instance.RotationDegrees.ToString("0.######", Invariant));
            SetAttribute(insert, PositionXTag, instance.InsertX.ToString("0.######", Invariant));
            SetAttribute(insert, PositionYTag, instance.InsertY.ToString("0.######", Invariant));
            document.Entities.Add(insert);
            written++;
        }

        document.Save(filePath);
        return written;
    }

    private static Block CreateBlock(string blockName, CadExportInstance source)
    {
        var block = new Block(blockName);
        foreach (var tag in new[] { NameTag, RotationTag, PositionXTag, PositionYTag })
        {
            block.AttributeDefinitions.Add(new AttributeDefinition(tag)
            {
                Prompt = tag,
                Value = string.Empty,
                Height = 1,
                Flags = AttributeFlags.Hidden
            });
        }

        // World -> block-local: undo translation, then rotation, then scale (inverse of the INSERT transform).
        var radians = source.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        Coordinate ToLocal(Coordinate world)
        {
            var dx = world.X - source.InsertX;
            var dy = world.Y - source.InsertY;
            return new Coordinate((dx * cos + dy * sin) / source.ScaleX, (-dx * sin + dy * cos) / source.ScaleY);
        }

        AddGeometry(block, source.WorldGeometry, ToLocal);
        return block;
    }

    private static void AddGeometry(Block block, Geometry geometry, Func<Coordinate, Coordinate> toLocal)
    {
        switch (geometry)
        {
            case GeometryCollection collection:
                foreach (var part in collection.Geometries)
                {
                    AddGeometry(block, part, toLocal);
                }
                break;
            case Polygon polygon:
                AddPolyline(block, polygon.ExteriorRing.Coordinates, true, toLocal);
                for (var i = 0; i < polygon.NumInteriorRings; i++)
                {
                    AddPolyline(block, polygon.GetInteriorRingN(i).Coordinates, true, toLocal);
                }
                break;
            case LineString line:
                AddPolyline(block, line.Coordinates, line.IsClosed && line.NumPoints >= 4, toLocal);
                break;
            case NetTopologySuite.Geometries.Point point when !point.IsEmpty:
                var local = toLocal(point.Coordinate);
                block.Entities.Add(new netDxf.Entities.Point(local.X, local.Y, 0));
                break;
        }
    }

    private static void AddPolyline(Block block, Coordinate[] coordinates, bool closed, Func<Coordinate, Coordinate> toLocal)
    {
        var count = closed && coordinates.Length > 1 && coordinates[0].Equals2D(coordinates[^1])
            ? coordinates.Length - 1
            : coordinates.Length;
        if (count < 2)
        {
            return;
        }

        var vertexes = new List<Polyline2DVertex>(count);
        for (var i = 0; i < count; i++)
        {
            var local = toLocal(coordinates[i]);
            vertexes.Add(new Polyline2DVertex(local.X, local.Y));
        }

        block.Entities.Add(new Polyline2D(vertexes, closed));
    }

    private static void SetAttribute(Insert insert, string tag, string value)
    {
        foreach (DxfAttribute attribute in insert.Attributes)
        {
            if (string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = value;
                return;
            }
        }
    }

    /// <summary>DXF table names may not contain &lt; &gt; / \ " : ; ? * | = ` and cannot be empty.</summary>
    private static string SanitizeName(string? name)
    {
        var invalid = "<>/\\\":;?*|=`,".ToCharArray();
        var cleaned = new string((name ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "BLOCK" : cleaned;
    }
}
