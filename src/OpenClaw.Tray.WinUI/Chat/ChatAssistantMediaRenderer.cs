using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal static class ChatAssistantMediaRenderer
{
    public static Element Render(
        ChatAssistantMediaPresentation media,
        string? sessionKey,
        Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantMediaResolutionResult>>?
            resolver)
    {
        if (media.Kind == ChatMediaContentKind.Image
            && !string.IsNullOrWhiteSpace(sessionKey)
            && resolver is not null)
        {
            return Component<ChatAssistantImageCard, ChatAssistantImageCardProps>(
                new(media, sessionKey, resolver));
        }

        return BuildUnavailableCard(media);
    }

    internal static Element BuildUnavailableCard(
        ChatAssistantMediaPresentation media,
        bool preparing = false,
        Action? retry = null)
    {
        var kind = KindLabel(media.Kind);
        var displayName = DisplayName(media);
        var statusText = preparing
            ? LocalizedOrDefault("Chat_AssistantMedia_Preparing", $"Preparing {kind.ToLowerInvariant()}")
            : LocalizedOrDefault("Chat_AssistantMedia_Unavailable", $"{kind} unavailable");
        var detail = string.IsNullOrWhiteSpace(media.MimeType)
            ? statusText
            : $"{statusText} · {media.MimeType}";
        var accessibleName = $"{displayName}. {statusText}";

        var glyph = TextBlock(Glyph(media.Kind))
            .FontSize(18)
            .FontWeight(FontWeights.Normal)
            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
            .Set(text => text.FontFamily = FluentIconCatalog.SymbolThemeFontFamily)
            .Center();
        var glyphBackground = Border(glyph)
            .Size(36, 36)
            .CornerRadius(8)
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"));
        var title = TextBlock(displayName)
            .FontSize(13)
            .FontWeight(FontWeights.SemiBold)
            .Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
            .Set(text =>
            {
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
            })
            .MaxWidth(320);
        var status = TextBlock(detail)
            .FontSize(11)
            .FontWeight(FontWeights.Normal)
            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
            .Set(text =>
            {
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
            })
            .MaxWidth(320);
        var content = HStack(
            10,
            glyphBackground,
            VStack(2, title, status).VAlign(VerticalAlignment.Center));
        Element body = retry is null
            ? content
            : HStack(
                10,
                content,
                Button(
                        LocalizedOrDefault("Chat_AssistantMedia_Retry", "Retry"),
                        retry)
                    .AutomationName(
                        LocalizedOrDefault("Chat_AssistantMedia_Retry", "Retry")));

        return Border(body)
            .Padding(10, 8)
            .CornerRadius(10)
            .Background(Theme.Ref("SubtleFillColorTertiaryBrush"))
            .BorderBrush(Theme.Ref("ControlStrokeColorDefaultBrush"))
            .BorderThickness(1)
            .HAlign(HorizontalAlignment.Left)
            .AutomationName(accessibleName);
    }

    private static string KindLabel(ChatMediaContentKind kind) => kind switch
    {
        ChatMediaContentKind.Image => LocalizedOrDefault("Chat_AssistantMedia_Image", "Image"),
        ChatMediaContentKind.Audio => LocalizedOrDefault("Chat_AssistantMedia_Audio", "Audio"),
        ChatMediaContentKind.Video => LocalizedOrDefault("Chat_AssistantMedia_Video", "Video"),
        ChatMediaContentKind.File => LocalizedOrDefault("Chat_AssistantMedia_File", "File"),
        _ => LocalizedOrDefault("Chat_AssistantMedia_Media", "Media"),
    };

    internal static string DisplayName(ChatAssistantMediaPresentation media) =>
        string.IsNullOrWhiteSpace(media.DisplayName)
            ? KindLabel(media.Kind)
            : media.DisplayName;

    private static string Glyph(ChatMediaContentKind kind) => kind switch
    {
        ChatMediaContentKind.Image => "\uEB9F",
        ChatMediaContentKind.Audio => "\uE8D6",
        ChatMediaContentKind.Video => "\uE714",
        ChatMediaContentKind.File => "\uE8A5",
        _ => "\uE7C3",
    };

    internal static string LocalizedOrDefault(string key, string fallback)
    {
        var localized = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal)
            ? fallback
            : localized;
    }
}

internal sealed record ChatAssistantImageCardProps(
    ChatAssistantMediaPresentation Media,
    string SessionKey,
    Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantMediaResolutionResult>>
        Resolver);

