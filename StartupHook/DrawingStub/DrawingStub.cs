// Minimal stub for System.Drawing.Common to satisfy type resolution on Linux.
// Only implements the types actually referenced by BC service tier DLLs.
// All methods are no-ops or return sensible defaults.

using System.IO;

// ============================================================================
// System.Drawing
// ============================================================================

namespace System.Drawing
{
    public abstract class Image : IDisposable
    {
        public virtual int Width => 0;
        public virtual int Height => 0;
        public Guid[] FrameDimensionsList => Array.Empty<Guid>();
        public int[] PropertyIdList => Array.Empty<int>();
        public Drawing.Imaging.ImageFormat RawFormat => Drawing.Imaging.ImageFormat.Png;

        public int GetFrameCount(Drawing.Imaging.FrameDimension dimension) => 0;
        public void SelectActiveFrame(Drawing.Imaging.FrameDimension dimension, int frameIndex) { }

        public void Save(Stream stream, Drawing.Imaging.ImageFormat format) { }
        public void Save(Stream stream, Drawing.Imaging.ImageCodecInfo encoder,
            Drawing.Imaging.EncoderParameters? encoderParams) { }
        public void SaveAdd(Drawing.Imaging.EncoderParameters encoderParams) { }

        public Drawing.Imaging.PropertyItem? GetPropertyItem(int propid) => null;
        public void SetPropertyItem(Drawing.Imaging.PropertyItem propitem) { }

        public static Image FromStream(Stream stream) => new Bitmap(stream);
        public static Image FromStream(Stream stream, bool useEmbeddedColorManagement) => new Bitmap(stream);

        public void Dispose() { }
        protected virtual void Dispose(bool disposing) { }
    }

    public sealed class Bitmap : Image
    {
        public Bitmap(int width, int height) { }
        public Bitmap(Stream stream) { }
        public Bitmap(Image original, int width, int height) { }
    }

    public struct Color
    {
        public byte A { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public bool IsEmpty => A == 0 && R == 0 && G == 0 && B == 0;
        public string Name => $"{A:X2}{R:X2}{G:X2}{B:X2}";

        public static Color Black => new Color();
        public static Color White => new Color();
        public static Color Empty => new Color();

        public static Color FromArgb(int argb) => new Color();
        public static Color FromArgb(int alpha, Color baseColor) => new Color();
        public static Color FromArgb(int red, int green, int blue) => new Color();
        public static Color FromArgb(int alpha, int red, int green, int blue) => new Color();
        public static Color FromName(string name) => new Color();

        public int ToArgb() => (A << 24) | (R << 16) | (G << 8) | B;
    }

    public static class ColorTranslator
    {
        public static Color FromHtml(string htmlColor) => Color.Empty;
        public static Color FromOle(int oleColor) => Color.Empty;
        public static Color FromWin32(int win32Color) => Color.Empty;
        public static string ToHtml(Color c) => string.Empty;
        public static int ToOle(Color c) => 0;
        public static int ToWin32(Color c) => 0;
    }

    public struct Point
    {
        public int X { get; set; }
        public int Y { get; set; }

        public Point(int x, int y) { X = x; Y = y; }
    }

    public struct PointF
    {
        public float X { get; set; }
        public float Y { get; set; }

        public PointF(float x, float y) { X = x; Y = y; }
    }

    public struct Rectangle
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public Rectangle(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }

    public struct RectangleF
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }

        public RectangleF(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }

    public struct Size
    {
        public int Width { get; set; }
        public int Height { get; set; }

        public Size(int width, int height) { Width = width; Height = height; }
    }

    public struct SizeF
    {
        public float Width { get; set; }
        public float Height { get; set; }

        public SizeF(float width, float height) { Width = width; Height = height; }
    }

    public sealed class FontFamily : IDisposable
    {
        public string Name => string.Empty;
        public FontFamily() { }
        public FontFamily(string name) { }
        public void Dispose() { }
    }

    public sealed class Font : IDisposable
    {
        public string Name { get; }
        public float Size { get; }
        public FontStyle Style { get; }

        public Font(string familyName, float emSize)
        {
            Name = familyName ?? string.Empty;
            Size = emSize;
            Style = FontStyle.Regular;
        }

        public Font(string familyName, float emSize, FontStyle style)
        {
            Name = familyName ?? string.Empty;
            Size = emSize;
            Style = style;
        }

