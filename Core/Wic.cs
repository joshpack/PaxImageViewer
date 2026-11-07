using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace PaxImageViewer;

/// <summary>
/// Windows Imaging Component, called through raw COM vtables (works with native AOT, no COM wrappers). It decodes
/// whatever codecs Windows has: TIFF and JPEG XR built in, HEIC/AVIF/JPEG XL/RAW from the Store extensions.
/// </summary>
internal static unsafe partial class Wic
{
    private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid Pbgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    private static readonly Guid FormatTiff = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(void* pv);

    private static void** Vt(nint p) => *(void***)p;

    private static void Release(nint p)
    {
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(p)[2])(p);
    }

    public static (WriteableBitmap? Bitmap, string Format) Decode(string path)
    {
        if (!OperatingSystem.IsWindows()) return (null, "");
        CoInitializeEx(0, 0); // COINIT_MULTITHREADED; harmless if this thread is already set up
        nint factory = 0, decoder = 0, frame = 0, converter = 0;
        try
        {
            if (CoCreateInstance(ClsidFactory, 0, 1 /* CLSCTX_INPROC_SERVER */, IidFactory, out factory) < 0) return (null, "");

            int hr;
            fixed (char* p = path)
            {
                nint d;
                // IWICImagingFactory::CreateDecoderFromFilename(path, vendor, GENERIC_READ, WICDecodeMetadataCacheOnDemand, out)
                hr = ((delegate* unmanaged[Stdcall]<nint, char*, Guid*, uint, int, nint*, int>)Vt(factory)[3])(factory, p, null, 0x80000000, 0, &d);
                decoder = d;
            }
            if (hr < 0) return (null, "");

            Guid container;
            ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Vt(decoder)[5])(decoder, &container); // GetContainerFormat

            nint f;
            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vt(decoder)[13])(decoder, 0, &f) < 0) return (null, ""); // GetFrame(0)
            frame = f;

            uint w, h;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Vt(frame)[3])(frame, &w, &h) < 0 || w == 0 || h == 0) return (null, "");

            nint c;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(factory)[10])(factory, &c) < 0) return (null, ""); // CreateFormatConverter
            converter = c;
            var fmt = Pbgra32;
            // IWICFormatConverter::Initialize(source, format, WICBitmapDitherTypeNone, palette, alpha threshold, WICBitmapPaletteTypeCustom)
            if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)Vt(converter)[8])(converter, frame, &fmt, 0, 0, 0.0, 0) < 0)
                return (null, "");

            var bmp = ViewerImage.NewBitmap((int)w, (int)h);
            using (var fb = bmp.Lock())
            {
                var size = (uint)(fb.RowBytes * (long)h);
                // IWICBitmapSource::CopyPixels(whole image, stride, size, buffer)
                if (((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Vt(converter)[7])(converter, null, (uint)fb.RowBytes, size, (byte*)fb.Address) < 0)
                {
                    bmp.Dispose();
                    return (null, "");
                }
            }

            var format = FormatName(path);
            // TIFFs carry the camera's rotation as metadata that WIC leaves to the caller.
            if (container == FormatTiff && Orientation(frame) is var o and > 1 and <= 8)
            {
                WriteableBitmap turned;
                using (var fb = bmp.Lock())
                using (var src = new SKBitmap())
                {
                    src.InstallPixels(new SKImageInfo(fb.Size.Width, fb.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), fb.Address, fb.RowBytes);
                    turned = ImageLoader.Orient(src, (SKEncodedOrigin)o);
                }
                bmp.Dispose();
                return (turned, format);
            }
            return (bmp, format);
        }
        catch
        {
            return (null, "");
        }
        finally
        {
            Release(converter);
            Release(frame);
            Release(decoder);
            Release(factory);
        }
    }

    /// <summary>System.Photo.Orientation (EXIF 1–8), or 0 when the image doesn't say.</summary>
    private static int Orientation(nint frame)
    {
        nint reader;
        // IWICBitmapFrameDecode::GetMetadataQueryReader
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(frame)[8])(frame, &reader) < 0 || reader == 0) return 0;
        try
        {
            var pv = stackalloc byte[32]; // PROPVARIANT (24 bytes on x64), with room to spare
            new Span<byte>(pv, 32).Clear();
            int hr;
            fixed (char* q = "/ifd/{ushort=274}")
                hr = ((delegate* unmanaged[Stdcall]<nint, char*, void*, int>)Vt(reader)[5])(reader, q, pv); // GetMetadataByName
            if (hr < 0) return 0;
            var vt = *(ushort*)pv;
            var value = vt == 18 /* VT_UI2 */ ? *(ushort*)(pv + 8) : 0;
            PropVariantClear(pv);
            return value;
        }
        finally
        {
            Release(reader);
        }
    }

    private static string FormatName(string path) => System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
    {
        "TIF" or "TIFF" => "TIFF",
        "HEIC" or "HEIF" or "HIF" => "HEIF",
        "JXR" or "WDP" or "HDP" => "JPEG XR",
        "JXL" => "JPEG XL",
        var e => e,
    };
}
