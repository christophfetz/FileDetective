using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace FileDetective.Core;

/// <summary>
/// The text recognition built into Windows (<c>Windows.Media.Ocr</c>, PLAN.md 11.3): offline, in the languages of the user
/// profile. Nothing leaves the computer. The image is decoded with <c>Windows.Graphics.Imaging</c> (a GIF with its first
/// picture), turned according to its EXIF orientation and scaled down if a side is larger than
/// <see cref="OcrEngine.MaxImageDimension"/>. Thread-safe: one engine, calls are serialized.
/// </summary>
public sealed class WindowsImageTextRecognizer : IImageTextRecognizer
{
    private readonly object _gate = new();
    private readonly OcrEngine? _engine;

    public WindowsImageTextRecognizer()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _engine = null;
        }
    }

    public bool IsAvailable => _engine is not null;

    /// <summary>The language the engine reads (e.g. <c>de-DE</c>), or null without an engine.</summary>
    public string? LanguageTag => _engine?.RecognizerLanguage?.LanguageTag;

    /// <summary>
    /// True if Windows has a text recognition language for at least one language of the user profile (the settings show a
    /// hint otherwise, PLAN.md 11.3). Never throws.
    /// </summary>
    public static bool AnyLanguageAvailable()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string Recognize(Stream image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var engine = _engine ?? throw new InvalidOperationException("No text recognition language is installed.");
        cancellationToken.ThrowIfCancellationRequested();

        // Copied into memory first: the decoder then never reads the file again, and the stream is closed by the caller.
        using var buffer = new MemoryStream();
        image.CopyTo(buffer);
        buffer.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            SoftwareBitmap bitmap;
            try
            {
                using var stream = buffer.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).GetAwaiter().GetResult();
                uint width = decoder.OrientedPixelWidth;
                uint height = decoder.OrientedPixelHeight;
                if (width == 0 || height == 0)
                {
                    throw new InvalidDataException("The image has no pixels.");
                }

                var transform = new BitmapTransform();
                uint max = OcrEngine.MaxImageDimension;
                if (width > max || height > max)
                {
                    double scale = Math.Min((double)max / width, (double)max / height);
                    transform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale));
                    transform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
                    transform.InterpolationMode = BitmapInterpolationMode.Fant;
                }

                bitmap = decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                        ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)
                    .AsTask(cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
            {
                throw new InvalidDataException("The image cannot be decoded.", ex);
            }

            using (bitmap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = engine.RecognizeAsync(bitmap).AsTask(cancellationToken).GetAwaiter().GetResult();
                var text = new StringBuilder();
                foreach (var line in result.Lines)
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(line.Text);
                }

                return text.ToString();
            }
        }
    }
}

/// <summary>Known folders of Windows that the program needs.</summary>
public static class KnownFolders
{
    private static readonly Guid ScreenshotsId = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private const uint DontVerify = 0x0000_4000;

    /// <summary>
    /// The Windows Screenshots folder (<c>FOLDERID_Screenshots</c>) wherever it is redirected, also to OneDrive; null if
    /// Windows does not know it. The folder may not exist yet (no screenshot taken so far).
    /// </summary>
    public static string? Screenshots()
    {
        try
        {
            int hr = SHGetKnownFolderPath(ScreenshotsId, DontVerify, 0, out nint path);
            try
            {
                return hr == 0 && path != 0 ? Marshal.PtrToStringUni(path) : null;
            }
            finally
            {
                if (path != 0)
                {
                    Marshal.FreeCoTaskMem(path);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
#pragma warning restore SYSLIB1054
}