        public Font(FontFamily family, float emSize)
        {
            Name = family?.Name ?? string.Empty;
            Size = emSize;
            Style = FontStyle.Regular;
        }

        public void Dispose() { }
    }

    [Flags]
    public enum FontStyle
    {
        Regular = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikeout = 8,
    }

    public static class SystemFonts
    {
        public static Font IconTitleFont => new Font("Sans", 10f);
        public static Font DefaultFont => new Font("Sans", 10f);
    }

    public abstract class Brush : IDisposable
    {
        public void Dispose() { }
    }

    public sealed class SolidBrush : Brush
    {
        public Color Color { get; set; }
        public SolidBrush(Color color) { Color = color; }
    }

    public sealed class Pen : IDisposable
    {
        public Color Color { get; set; }
        public float Width { get; set; }

        public Pen(Color color) { Color = color; Width = 1f; }
        public Pen(Color color, float width) { Color = color; Width = width; }
        public void Dispose() { }
    }

    public sealed class StringFormat : IDisposable
    {
        public StringAlignment Alignment { get; set; }
        public StringAlignment LineAlignment { get; set; }
        public void Dispose() { }
    }

    public enum StringAlignment
    {
        Near = 0,
        Center = 1,
        Far = 2,
    }

    public sealed class Graphics : IDisposable
    {
        public Drawing2D.SmoothingMode SmoothingMode { get; set; }
        public Drawing2D.InterpolationMode InterpolationMode { get; set; }
        public Drawing2D.PixelOffsetMode PixelOffsetMode { get; set; }
        public Drawing2D.CompositingQuality CompositingQuality { get; set; }
        public Text.TextRenderingHint TextRenderingHint { get; set; }

        public static Graphics FromImage(Image image) => new Graphics();

        public void FillRectangle(Brush brush, RectangleF rect) { }
        public void FillRectangle(Brush brush, Rectangle rect) { }
        public void DrawString(string s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format) { }
        public void DrawString(string s, Font font, Brush brush, PointF point) { }
        public void DrawImage(Image image, Point point) { }
        public void DrawImage(Image image, int x, int y) { }
        public void DrawImage(Image image, Rectangle destRect) { }
        public void Flush() { }
        public void Dispose() { }
    }

    public enum GraphicsUnit
    {
        World = 0,
        Display = 1,
        Pixel = 2,
        Point = 3,
        Inch = 4,
        Document = 5,
        Millimeter = 6,
    }
}

// ============================================================================
// System.Drawing.Text
// ============================================================================

namespace System.Drawing.Text
{
    public abstract class FontCollection : IDisposable
    {
        public FontFamily[] Families => Array.Empty<FontFamily>();
        public void Dispose() { }
    }

    public sealed class InstalledFontCollection : FontCollection
    {
        public InstalledFontCollection() { }
    }

    public enum TextRenderingHint
    {
        SystemDefault = 0,
        SingleBitPerPixelGridFit = 1,
        SingleBitPerPixel = 2,
        AntiAliasGridFit = 3,
        AntiAlias = 4,
        ClearTypeGridFit = 5,
    }
}

// ============================================================================
// System.Drawing.Drawing2D
// ============================================================================

namespace System.Drawing.Drawing2D
{
    public enum SmoothingMode
    {
        Invalid = -1,
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        AntiAlias = 4,
    }

    public enum InterpolationMode
    {
        Invalid = -1,
        Default = 0,
        Low = 1,
        High = 2,
        Bilinear = 3,
        Bicubic = 4,
        NearestNeighbor = 5,
        HighQualityBilinear = 6,
        HighQualityBicubic = 7,
    }

    public enum PixelOffsetMode
    {
        Invalid = -1,
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        Half = 4,
    }

    public enum CompositingQuality
    {
        Invalid = -1,
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        GammaCorrected = 3,
        AssumeLinear = 4,
    }
}

// ============================================================================
// System.Drawing.Imaging
// ============================================================================

namespace System.Drawing.Imaging
{
    public sealed class ImageFormat
    {
        private readonly Guid _guid;

        public Guid Guid => _guid;

        public ImageFormat(Guid guid) { _guid = guid; }

