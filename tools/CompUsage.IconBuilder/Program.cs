using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Package the approved PNG artwork into Windows icon frames without changing its design.
try
{
    if (args.Length != 3) throw new ArgumentException("Usage: IconBuilder source.png target.ico ui-icon.png");
    var source = BitmapFrame.Create(new Uri(Path.GetFullPath(args[0])), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    if (source.PixelWidth != source.PixelHeight) throw new ArgumentException("The icon source must be square.");
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var frames = sizes.Select(size =>
    {
        var drawing = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.HighQuality);
        using (var context = drawing.RenderOpen()) context.DrawImage(source, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream(); encoder.Save(memory);
        var png = memory.ToArray();
        // Small uncompressed frames also work with installer stubs; the 256px frame uses PNG compression.
        return (Icon: size == 256 ? png : Dib(bitmap, size), Png: png);
    }).ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    using (var writer = new BinaryWriter(File.Create(args[1])))
    {
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frames[i].Icon.Length); writer.Write(offset); offset += frames[i].Icon.Length;
        }
        foreach (var frame in frames) writer.Write(frame.Icon);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    File.WriteAllBytes(args[2], frames[7].Png);
    var check = new IconBitmapDecoder(new Uri(Path.GetFullPath(args[1])), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    if (check.Frames.Count != sizes.Length || !check.Frames.Select(f => f.PixelWidth).Order().SequenceEqual(sizes)
        || check.Frames.Any(f => f.PixelHeight != f.PixelWidth))
        throw new InvalidDataException("The generated icon frames could not be decoded.");
    Console.WriteLine("Windows icon frames: " + string.Join(", ", check.Frames.Select(f => f.PixelWidth).Order()));

    static byte[] Dib(BitmapSource bitmap, int size)
    {
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = size * 4;
        var pixels = new byte[stride * size]; straight.CopyPixels(pixels, stride, 0);
        var maskStride = (size + 31) / 32 * 4;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write((stride + maskStride) * size);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var row = size - 1; row >= 0; row--) writer.Write(pixels, row * stride, stride);
        for (var row = size - 1; row >= 0; row--)
        {
            var mask = new byte[maskStride];
            for (var x = 0; x < size; x++)
                if (pixels[row * stride + x * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
            writer.Write(mask);
        }
        return memory.ToArray();
    }
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
