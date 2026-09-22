using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SeaOfStarsLocalMap
{
    public readonly struct MapWorldPoint
    {
        public readonly double X, Y;
        public MapWorldPoint(double x, double y) { X = x; Y = y; }
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    }

    public readonly struct MapWorldRect
    {
        public readonly double MinX, MinY, MaxX, MaxY;
        public MapWorldRect(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public MapWorldPoint Center => new MapWorldPoint(MinX + Width * 0.5, MinY + Height * 0.5);
        public bool IsValid => double.IsFinite(MinX) && double.IsFinite(MinY) &&
            double.IsFinite(MaxX) && double.IsFinite(MaxY) &&
            double.IsFinite(Width) && double.IsFinite(Height) && Width > 0d && Height > 0d;
    }

    /// <summary>
    /// Fog for one fixed base image and floor. Mask rows run bottom-up, matching minY.
    /// Revealing only lowers opacity; geometry and dimensions never change.
    /// The opacity buffer belongs to this object and must be treated as read-only.
    /// </summary>
    public sealed class StaticFog
    {
        private const uint Magic = 0x474F4653; // SFOG
        private const int FormatVersion = 1;
        private const int FixedSaveBytes = 87;
        private readonly byte[] baseIdBytes;
        private int dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY;
        public int Width { get; }
        public int Height { get; }
        public MapWorldRect Bounds { get; }
        public string BaseId { get; }
        public byte HiddenOpacity { get; }
        public byte[] Opacity { get; }
        public long Revision { get; private set; }

        /// <param name="baseId">Stable identity of this exact base image and floor, preferably including its content hash.</param>
        public StaticFog(int width, int height, MapWorldRect bounds, string baseId, byte hiddenOpacity = 240)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > 4194304)
                throw new ArgumentOutOfRangeException(nameof(width), "Fog dimensions must fit within 4096 per edge and 4194304 pixels.");
            if (!bounds.IsValid) throw new ArgumentException("Fog requires finite, fixed world bounds with positive size.", nameof(bounds));
            if (string.IsNullOrWhiteSpace(baseId)) throw new ArgumentException("A base image and floor identity is required.", nameof(baseId));
            baseIdBytes = new UTF8Encoding(false, true).GetBytes(baseId);
            if (baseIdBytes.Length > 1024) throw new ArgumentException("Base identity must fit within 1024 UTF-8 bytes.", nameof(baseId));
            if (hiddenOpacity == 0) throw new ArgumentOutOfRangeException(nameof(hiddenOpacity));
            Width = width; Height = height; Bounds = bounds; BaseId = baseId; HiddenOpacity = hiddenOpacity;
            Opacity = new byte[width * height];
            Array.Fill(Opacity, hiddenOpacity);
            dirtyMinX = 0; dirtyMinY = 0; dirtyMaxX = width - 1; dirtyMaxY = height - 1;
        }

        public bool RevealDisc(MapWorldPoint center, double radius, double softEdge = 0d) =>
            RevealSegment(center, center, radius, softEdge);

        /// <summary>Returns the saved fog opacity at one world point, or the hidden value outside this map.</summary>
        public byte OpacityAt(MapWorldPoint point)
        {
            if (!point.IsFinite || point.X < Bounds.MinX || point.X > Bounds.MaxX ||
                point.Y < Bounds.MinY || point.Y > Bounds.MaxY) return HiddenOpacity;
            int x = Math.Min(Width - 1, Math.Max(0, (int)((point.X - Bounds.MinX) / Bounds.Width * Width)));
            int y = Math.Min(Height - 1, Math.Max(0, (int)((point.Y - Bounds.MinY) / Bounds.Height * Height)));
            return Opacity[y * Width + x];
        }

        /// <summary>Landmarks become known once their map pixel is inside the solid or near-solid revealed area.</summary>
        public bool IsRevealed(MapWorldPoint point, byte maximumOpacity = 48) => OpacityAt(point) <= maximumOpacity;

        /// <summary>
        /// Reveals a continuous capsule along movement, including both endpoints.
        /// radius is fully revealed; softEdge is an additional world-space feather width.
        /// Call separately for each floor, and do not connect teleport destinations.
        /// </summary>
        public bool RevealSegment(MapWorldPoint from, MapWorldPoint to, double radius, double softEdge = 0d)
        {
            if (!from.IsFinite || !to.IsFinite) throw new ArgumentException("Reveal positions must be finite.");
            if (!double.IsFinite(radius) || !double.IsFinite(softEdge) || radius < 0d || softEdge < 0d || !double.IsFinite(radius + softEdge))
                throw new ArgumentOutOfRangeException(nameof(radius), "Reveal radius and feather must be finite and nonnegative.");
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double length = Hypot(dx, dy);
            if (!double.IsFinite(length)) throw new ArgumentException("Reveal segment exceeds the supported coordinate range.");
            double ux = length > 0d ? dx / length : 0d, uy = length > 0d ? dy / length : 0d;
            double outer = radius + softEdge;
            double left = Math.Max(Bounds.MinX, Math.Min(from.X, to.X) - outer);
            double right = Math.Min(Bounds.MaxX, Math.Max(from.X, to.X) + outer);
            double bottom = Math.Max(Bounds.MinY, Math.Min(from.Y, to.Y) - outer);
            double top = Math.Min(Bounds.MaxY, Math.Max(from.Y, to.Y) + outer);
            if (right < left || top < bottom) return false;
            double scaleX = Width / Bounds.Width, scaleY = Height / Bounds.Height;
            int xStart = ClipIndex(Math.Ceiling((left - Bounds.MinX) * scaleX - 0.5), Width);
            int xEnd = ClipIndex(Math.Floor((right - Bounds.MinX) * scaleX - 0.5) + 1d, Width);
            int yStart = ClipIndex(Math.Ceiling((bottom - Bounds.MinY) * scaleY - 0.5), Height);
            int yEnd = ClipIndex(Math.Floor((top - Bounds.MinY) * scaleY - 0.5) + 1d, Height);
            bool changed = false;
            int changedMinX = Width, changedMinY = Height, changedMaxX = -1, changedMaxY = -1;
            for (int y = yStart; y < yEnd; y++)
            {
                double worldY = Bounds.MinY + (y + 0.5) / scaleY;
                for (int x = xStart; x < xEnd; x++)
                {
                    int index = y * Width + x;
                    if (Opacity[index] == 0) continue;
                    double worldX = Bounds.MinX + (x + 0.5) / scaleX;
                    double along = length > 0d ? Math.Clamp((worldX - from.X) * ux + (worldY - from.Y) * uy, 0d, length) : 0d;
                    double distance = Hypot(worldX - (from.X + ux * along), worldY - (from.Y + uy * along));
                    if (!double.IsFinite(distance) || distance > outer) continue;
                    byte next = 0;
                    if (distance > radius)
                    {
                        if (softEdge == 0d) continue;
                        double t = Math.Clamp((distance - radius) / softEdge, 0d, 1d);
                        next = (byte)Math.Round(HiddenOpacity * t * t * (3d - 2d * t));
                    }
                    if (next >= Opacity[index]) continue;
                    Opacity[index] = next;
                    changed = true;
                    if (x < changedMinX) changedMinX = x;
                    if (x > changedMaxX) changedMaxX = x;
                    if (y < changedMinY) changedMinY = y;
                    if (y > changedMaxY) changedMaxY = y;
                }
            }
            if (changed)
            {
                Revision++;
                MarkDirty(changedMinX, changedMinY, changedMaxX, changedMaxY);
            }
            return changed;
        }

        /// <summary>
        /// Returns and clears the smallest pixel rectangle changed since the previous call.
        /// This lets the runtime update only that part of the GPU fog texture.
        /// </summary>
        public bool TryConsumeDirtyRect(out int x, out int y, out int width, out int height)
        {
            if (dirtyMaxX < dirtyMinX || dirtyMaxY < dirtyMinY)
            {
                x = y = width = height = 0;
                return false;
            }
            x = dirtyMinX; y = dirtyMinY;
            width = dirtyMaxX - dirtyMinX + 1;
            height = dirtyMaxY - dirtyMinY + 1;
            dirtyMinX = Width; dirtyMinY = Height; dirtyMaxX = dirtyMaxY = -1;
            return true;
        }

        /// <summary>Serializes exact geometry, base identity and mask, with a SHA-256 integrity checksum.</summary>
        public byte[] Save()
        {
            using var stream = new MemoryStream(FixedSaveBytes + baseIdBytes.Length + Opacity.Length);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Magic); writer.Write(FormatVersion);
            writer.Write(Width); writer.Write(Height);
            writer.Write(Bounds.MinX); writer.Write(Bounds.MinY); writer.Write(Bounds.MaxX); writer.Write(Bounds.MaxY);
            writer.Write(HiddenOpacity);
            writer.Write((ushort)baseIdBytes.Length); writer.Write(baseIdBytes);
            writer.Write(Opacity.Length); writer.Write(Opacity);
            writer.Flush();
            byte[] checksum = SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
            writer.Write(checksum); writer.Flush();
            return stream.ToArray();
        }

        /// <summary>
        /// Validates the entire save before changing this mask. Requires matching format,
        /// geometry, opacity and base/floor identity. Merges known areas monotonically;
        /// on a fresh instance this restores the exact saved mask.
        /// </summary>
        public void Load(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length != FixedSaveBytes + baseIdBytes.Length + Opacity.Length)
                throw new InvalidDataException("Fog save size does not match this base map.");
            int payloadLength = data.Length - 32;
            byte[] checksum = SHA256.HashData(data.AsSpan(0, payloadLength));
            if (!CryptographicOperations.FixedTimeEquals(checksum, data.AsSpan(payloadLength, 32)))
                throw new InvalidDataException("Fog save checksum is invalid.");
            using var reader = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8, false);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
                throw new InvalidDataException("Unsupported fog save format or version.");
            if (reader.ReadInt32() != Width || reader.ReadInt32() != Height ||
                reader.ReadDouble() != Bounds.MinX || reader.ReadDouble() != Bounds.MinY ||
                reader.ReadDouble() != Bounds.MaxX || reader.ReadDouble() != Bounds.MaxY || reader.ReadByte() != HiddenOpacity)
                throw new InvalidDataException("Fog save geometry does not match this base map.");
            int identityLength = reader.ReadUInt16();
            if (identityLength != baseIdBytes.Length || !reader.ReadBytes(identityLength).AsSpan().SequenceEqual(baseIdBytes))
                throw new InvalidDataException("Fog save belongs to a different base image or floor.");
            if (reader.ReadInt32() != Opacity.Length) throw new InvalidDataException("Fog mask length is invalid.");
            int offset = (int)reader.BaseStream.Position;
            for (int i = 0; i < Opacity.Length; i++)
                if (data[offset + i] > HiddenOpacity) throw new InvalidDataException("Fog opacity exceeds the map's hidden opacity.");
            bool changed = false;
            int changedMinX = Width, changedMinY = Height, changedMaxX = -1, changedMaxY = -1;
            for (int i = 0; i < Opacity.Length; i++)
            {
                byte value = data[offset + i];
                if (value >= Opacity[i]) continue;
                Opacity[i] = value;
                changed = true;
                int x = i % Width, y = i / Width;
                if (x < changedMinX) changedMinX = x;
                if (x > changedMaxX) changedMaxX = x;
                if (y < changedMinY) changedMinY = y;
                if (y > changedMaxY) changedMaxY = y;
            }
            if (changed)
            {
                Revision++;
                MarkDirty(changedMinX, changedMinY, changedMaxX, changedMaxY);
            }
        }

        private void MarkDirty(int minX, int minY, int maxX, int maxY)
        {
            if (minX > maxX || minY > maxY) return;
            dirtyMinX = Math.Min(dirtyMinX, minX); dirtyMinY = Math.Min(dirtyMinY, minY);
            dirtyMaxX = Math.Max(dirtyMaxX, maxX); dirtyMaxY = Math.Max(dirtyMaxY, maxY);
        }

        private static int ClipIndex(double value, int max) => (int)Math.Clamp(value, 0d, max);
        private static double Hypot(double x, double y)
        {
            x = Math.Abs(x); y = Math.Abs(y);
            double max = Math.Max(x, y);
            if (max == 0d || double.IsInfinity(max)) return max;
            double ratio = Math.Min(x, y) / max;
            return max * Math.Sqrt(1d + ratio * ratio);
        }
    }

    /// <summary>Pure view calculations; changing a view never resizes or rewrites the base map.</summary>
    public static class MapViewport
    {
        /// <summary>Player-centered minimap with a constant horizontal world span.</summary>
        public static MapWorldRect FixedZoom(MapWorldPoint center, double worldWidth, int pixelWidth, int pixelHeight)
        {
            ValidateViewport(pixelWidth, pixelHeight);
            if (!center.IsFinite || !double.IsFinite(worldWidth) || worldWidth <= 0d)
                throw new ArgumentException("View center and horizontal span must be finite and valid.");
            double worldHeight = worldWidth * ((double)pixelHeight / pixelWidth);
            var result = new MapWorldRect(center.X - worldWidth * 0.5, center.Y - worldHeight * 0.5,
                center.X + worldWidth * 0.5, center.Y + worldHeight * 0.5);
            if (!result.IsValid) throw new ArgumentException("View exceeds the supported coordinate range.");
            return result;
        }

        /// <summary>Fits the full map at zoom 1. Larger zoom narrows the view; center supports manual pan.</summary>
        public static MapWorldRect ForOverview(MapWorldRect bounds, int pixelWidth, int pixelHeight,
            double zoom = 1d, MapWorldPoint? center = null)
        {
            ValidateViewport(pixelWidth, pixelHeight);
            if (!bounds.IsValid || !double.IsFinite(zoom) || zoom <= 0d) throw new ArgumentException("Overview bounds and zoom must be valid.");
            double fittedWidth = Math.Max(bounds.Width, bounds.Height * ((double)pixelWidth / pixelHeight));
            return FixedZoom(center ?? bounds.Center, fittedWidth / zoom, pixelWidth, pixelHeight);
        }

        /// <summary>Keeps a manually panned view within the base without altering its zoom or aspect ratio.</summary>
        public static MapWorldRect ClampToBounds(MapWorldRect view, MapWorldRect bounds)
        {
            if (!view.IsValid || !bounds.IsValid) throw new ArgumentException("View and map bounds must be valid.");
            double x = view.Width >= bounds.Width ? bounds.Center.X : Math.Clamp(view.Center.X, bounds.MinX + view.Width * 0.5, bounds.MaxX - view.Width * 0.5);
            double y = view.Height >= bounds.Height ? bounds.Center.Y : Math.Clamp(view.Center.Y, bounds.MinY + view.Height * 0.5, bounds.MaxY - view.Height * 0.5);
            return new MapWorldRect(x - view.Width * 0.5, y - view.Height * 0.5, x + view.Width * 0.5, y + view.Height * 0.5);
        }

        private static void ValidateViewport(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        }
    }
}