        public static ImageFormat Bmp => new ImageFormat(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Jpeg => new ImageFormat(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Png => new ImageFormat(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Gif => new ImageFormat(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Tiff => new ImageFormat(new Guid("b96b3cb1-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Exif => new ImageFormat(new Guid("b96b3cb2-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Icon => new ImageFormat(new Guid("b96b3cb5-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Emf => new ImageFormat(new Guid("b96b3cac-0728-11d3-9d7b-0000f81ef32e"));
        public static ImageFormat Wmf => new ImageFormat(new Guid("b96b3cad-0728-11d3-9d7b-0000f81ef32e"));

        public override bool Equals(object? o)
        {
            if (o is ImageFormat other) return _guid == other._guid;
            return false;
        }

        public override int GetHashCode() => _guid.GetHashCode();
    }

    public sealed class FrameDimension
    {
        private readonly Guid _guid;

        public Guid Guid => _guid;

        public FrameDimension(Guid guid) { _guid = guid; }

        public static FrameDimension Page => new FrameDimension(new Guid("7462dc86-6180-4c7e-8e3f-ee7333a7a483"));
        public static FrameDimension Time => new FrameDimension(new Guid("6aedbd6d-3fb5-418a-83a6-7f45229dc872"));
        public static FrameDimension Resolution => new FrameDimension(new Guid("84236f7b-3bd3-428f-8dab-4ea1439ca315"));

        public override bool Equals(object? o)
        {
            if (o is FrameDimension other) return _guid == other._guid;
            return false;
        }

        public override int GetHashCode() => _guid.GetHashCode();
    }

    public sealed class ImageCodecInfo
    {
        public Guid FormatID { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public string CodecName { get; set; } = string.Empty;

        public static ImageCodecInfo[] GetImageDecoders() => Array.Empty<ImageCodecInfo>();
        public static ImageCodecInfo[] GetImageEncoders() => Array.Empty<ImageCodecInfo>();
    }

    public sealed class Encoder
    {
        private readonly Guid _guid;

        public Guid Guid => _guid;

        public Encoder(Guid guid) { _guid = guid; }

        public static readonly Encoder Quality = new Encoder(new Guid("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb"));
        public static readonly Encoder SaveFlag = new Encoder(new Guid("292266fc-ac40-47bf-8cfc-a85b89a655de"));
        public static readonly Encoder Compression = new Encoder(new Guid("e09d739d-ccd4-44ee-8eba-3fbf8be4fc58"));
    }

    public sealed class EncoderParameter : IDisposable
    {
        public EncoderParameter(Encoder encoder, long value) { }
        public EncoderParameter(Encoder encoder, int value) { }
        public void Dispose() { }
    }

    public sealed class EncoderParameters : IDisposable
    {
        public EncoderParameter[] Param { get; set; }

        public EncoderParameters()
        {
            Param = new EncoderParameter[1];
        }

        public EncoderParameters(int count)
        {
            Param = new EncoderParameter[count];
        }

        public void Dispose() { }
    }

    public enum EncoderValue
    {
        MultiFrame = 18,
        FrameDimensionTime = 21,
        FrameDimensionResolution = 22,
        FrameDimensionPage = 23,
        Flush = 20,
    }

    public sealed class PropertyItem
    {
        public int Id { get; set; }
        public int Len { get; set; }
        public short Type { get; set; }
        public byte[]? Value { get; set; }
    }

    public enum PixelFormat
    {
        Format32bppArgb = 2498570,
        Format24bppRgb = 137224,
        Format32bppRgb = 139273,
    }
}

// ============================================================================
// System.Drawing.Printing
// ============================================================================

namespace System.Drawing.Printing
{
    public enum PaperSourceKind
    {
        Upper = 1, Lower = 2, Middle = 3, Manual = 4, Envelope = 5,
        EnvelopeManual = 6, AutomaticFeed = 7, TractorFeed = 8,
        SmallFormat = 9, LargeFormat = 10, LargeCapacity = 11,
        Cassette = 14, FormSource = 15, Custom = 257,
    }

    public class PrinterSettings
    {
        public string PrinterName { get; set; } = "";
        public bool IsValid => false;
        public StringCollection InstalledPrinters => new StringCollection();

        public class StringCollection : System.Collections.IEnumerable
        {
            public int Count => 0;
            public string this[int index] => throw new IndexOutOfRangeException();
            public System.Collections.IEnumerator GetEnumerator() { yield break; }
        }
    }
}
