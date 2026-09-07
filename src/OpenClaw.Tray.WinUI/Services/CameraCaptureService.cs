using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace OpenClawTray.Services;

/// <summary>
/// Camera capture service using Windows.Media.Capture
/// </summary>
public class CameraCaptureService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly CaptureOperationGate _captureGate = new();
    
    public CameraCaptureService(IOpenClawLogger logger)
    {
        _logger = logger;
    }
    
    public void Dispose()
    {
        _captureGate.Dispose();
    }
    
    public async Task<CameraInfo[]> ListCamerasAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation
            .FindAllAsync(DeviceClass.VideoCapture)
            .AsTask(cancellationToken);
        var result = new List<CameraInfo>();
        
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            result.Add(new CameraInfo
            {
                DeviceId = device.Id,
                Name = device.Name,
                IsDefault = i == 0
            });
        }
        
        return result.ToArray();
    }
    
    public async Task<CameraSnapResult> SnapAsync(
        CameraSnapArgs args,
        CancellationToken cancellationToken)
    {
        _logger.Info($"camera.snap start: deviceId={args.DeviceId ?? "(default)"}, format={args.Format}, maxWidth={args.MaxWidth}, quality={args.Quality}");
        using var captureLease = await _captureGate.EnterAsync(cancellationToken);
        
        try
        {
            var format = NormalizeFormat(args.Format);
            var deviceId = await ResolveVideoDeviceIdAsync(args.DeviceId, cancellationToken);
            _logger.Info($"camera.snap: initializing MediaCapture (format={format})");
            using var capture = new MediaCapture();
            
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Auto
            };
            
            var initStart = DateTime.UtcNow;
            await capture.InitializeAsync(settings).AsTask(cancellationToken);
            _logger.Info($"camera.snap: MediaCapture initialized in {(DateTime.UtcNow - initStart).TotalMilliseconds:0}ms");
            
            var photoCandidates = SelectPhotoEncodings(capture, format, args.MaxWidth);
            if (photoCandidates.Count == 0)
            {
                _logger.Warn("camera.snap: no photo stream properties available; falling back to video frame");
                return await CaptureVideoFallbackAsync(
                    capture, format, args.MaxWidth, args.Quality, cancellationToken);
            }
            
            using var stream = new InMemoryRandomAccessStream();
            _logger.Info($"camera.snap: preferred encoding {photoCandidates[0].Subtype} {photoCandidates[0].Width}x{photoCandidates[0].Height}");
            
            var captureStart = DateTime.UtcNow;
            var encoding = await CaptureWithFallbackAsync(
                capture, photoCandidates, cancellationToken);
            if (encoding == null)
            {
                _logger.Warn("camera.snap: no supported photo encodings; falling back to video frame");
                return await CaptureVideoFallbackAsync(
                    capture, format, args.MaxWidth, args.Quality, cancellationToken);
            }
            
            try
            {
                await capture.CapturePhotoToStreamAsync(encoding, stream).AsTask(cancellationToken);
            }
            catch (Exception ex) when (IsInvalidMediaType(ex))
            {
                _logger.Warn("camera.snap: photo capture unsupported; falling back to video frame");
                return await CaptureVideoFallbackAsync(
                    capture, format, args.MaxWidth, args.Quality, cancellationToken);
            }
            _logger.Info($"camera.snap: CapturePhotoToStreamAsync completed in {(DateTime.UtcNow - captureStart).TotalMilliseconds:0}ms");
            
            stream.Seek(0);
            var encodeStart = DateTime.UtcNow;
            var result = await EncodeAsync(
                stream, format, args.MaxWidth, args.Quality, cancellationToken);
            _logger.Info($"camera.snap: encoded {result.Width}x{result.Height} ({result.Base64.Length} chars) in {(DateTime.UtcNow - encodeStart).TotalMilliseconds:0}ms");
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error("Camera access denied. Check Windows privacy settings.", ex);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"camera.snap failed (0x{ex.HResult:X8})", ex);
            throw;
        }
    }
    
    public async Task<CameraClipResult> ClipAsync(
        CameraClipArgs args,
        CancellationToken cancellationToken)
    {
        _logger.Info($"camera.clip start: deviceId={args.DeviceId ?? "(default)"}, durationMs={args.DurationMs}, includeAudio={args.IncludeAudio}, format={args.Format}");
        using var captureLease = await _captureGate.EnterAsync(cancellationToken);
        MediaCapture? capture = null;
        var recordStartRequested = false;
        var recordingStopped = false;
        
        try
        {
            var deviceId = await ResolveVideoDeviceIdAsync(args.DeviceId, cancellationToken);
            capture = new MediaCapture();
            
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                MemoryPreference = MediaCaptureMemoryPreference.Auto,
                StreamingCaptureMode = args.IncludeAudio
                    ? StreamingCaptureMode.AudioAndVideo
                    : StreamingCaptureMode.Video
            };
            
            var initStart = DateTime.UtcNow;
            await capture.InitializeAsync(settings).AsTask(cancellationToken);
            _logger.Info($"camera.clip: MediaCapture initialized in {(DateTime.UtcNow - initStart).TotalMilliseconds:0}ms");

            var recordProperties = await TryConfigureVideoRecordStreamAsync(
                capture, cancellationToken);
            using var stream = new InMemoryRandomAccessStream();
            var profile = CreateClipProfile(args.IncludeAudio, recordProperties);
            
            var recordStart = DateTime.UtcNow;
            recordStartRequested = true;
            await capture.StartRecordToStreamAsync(profile, stream).AsTask(cancellationToken);
            _logger.Info($"camera.clip: recording started");
            
            await Task.Delay(args.DurationMs, cancellationToken);
            
            await capture.StopRecordAsync();
            recordingStopped = true;
            var elapsed = (DateTime.UtcNow - recordStart).TotalMilliseconds;
            _logger.Info($"camera.clip: recording stopped after {elapsed:0}ms");
            
            stream.Seek(0);
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
            var buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);
            var base64 = Convert.ToBase64String(buffer);
            
            _logger.Info($"camera.clip: encoded {base64.Length} chars");
            
            return new CameraClipResult
            {
                Format = args.Format,
                Base64 = base64,
                DurationMs = args.DurationMs,
                HasAudio = args.IncludeAudio
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error("Camera access denied. Check Windows privacy settings.", ex);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"camera.clip failed (0x{ex.HResult:X8})", ex);
            throw;
        }
        finally
        {
            if (capture != null && recordStartRequested && !recordingStopped)
            {
                try
                {
                    await capture.StopRecordAsync();
                }
                catch (Exception ex)
                {
                    _logger.Debug($"camera.clip cancellation cleanup failed: {ex.Message}");
                }
            }

            capture?.Dispose();
        }
    }

    private static async Task<string?> ResolveVideoDeviceIdAsync(
        string? requestedDeviceId,
        CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation
            .FindAllAsync(DeviceClass.VideoCapture)
            .AsTask(cancellationToken);

        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No camera devices found.");
        }

        if (string.IsNullOrWhiteSpace(requestedDeviceId))
        {
            return null;
        }

        var requestedDevice = devices.FirstOrDefault(
            device => string.Equals(device.Id, requestedDeviceId, StringComparison.Ordinal));
        if (requestedDevice == null)
        {
            throw new InvalidOperationException("Requested camera device was not found.");
        }

        return requestedDevice.Id;
    }
    
    private async Task<ImageEncodingProperties?> CaptureWithFallbackAsync(
        MediaCapture capture,
        List<ImageEncodingProperties> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                await capture.VideoDeviceController
                    .SetMediaStreamPropertiesAsync(MediaStreamType.Photo, candidate)
                    .AsTask(cancellationToken);
                _logger.Info($"camera.snap: using photo encoding {candidate.Subtype} {candidate.Width}x{candidate.Height}");
                return candidate;
            }
            catch (Exception ex) when (IsInvalidMediaType(ex))
            {
                _logger.Warn($"camera.snap: photo encoding {candidate.Subtype} {candidate.Width}x{candidate.Height} not supported");
            }
        }
        
        return null;
    }
    
    private static bool IsInvalidMediaType(Exception ex)
    {
        const int MfEInvalidMediaType = unchecked((int)0xC00D36B4);
        return ex.HResult == MfEInvalidMediaType;
    }

    private async Task<VideoEncodingProperties?> TryConfigureVideoRecordStreamAsync(
        MediaCapture capture,
        CancellationToken cancellationToken)
    {
        var props = capture.VideoDeviceController
            .GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)
            .OfType<VideoEncodingProperties>()
            .Where(p => p.Width > 0 && p.Height > 0)
            .ToList();

        if (props.Count == 0)
        {
            _logger.Warn("camera.clip: no video record stream properties available; using automatic MP4 profile");
            return null;
        }

        var bounded = props.Where(p => p.Width <= 1280 && p.Height <= 720).ToList();
        var candidates = (bounded.Count > 0 ? bounded : props)
            .OrderByDescending(p => p.Width)
            .ThenByDescending(p => p.Height)
            .ThenByDescending(p => p.Bitrate)
            .ToList();

        foreach (var candidate in candidates)
        {
            try
            {
                await capture.VideoDeviceController
                    .SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, candidate)
                    .AsTask(cancellationToken);
                _logger.Info($"camera.clip: using record stream {candidate.Subtype} {candidate.Width}x{candidate.Height}");
                return candidate;
            }
            catch (Exception ex) when (IsInvalidMediaType(ex))
            {
                _logger.Warn($"camera.clip: record stream {candidate.Subtype} {candidate.Width}x{candidate.Height} not supported");
            }
        }

        _logger.Warn("camera.clip: no compatible record stream properties accepted; using automatic MP4 profile");
        return null;
    }

    private static MediaEncodingProfile CreateClipProfile(bool includeAudio, VideoEncodingProperties? recordProperties)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);

        if (!includeAudio)
        {
            profile.Audio = null;
        }

        if (recordProperties != null)
        {
            profile.Video.Width = recordProperties.Width;
            profile.Video.Height = recordProperties.Height;

            if (recordProperties.Bitrate > 0)
            {
                profile.Video.Bitrate = recordProperties.Bitrate;
            }

            if (recordProperties.FrameRate.Numerator > 0 && recordProperties.FrameRate.Denominator > 0)
            {
                profile.Video.FrameRate.Numerator = recordProperties.FrameRate.Numerator;
                profile.Video.FrameRate.Denominator = recordProperties.FrameRate.Denominator;
            }
        }

        return profile;
    }

    private static List<ImageEncodingProperties> SelectPhotoEncodings(MediaCapture capture, string format, int maxWidth)
    {
        var props = capture.VideoDeviceController
            .GetAvailableMediaStreamProperties(MediaStreamType.Photo)
            .OfType<ImageEncodingProperties>()
            .ToList();
        
        if (props.Count == 0)
        {
            return new List<ImageEncodingProperties>();
        }
        
        string[] desired = format == "png"
            ? new[] { "PNG" }
            : new[] { "JPEG", "JPG", "MJPG" };
        
        var candidates = props
            .Where(p => desired.Contains(p.Subtype, StringComparer.OrdinalIgnoreCase))
            .ToList();
        
        if (candidates.Count == 0)
        {
            candidates = props;
        }
        
        var filtered = maxWidth > 0
            ? candidates.Where(p => p.Width <= maxWidth).ToList()
            : candidates;
        
        return (filtered.Count > 0 ? filtered : candidates)
            .OrderByDescending(p => p.Width)
            .ThenByDescending(p => p.Height)
            .ToList();
    }
    
    private static VideoEncodingProperties? SelectPreviewEncoding(MediaCapture capture, int maxWidth)
    {
        var props = capture.VideoDeviceController
            .GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
            .OfType<VideoEncodingProperties>()
            .ToList();
        
        if (props.Count == 0)
        {
            return null;
        }
        
        var filtered = maxWidth > 0
            ? props.Where(p => p.Width <= maxWidth).ToList()
            : props;
        
        return (filtered.Count > 0 ? filtered : props)
            .OrderByDescending(p => p.Width)
            .ThenByDescending(p => p.Height)
            .FirstOrDefault();
    }
    
    private async Task<CameraSnapResult> CaptureVideoFallbackAsync(
        MediaCapture capture,
        string format,
        int maxWidth,
        int quality,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureFrameReaderAsync(
                capture, format, maxWidth, quality, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"camera.snap: frame reader fallback failed (0x{ex.HResult:X8}): {ex.Message}");
        }
        
        _logger.Warn("camera.snap: frame reader unavailable; falling back to preview frame");
        return await CapturePreviewFrameAsync(
            capture, format, maxWidth, quality, cancellationToken);
    }
    
    private async Task<CameraSnapResult> CaptureFrameReaderAsync(
        MediaCapture capture,
        string format,
        int maxWidth,
        int quality,
        CancellationToken cancellationToken)
    {
        _logger.Info($"camera.snap: frame sources={capture.FrameSources.Count}");
        var sources = capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .OrderByDescending(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord ? 1 : 0)
            .ToList();
        var source = sources.FirstOrDefault();
        if (source == null)
        {
            throw new InvalidOperationException("No color frame source available");
        }
        
        MediaFrameReader? frameReader = null;
        var frameReaderStarted = false;
        try
        {
            var selectedFormat = SelectFrameReaderFormat(source, maxWidth);
            if (selectedFormat != null)
            {
                _logger.Info($"camera.snap: frame format {selectedFormat.Subtype} {selectedFormat.VideoFormat.Width}x{selectedFormat.VideoFormat.Height}");
                await source.SetFormatAsync(selectedFormat).AsTask(cancellationToken);
            }
            
            try
            {
                frameReader = selectedFormat != null
                    ? await capture.CreateFrameReaderAsync(source, selectedFormat.Subtype).AsTask(cancellationToken)
                    : await capture.CreateFrameReaderAsync(source).AsTask(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                frameReader = await capture.CreateFrameReaderAsync(source).AsTask(cancellationToken);
            }
            
            var tcs = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
            {
                if (tcs.Task.IsCompleted)
                {
                    return;
                }
                
                using var frame = sender.TryAcquireLatestFrame();
                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap == null)
                {
                    return;
                }
                
                var copied = SoftwareBitmap.Convert(bitmap, bitmap.BitmapPixelFormat, bitmap.BitmapAlphaMode);
                tcs.TrySetResult(copied);
            }
            
            frameReader.FrameArrived += OnFrameArrived;
            
            var status = await frameReader.StartAsync().AsTask(cancellationToken);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                throw new InvalidOperationException($"Frame reader start failed: {status}");
            }
            frameReaderStarted = true;
            
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            await using var reg = timeoutCts.Token.Register(
                () => tcs.TrySetCanceled(timeoutCts.Token));
            
            var bitmap = await tcs.Task;
            frameReader.FrameArrived -= OnFrameArrived;
            await frameReader.StopAsync();
            frameReaderStarted = false;
            
            return await EncodeSoftwareBitmapAsync(
                bitmap, format, maxWidth, quality, cancellationToken);
        }
        finally
        {
            if (frameReader != null && frameReaderStarted)
            {
                try
                {
                    await frameReader.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger.Debug($"camera.snap frame reader cleanup failed: {ex.Message}");
                }
            }

            frameReader?.Dispose();
        }
    }
    
    private static MediaFrameFormat? SelectFrameReaderFormat(MediaFrameSource source, int maxWidth)
    {
        var formats = source.SupportedFormats;
        if (formats.Count == 0)
        {
            return null;
        }
        
        var preferred = new[]
        {
            MediaEncodingSubtypes.Bgra8,
            MediaEncodingSubtypes.Nv12,
            MediaEncodingSubtypes.Yuy2,
            MediaEncodingSubtypes.Mjpg
        };
        
        foreach (var subtype in preferred)
        {
            var match = formats.FirstOrDefault(f =>
                string.Equals(f.Subtype, subtype, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }
        
        var filtered = maxWidth > 0
            ? formats.Where(f => f.VideoFormat.Width <= maxWidth).ToList()
            : formats.ToList();
        
        return (filtered.Count > 0 ? filtered : formats)
            .OrderByDescending(f => f.VideoFormat.Width)
            .ThenByDescending(f => f.VideoFormat.Height)
            .FirstOrDefault();
    }
    
    private async Task<CameraSnapResult> CapturePreviewFrameAsync(
        MediaCapture capture,
        string format,
        int maxWidth,
        int quality,
        CancellationToken cancellationToken)
    {
        var previewEncoding = SelectPreviewEncoding(capture, maxWidth);
        if (previewEncoding != null)
        {
            await capture.VideoDeviceController
                .SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, previewEncoding)
                .AsTask(cancellationToken);
            _logger.Info($"camera.snap: preview encoding {previewEncoding.Subtype} {previewEncoding.Width}x{previewEncoding.Height}");
        }
        else
        {
            _logger.Warn("camera.snap: no preview stream properties; using default preview settings");
        }
        
        var previewStartRequested = false;
        Exception? captureError = null;
        try
        {
            _logger.Info("camera.snap: starting preview");
            previewStartRequested = true;
            await capture.StartPreviewAsync().AsTask(cancellationToken);
            _logger.Info("camera.snap: preview started");

            var activePreview = capture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
            var width = (int)(activePreview?.Width ?? previewEncoding?.Width ?? 1280);
            var height = (int)(activePreview?.Height ?? previewEncoding?.Height ?? 720);
            if (width <= 0 || height <= 0)
            {
                width = 1280;
                height = 720;
            }
            
            var subtype = activePreview?.Subtype ?? previewEncoding?.Subtype;
            var pixelFormat = GetPreviewPixelFormat(subtype);
            _logger.Info($"camera.snap: grabbing preview frame {width}x{height} ({subtype ?? "default"})");
            using var frame = new VideoFrame(pixelFormat, width, height);
            await capture.GetPreviewFrameAsync(frame).AsTask(cancellationToken);
            var bitmap = frame.SoftwareBitmap ?? throw new InvalidOperationException("Preview frame missing bitmap");
            SoftwareBitmap? converted = null;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                bitmap = converted;
            }
            
            using var previewStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder
                .CreateAsync(BitmapEncoder.PngEncoderId, previewStream)
                .AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);
            previewStream.Seek(0);
            
            return await EncodeAsync(
                previewStream, format, maxWidth, quality, cancellationToken);
        }
        catch (Exception ex)
        {
            captureError = ex;
            throw;
        }
        finally
        {
            if (previewStartRequested)
            {
                _logger.Info("camera.snap: stopping preview");
                try
                {
                    await capture.StopPreviewAsync();
                }
                catch (Exception ex) when (
                    captureError != null || cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug($"camera.snap: preview stop cleanup failed: {ex.Message}");
                }
            }

            CaptureCancellationPolicy.ThrowIfCancellationRequested(
                captureError,
                cancellationToken);
        }
    }
    
    private async Task<CameraSnapResult> EncodeSoftwareBitmapAsync(
        SoftwareBitmap bitmap,
        string format,
        int maxWidth,
        int quality,
        CancellationToken cancellationToken)
    {
        SoftwareBitmap? converted = null;
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            bitmap = converted;
        }
        
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
        stream.Seek(0);
        
        return await EncodeAsync(stream, format, maxWidth, quality, cancellationToken);
    }
    
    private static BitmapPixelFormat GetPreviewPixelFormat(string? subtype)
    {
        if (string.Equals(subtype, MediaEncodingSubtypes.Yuy2, StringComparison.OrdinalIgnoreCase))
        {
            return BitmapPixelFormat.Yuy2;
        }
        
        if (string.Equals(subtype, MediaEncodingSubtypes.Nv12, StringComparison.OrdinalIgnoreCase))
        {
            return BitmapPixelFormat.Nv12;
        }
        
        return BitmapPixelFormat.Bgra8;
    }
    
    private static string NormalizeFormat(string? format)
    {
        var normalized = (format ?? "jpeg").Trim().ToLowerInvariant();
        if (normalized == "jpg") normalized = "jpeg";
        if (normalized != "jpeg" && normalized != "png") normalized = "jpeg";
        return normalized;
    }
    
    private static async Task<CameraSnapResult> EncodeAsync(
        IRandomAccessStream input,
        string format,
        int maxWidth,
        int quality,
        CancellationToken cancellationToken)
    {
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        
        uint targetWidth = width;
        uint targetHeight = height;
        if (maxWidth > 0 && width > maxWidth)
        {
            var scale = (double)maxWidth / width;
            targetWidth = (uint)maxWidth;
            targetHeight = (uint)Math.Max(1, Math.Round(height * scale));
        }
        
        var transform = new BitmapTransform
        {
            ScaledWidth = targetWidth,
            ScaledHeight = targetHeight
        };
        
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        
        using var output = new InMemoryRandomAccessStream();
        var encoderId = format == "png" ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;
        var encoder = await BitmapEncoder.CreateAsync(encoderId, output).AsTask(cancellationToken);
        
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            targetWidth,
            targetHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixelData.DetachPixelData());
        
        if (format == "jpeg")
        {
            var qualityValue = Math.Clamp(quality / 100.0, 0.0, 1.0);
            var props = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(qualityValue, PropertyType.Single) }
            };
            await encoder.BitmapProperties.SetPropertiesAsync(props).AsTask(cancellationToken);
        }
        
        await encoder.FlushAsync().AsTask(cancellationToken);
        output.Seek(0);
        
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output);
        await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken);
        reader.ReadBytes(bytes);
        
        return new CameraSnapResult
        {
            Format = format,
            Width = (int)targetWidth,
            Height = (int)targetHeight,
            Base64 = Convert.ToBase64String(bytes)
        };
    }
}