internal sealed class ChatAssistantImageCard : Component<ChatAssistantImageCardProps>
{
    public override Element Render()
    {
        var props = Props;
        var (state, setState) = UseState<ChatAssistantImageLoadState?>(null, threadSafe: true);
        var (attempt, setAttempt) = UseState(0, threadSafe: true);
        var (viewerOpen, setViewerOpen) = UseState(false, threadSafe: true);

        UseEffect((Func<Action>)(() =>
        {
            var cancellation = new CancellationTokenSource();
            setViewerOpen(false);
            _ = LoadAsync(cancellation.Token);
            return () =>
            {
                cancellation.Cancel();
                cancellation.Dispose();
            };

            async Task LoadAsync(CancellationToken cancellationToken)
            {
                try
                {
                    var resolved = await props.Resolver(
                        props.SessionKey,
                        props.Media.Reference,
                        cancellationToken);
                    BitmapImage? bitmap = null;
                    if (resolved.Status == AssistantMediaResolutionStatus.Ready
                        && resolved.Data is { Length: > 0 } bytes)
                    {
                        bitmap = await TryDecodeBitmapAsync(bytes, cancellationToken);
                        if (bitmap is null)
                            resolved = AssistantMediaResolutionResult.Unavailable;
                    }
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        setState(new ChatAssistantImageLoadState(
                            resolved.Status,
                            bitmap,
                            props.SessionKey,
                            props.Media.Reference));
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Logger.Warn($"Assistant image load failed ({ex.GetType().Name}).");
                    setState(new ChatAssistantImageLoadState(
                        AssistantMediaResolutionStatus.Unavailable,
                        null,
                        props.SessionKey,
                        props.Media.Reference));
                }
            }
        }), props.SessionKey, props.Media.Reference, attempt);

        var currentState = state is not null
            && string.Equals(state.SessionKey, props.SessionKey, StringComparison.Ordinal)
            && ReferenceEquals(state.Reference, props.Media.Reference)
                ? state
                : null;
        if (currentState is
            { Status: AssistantMediaResolutionStatus.Ready, Bitmap: { } bitmap })
        {
            var image = BuildImage(props.Media, bitmap, () => setViewerOpen(true));
            var viewer = ContentDialog(
                props.Media.Alt ?? ChatAssistantMediaRenderer.DisplayName(props.Media),
                BuildViewerImage(props.Media, bitmap),
                ChatAssistantMediaRenderer.LocalizedOrDefault(
                    "Chat_AssistantMedia_Close",
                    "Close")) with
            {
                IsOpen = viewerOpen,
                OnClosed = _ => setViewerOpen(false),
            };
            return Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                image,
                viewer);
        }

        var preparing = currentState is null
            || currentState.Status == AssistantMediaResolutionStatus.Preparing;
        return ChatAssistantMediaRenderer.BuildUnavailableCard(
            props.Media,
            preparing,
            currentState is null ? null : () =>
            {
                setState(null);
                setAttempt(attempt + 1);
            });
    }

    private static Element BuildImage(
        ChatAssistantMediaPresentation media,
        BitmapImage bitmap,
        Action openViewer)
    {
        const double maximumWidth = 480;
        const double maximumHeight = 320;
        var pixelWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : (int)maximumWidth;
        var pixelHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : (int)maximumHeight;
        var scale = Math.Min(
            Math.Min(maximumWidth / pixelWidth, maximumHeight / pixelHeight),
            1.0);
        var preview = Border(Empty())
            .Background(new ImageBrush
            {
                ImageSource = bitmap,
                Stretch = Stretch.Uniform,
            })
            .Size(pixelWidth * scale, pixelHeight * scale)
            .CornerRadius(10)
            .AutomationName(media.Alt ?? ChatAssistantMediaRenderer.DisplayName(media));
        return Button(preview, openViewer)
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderThickness(0)
            .AutomationName(string.Format(
                ChatAssistantMediaRenderer.LocalizedOrDefault(
                    "Chat_AssistantMedia_OpenImage",
                    "Open image {0}"),
                ChatAssistantMediaRenderer.DisplayName(media)));
    }

    private static Element BuildViewerImage(
        ChatAssistantMediaPresentation media,
        BitmapImage bitmap)
    {
        const double maximumWidth = 1200;
        const double maximumHeight = 800;
        var pixelWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : (int)maximumWidth;
        var pixelHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : (int)maximumHeight;
        var scale = Math.Min(
            Math.Min(maximumWidth / pixelWidth, maximumHeight / pixelHeight),
            1.0);
        return ScrollViewer(
            Border(Empty())
                .Background(new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = Stretch.Uniform,
                })
                .Size(pixelWidth * scale, pixelHeight * scale)
                .AutomationName(media.Alt ?? ChatAssistantMediaRenderer.DisplayName(media)));
    }

    private static async Task<BitmapImage?> TryDecodeBitmapAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(cancellationToken);
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            if (!ChatAssistantImageDecodePolicy.TryGetDecodeSize(
                decoder.PixelWidth,
                decoder.PixelHeight,
                out var decodeWidth,
                out var decodeHeight))
            {
                return null;
            }
            stream.Seek(0);
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = decodeWidth,
                DecodePixelHeight = decodeHeight,
            };
            await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
            return bitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Assistant image decode failed ({ex.GetType().Name}).");
            return null;
        }
    }
}

internal sealed record ChatAssistantImageLoadState(
    AssistantMediaResolutionStatus Status,
    BitmapImage? Bitmap,
    string SessionKey,
    ChatMediaContentInfo Reference);

internal static class ChatAttachmentBitmapDecoder
{
    internal static BitmapImage? TryDecode(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            if (!ChatAssistantImageDecodePolicy.TryGetDecodeSize(
                    decoder.PixelWidth,
                    decoder.PixelHeight,
                    out var decodeWidth,
                    out var decodeHeight))
            {
                return null;
            }

            stream.Seek(0);
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = decodeWidth,
                DecodePixelHeight = decodeHeight,
            };
            bitmap.SetSource(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Attachment image decode failed ({ex.GetType().Name}).");
            return null;
        }
    }
}
