using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public readonly record struct ReconnectAuthorizationResult(
    bool Allowed,
    GatewayErrorKind FailureKind = GatewayErrorKind.Unknown,
    string? Detail = null)
{
    public static ReconnectAuthorizationResult AllowedResult { get; } = new(true);
}

internal enum HandshakeChallengeState
{
    Idle,
    Active,
    Authorized,
    Blocked,
}

internal sealed class HandshakeChallengeGate
{
    private readonly object _lock = new();
    private long _generation;
    private HandshakeChallengeState _state;

    public void Reset(long generation)
    {
        lock (_lock)
        {
            _generation = generation;
            _state = HandshakeChallengeState.Idle;
        }
    }

    public bool TryBegin(long generation)
    {
        lock (_lock)
        {
            if (_generation != generation)
                return false;

            if (_state != HandshakeChallengeState.Idle)
                return false;

            _state = HandshakeChallengeState.Active;
            return true;
        }
    }

    public bool TryAuthorize(long generation)
    {
        lock (_lock)
        {
            if (_generation != generation || _state != HandshakeChallengeState.Active)
                return false;

            _state = HandshakeChallengeState.Authorized;
            return true;
        }
    }

    public bool TryBlock(long generation)
    {
        lock (_lock)
        {
            if (_generation != generation ||
                _state is not (HandshakeChallengeState.Active or HandshakeChallengeState.Authorized))
                return false;

            _state = HandshakeChallengeState.Blocked;
            return true;
        }
    }

    public bool IsAuthorized(long generation)
    {
        lock (_lock)
        {
            return _generation == generation &&
                _state == HandshakeChallengeState.Authorized;
        }
    }
}

/// <summary>
/// Abstract base class for WebSocket-based gateway clients.
/// Extracts shared connection lifecycle: connect, listen, reconnect, send, dispose.
/// Subclasses implement message processing and provide configuration via abstract members.
/// </summary>
public abstract class WebSocketClientBase : IDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly object _connectionStateLock = new();
    private readonly string _gatewayUrl;
    private readonly string? _credentials;
    private CancellationTokenSource _cts;
    private bool _disposed;
    private int _reconnectAttempts;
    private int _reconnectLoopActive;
    private long _connectionGeneration;
    private int _remoteCloseStatusCode = -1;
    private string? _remoteCloseStatusDescription;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };
    private static readonly TimeSpan DefaultConnectAttemptTimeout = TimeSpan.FromSeconds(15);

    protected readonly string _token;
    protected readonly IOpenClawLogger _logger;

    /// <summary>Gateway URL with credentials stripped, safe for logging/display.</summary>
    protected string GatewayUrlForDisplay { get; }

    /// <summary>Whether Dispose has been called.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>Whether the WebSocket is currently open and connected.</summary>
    protected bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>Cancellation token tied to this client's lifetime.</summary>
    protected CancellationToken CancellationToken => _cts.Token;

    /// <summary>Monotonic identity for the current transport connection.</summary>
    protected long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    /// <summary>Close status from the current connection's server-originated close frame.</summary>
    protected int? RemoteCloseStatusCode
    {
        get
        {
            var code = Volatile.Read(ref _remoteCloseStatusCode);
            return code >= 0 ? code : null;
        }
    }

    /// <summary>Close description from the current connection's server-originated close frame.</summary>
    protected string? RemoteCloseStatusDescription =>
        Volatile.Read(ref _remoteCloseStatusDescription);

    /// <summary>Identifies the transport attempt currently owned by this client.</summary>
    protected long CurrentConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<string>? AuthenticationFailed;
    public event EventHandler? Disposed;
    /// <summary>
    /// Optional fail-closed authorization invoked immediately before every client-owned reconnect.
    /// Managed-local callers use it to re-check endpoint provenance and explicit user intent.
    /// </summary>
    public Func<CancellationToken, Task<ReconnectAuthorizationResult>>?
        ReconnectAuthorizationAsync { get; set; }

    /// <summary>Reset reconnect backoff counter. Call after successful application-level handshake.</summary>
    protected void ResetReconnectAttempts() => _reconnectAttempts = 0;

    /// <summary>Fire AuthenticationFailed event and stop auto-reconnect.</summary>
    protected void RaiseAuthenticationFailed(string message)
    {
        _logger.Warn($"{ClientRole} authentication failed: {message}");
        AuthenticationFailed?.Invoke(this, message);
    }

    // --- Abstract members (subclass MUST implement) ---

    /// <summary>
    /// Process a received WebSocket text message. Called from the listen loop.
    /// Gateway wraps its sync ProcessMessage with Task.CompletedTask;
    /// Node directly uses its async implementation.
    /// </summary>
    protected abstract Task ProcessMessageAsync(string json);

    /// <summary>
    /// Process a message attributed to the socket generation that received it.
    /// Override when message side effects or responses must remain bound to that socket.
    /// </summary>
    protected virtual Task ProcessMessageForConnectionAsync(
        string json,
        long sourceConnectionGeneration) =>
        ProcessMessageAsync(json);

    /// <summary>Receive buffer size in bytes. Gateway: 16384, Node: 65536.</summary>
    protected abstract int ReceiveBufferSize { get; }

    /// <summary>Client role for log messages, e.g. "gateway" or "node".</summary>
    protected abstract string ClientRole { get; }

    // --- Virtual hooks (subclass MAY override) ---

    /// <summary>Called after WebSocket connects, before the listen loop starts.</summary>
    protected virtual Task OnConnectedAsync() => Task.CompletedTask;

    /// <summary>Called when the server closes the connection or it drops.</summary>
    protected virtual void OnDisconnected() { }

    /// <summary>Called on unrecoverable listen-loop errors.</summary>
    protected virtual void OnError(Exception ex) { }

    /// <summary>Called at the start of Dispose, before CTS cancellation.</summary>
    protected virtual void OnDisposing() { }

    /// <summary>
    /// Whether auto-reconnect should run after an unexpected disconnect.
    /// Subclasses can return false for known terminal states (for example awaiting pairing approval).
    /// </summary>
    protected virtual bool ShouldAutoReconnect() => true;

    /// <summary>Maximum time allowed for one WebSocket HTTP upgrade attempt.</summary>
    protected virtual TimeSpan ConnectAttemptTimeout => DefaultConnectAttemptTimeout;

    protected WebSocketClientBase(string gatewayUrl, string token, IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrEmpty(gatewayUrl))
            throw new ArgumentException("Gateway URL is required.", nameof(gatewayUrl));
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("Token is required.", nameof(token));

        _gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        GatewayUrlForDisplay = GatewayUrlHelper.SanitizeForDisplay(_gatewayUrl);
        _token = token;
        _credentials = GatewayUrlHelper.ExtractCredentials(gatewayUrl);
        _logger = logger ?? NullLogger.Instance;
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync()
    {
        _ = await ConnectAsync(
            expectedSocket: null,
            expectedGeneration: 0).ConfigureAwait(false);
    }

    private async Task<(ClientWebSocket Socket, long Generation)?> ConnectAsync(
        ClientWebSocket? expectedSocket,
        long expectedGeneration)
    {
        if (_disposed)
        {
            _logger.Debug($"Skipping {ClientRole} connect: client already disposed");
            return null;
        }

        var connectGeneration = 0L;
        ClientWebSocket? ws = null;

        try
        {
            ws = new ClientWebSocket();
            if (!TryPublishConnection(
                    ws,
                    expectedSocket,
                    expectedGeneration,
                    out connectGeneration))
            {
                DisposeStaleSocket(ws);
                _logger.Debug($"{ClientRole} reconnect skipped: connection ownership changed");
                return null;
            }

            RaiseStatusChanged(ConnectionStatus.Connecting);
            _logger.Info($"Connecting to {ClientRole}: {GatewayUrlForDisplay}");

            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            // Set Origin header (convert ws/wss to http/https)
            var uri = new Uri(_gatewayUrl);
            var originScheme = uri.Scheme == "wss" ? "https" : "http";
            var origin = $"{originScheme}://{uri.Host}:{uri.Port}";
            ws.Options.SetRequestHeader("Origin", origin);

            if (!string.IsNullOrEmpty(_credentials))
            {
                var credentialsToEncode = GatewayUrlHelper.DecodeCredentials(_credentials);
                ws.Options.SetRequestHeader(
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentialsToEncode))}");
            }

            var connectTimeout = ConnectAttemptTimeout;
            using var connectCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connectCancellation.CancelAfter(connectTimeout);
            try
            {
                await ws.ConnectAsync(uri, connectCancellation.Token);
            }
            catch (OperationCanceledException ex) when (
                !_cts.Token.IsCancellationRequested &&
                connectCancellation.IsCancellationRequested)
            {
                try { ws.Abort(); }
                catch (Exception abortEx)
                {
                    _logger.Debug($"{ClientRole} timed-out WebSocket abort threw: {abortEx.Message}");
                }

                throw new TimeoutException(
                    $"{ClientRole} connect timed out after {connectTimeout.TotalSeconds:0.#}s.",
                    ex);
            }

            if (!IsCurrentConnection(ws, connectGeneration))
            {
                DisposeStaleSocket(ws);
                return null;
            }

            // Don't reset _reconnectAttempts here — TCP connect succeeding doesn't mean
            // auth will succeed. Reset only after the full application-level handshake
            // completes (subclass calls ResetReconnectAttempts after hello-ok).
            _logger.Info($"{ClientRole} connected, waiting for challenge...");

            await OnConnectedAsync();
            if (!IsCurrentConnection(ws, connectGeneration))
            {
                DisposeStaleSocket(ws);
                return null;
            }

            _ = Task.Run(() => ListenForMessagesAsync(ws, connectGeneration), _cts.Token);
            return (ws, connectGeneration);
        }
        catch (OperationCanceledException)
        {
            if (ws != null)
            {
                DisposeStaleSocket(ws);
            }
            _logger.Debug($"{ClientRole} connect canceled (likely shutdown)");
            return null;
        }
        catch (ObjectDisposedException)
        {
            if (ws != null)
            {
                DisposeStaleSocket(ws);
            }
            _logger.Debug($"{ClientRole} connect aborted after dispose");
            return null;
        }
        catch (Exception ex)
        {
            if (ws != null && !IsCurrentConnection(ws, connectGeneration))
            {
                DisposeStaleSocket(ws);
                _logger.Debug($"{ClientRole} stale connection failure ignored: {ex.Message}");
                return null;
            }

            if (ws != null)
            {
                DisposeStaleSocket(ws);
            }
            if (ex is TimeoutException)
            {
                _logger.Warn(ex.Message);
            }
            _logger.Error($"{ClientRole} connection failed", ex);
            OnConnectionException(ex);
            RaiseStatusChanged(ConnectionStatus.Error);

            if (!_disposed && !_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
            {
                _ = ReconnectWithBackoffAsync(ws, connectGeneration);
            }
            return ws is null ? null : (ws, connectGeneration);
        }
    }

    /// <summary>
    /// Lets a concrete client preserve a typed transport failure before the generic status event is
    /// raised. The base class deliberately does not expose exception text to consumers.
    /// </summary>
    protected virtual void OnConnectionException(Exception exception)
    {
    }

    private bool IsCurrentConnection(ClientWebSocket ws, long generation) =>
        !_disposed
        && Interlocked.Read(ref _connectionGeneration) == generation
        && ReferenceEquals(_webSocket, ws);

    private bool TryPublishConnection(
        ClientWebSocket ws,
        ClientWebSocket? expectedSocket,
        long expectedGeneration,
        out long connectGeneration)
    {
        lock (_connectionStateLock)
        {
            connectGeneration = 0;
            if (_disposed ||
                !IsReconnectOwnerLocked(expectedSocket, expectedGeneration))
            {
                return false;
            }

            connectGeneration = Interlocked.Increment(ref _connectionGeneration);
            Volatile.Write(ref _remoteCloseStatusCode, -1);
            Volatile.Write(ref _remoteCloseStatusDescription, null);
            _webSocket = ws;
            return true;
        }
    }

    private void DisposeStaleSocket(ClientWebSocket ws)
    {
        lock (_connectionStateLock)
        {
            if (ReferenceEquals(_webSocket, ws))
            {
                _webSocket = null;
            }
        }

        // slopwatch-ignore: SW003 Cleanup is best-effort for superseded sockets.
        try { ws.Dispose(); } catch { }
    }

    /// <summary>
    /// Aborts the current transport while retaining reconnect ownership for its listen loop.
    /// Use when a socket-specific trust check fails and only a fresh socket may retry.
    /// </summary>
    protected bool IsCurrentConnectionGeneration(long expectedGeneration) =>
        !_disposed && Interlocked.Read(ref _connectionGeneration) == expectedGeneration;

    protected void AbortCurrentWebSocket(long expectedGeneration)
    {
        var ws = _webSocket;
        if (ws is null ||
            !IsCurrentConnectionGeneration(expectedGeneration) ||
            !ReferenceEquals(_webSocket, ws))
            return;

        try { ws.Abort(); }
        catch (Exception ex) { _logger.Debug($"{ClientRole} WebSocket abort threw: {ex.Message}"); }
    }

    // Cap on a single accumulated inbound message. A peer that streams an unbounded multi-frame text
    // message (never setting EndOfMessage) would otherwise grow the StringBuilder without limit —
    // a memory-exhaustion DoS (CWE-770 / CWE-400). 32M UTF-16 chars (~64 MB) is generous for large
    // payloads (e.g. base64 attachments) yet bounded; on overflow the receive loop closes the socket.
    internal const int MaxInboundMessageChars = 32 * 1024 * 1024;

    // Appends a decoded frame to the accumulation buffer unless it would exceed the cap; returns
    // false (leaving sb unchanged) when the limit would be crossed, so the caller can close the socket.
    internal static bool TryAppendWithinLimit(StringBuilder sb, char[] chars, int count, int maxChars)
    {
        if ((long)sb.Length + count > maxChars) return false;
        sb.Append(chars, 0, count);
        return true;
    }

    private async Task ListenForMessagesAsync(ClientWebSocket ws, long connectionGeneration)
    {
        // Rent a pooled buffer — consistent with the SendRawAsync hot path; avoids a large
        // (16–64 KB) heap allocation per connection that would otherwise land on the LOH.
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var sb = new StringBuilder();

        try
        {
            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(
                    new ArraySegment<byte>(buffer, 0, ReceiveBufferSize), _cts.Token);
                if (!IsCurrentConnection(ws, connectionGeneration))
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage && sb.Length == 0)
                    {
                        // Fast path: single-frame message — decode directly, skip StringBuilder round-trip
                        await ProcessMessageForConnectionAsync(
                            Encoding.UTF8.GetString(buffer, 0, result.Count),
                            connectionGeneration);
                    }
                    else
                    {
                        // Multi-frame path: decode into a pooled char buffer and append to the
                        // StringBuilder directly, avoiding the intermediate string allocation that
                        // Encoding.UTF8.GetString would produce.
                        var maxCharCount = Encoding.UTF8.GetMaxCharCount(result.Count);
                        var charBuffer = ArrayPool<char>.Shared.Rent(maxCharCount);
                        bool withinLimit;
                        try
                        {
                            var charCount = Encoding.UTF8.GetChars(buffer, 0, result.Count, charBuffer, 0);
                            withinLimit = TryAppendWithinLimit(sb, charBuffer, charCount, MaxInboundMessageChars);
                        }
                        finally
                        {
                            ArrayPool<char>.Shared.Return(charBuffer);
                        }

                        if (!withinLimit)
                        {
                            _logger.Warn($"[{ClientRole}] inbound message exceeded {MaxInboundMessageChars} chars; closing connection (memory-exhaustion guard)");
                            try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None); }
                            catch { /* best-effort close */ }
                            break;
                        }

                        if (result.EndOfMessage)
                        {
                            await ProcessMessageForConnectionAsync(
                                sb.ToString(),
                                connectionGeneration);
                            sb.Clear();
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeStatus = result.CloseStatus?.ToString() ?? "unknown";
                    var closeDesc = result.CloseStatusDescription ?? "no description";
                    _logger.Info($"Server closed connection: {closeStatus} - {closeDesc}");
                    if (IsCurrentConnection(ws, connectionGeneration))
                    {
                        Volatile.Write(
                            ref _remoteCloseStatusCode,
                            result.CloseStatus is null ? -1 : (int)result.CloseStatus.Value);
                        Volatile.Write(
                            ref _remoteCloseStatusDescription,
                            result.CloseStatusDescription);
                        OnDisconnected();
                        RaiseStatusChanged(ConnectionStatus.Disconnected);
                    }
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.Warn("Connection closed prematurely");
            if (IsCurrentConnection(ws, connectionGeneration))
            {
                OnDisconnected();
                RaiseStatusChanged(ConnectionStatus.Disconnected);
            }
        }
        catch (OperationCanceledException) { /* Expected on shutdown/disconnect. */ }
        catch (ObjectDisposedException) { /* CTS or WebSocket disposed during shutdown */ }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} listen error", ex);
            if (IsCurrentConnection(ws, connectionGeneration))
            {
                OnError(ex);
                RaiseStatusChanged(ConnectionStatus.Error);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Auto-reconnect if not intentionally disposed
        if (IsCurrentConnection(ws, connectionGeneration))
        {
            try
            {
                if (!_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
                {
                    await ReconnectWithBackoffAsync(ws, connectionGeneration);
                }
            }
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            catch (ObjectDisposedException) { /* CTS disposed during check */ }
        }
    }

    protected async Task ReconnectWithBackoffAsync(
        ClientWebSocket? expectedSocket = null,
        long expectedGeneration = 0)
    {
        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
        {
            return;
        }

        var ownerSocket = expectedSocket;
        var ownerGeneration = expectedGeneration;
        try
        {
            while (!_disposed
                && !_cts.Token.IsCancellationRequested
                && ShouldAutoReconnect()
                && IsReconnectOwner(ownerSocket, ownerGeneration))
            {
                var delay = BackoffMs[Math.Min(_reconnectAttempts, BackoffMs.Length - 1)];
                // Add 0-25% jitter to prevent thundering herd when multiple clients
                // (operator + node) reconnect on the same schedule
                var jitter = Random.Shared.Next(0, delay / 4);
                delay += jitter;
                _reconnectAttempts++;
                _logger.Warn($"{ClientRole} reconnecting in {delay}ms (attempt {_reconnectAttempts})");
                RaiseStatusChanged(ConnectionStatus.Connecting);

                await Task.Delay(delay, _cts.Token);

                if (_cts.Token.IsCancellationRequested
                    || _disposed
                    || !ShouldAutoReconnect()
                    || !IsReconnectOwner(ownerSocket, ownerGeneration))
                {
                    break;
                }

                if (ReconnectAuthorizationAsync is not null)
                {
                    var authorization =
                        await ReconnectAuthorizationAsync(_cts.Token).ConfigureAwait(false);
                    if (_cts.Token.IsCancellationRequested
                        || _disposed
                        || !ShouldAutoReconnect()
                        || !IsReconnectOwner(ownerSocket, ownerGeneration))
                    {
                        break;
                    }

                    if (!authorization.Allowed)
                    {
                        _logger.Warn(
                            $"{ClientRole} reconnect blocked by endpoint authorization policy: " +
                            (authorization.Detail ?? authorization.FailureKind.ToString()));
                        OnReconnectAuthorizationDenied(authorization);
                        RaiseStatusChanged(ConnectionStatus.Error);
                        break;
                    }
                }

                // Safely dispose old socket
                var oldSocket = ownerSocket ?? _webSocket;
                if (oldSocket != null)
                {
                    DisposeStaleSocket(oldSocket);
                }

                var currentSocket = _webSocket;
                if (currentSocket != null
                    && !ReferenceEquals(currentSocket, oldSocket)
                    && IsSocketClosingOrClosed(currentSocket))
                {
                    DisposeStaleSocket(currentSocket);
                }

                var attempt = await ConnectAsync(
                    ownerSocket,
                    ownerGeneration).ConfigureAwait(false);
                if (attempt is null)
                {
                    if (IsReconnectOwner(ownerSocket, ownerGeneration))
                    {
                        continue;
                    }

                    break;
                }

                ownerSocket = attempt.Value.Socket;
                ownerGeneration = attempt.Value.Generation;

                if (IsConnected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* Reconnect loop canceled during shutdown — expected. */ }
        catch (ObjectDisposedException) { /* CTS disposed mid-loop during shutdown — expected. */ }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} reconnect failed", ex);
            RaiseStatusChanged(ConnectionStatus.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectLoopActive, 0);
        }
    }

    protected virtual void OnReconnectAuthorizationDenied(
        ReconnectAuthorizationResult authorization)
    {
    }

    private bool IsReconnectOwner(ClientWebSocket? expectedSocket, long expectedGeneration)
    {
        lock (_connectionStateLock)
        {
            return IsReconnectOwnerLocked(expectedSocket, expectedGeneration);
        }
    }

    private bool IsReconnectOwnerLocked(
        ClientWebSocket? expectedSocket,
        long expectedGeneration)
    {
        if (expectedSocket is null && expectedGeneration == 0)
            return true;

        if (Interlocked.Read(ref _connectionGeneration) == expectedGeneration &&
            (expectedSocket is null ||
             _webSocket is null ||
             ReferenceEquals(_webSocket, expectedSocket)))
        {
            return true;
        }

        // A newer open socket owns recovery. A null or closing socket can be adopted because its
        // listener cannot start another reconnect loop while this loop holds the single-flight gate.
        var currentSocket = _webSocket;
        return currentSocket is null || IsSocketClosingOrClosed(currentSocket);
    }

    private static bool IsSocketClosingOrClosed(ClientWebSocket ws) =>
        ws.State is WebSocketState.CloseReceived
            or WebSocketState.CloseSent
            or WebSocketState.Closed
            or WebSocketState.Aborted;

    /// <summary>Send a text message over the WebSocket. Thread-safe.</summary>
    protected virtual async Task SendRawAsync(string message)
    {
        try
        {
            await _sendLock.WaitAsync(_cts.Token);
        }

        catch (OperationCanceledException)
        {
            // Shutdown canceled the wait; drop the send silently.
            return;
        }
        catch (ObjectDisposedException)
        {
            // Send lock disposed mid-wait during shutdown.
            return;
        }

        try
        {
            // Serialize sends; reconnect/dispose can still close the captured socket,
            // so the send below keeps the existing state-change guards.
            var ws = _webSocket;
            if (ws?.State != WebSocketState.Open) return;

            try
            {
                // Rent a pooled buffer to avoid per-send heap allocations on the hot send path.
                var byteCount = Encoding.UTF8.GetByteCount(message);
                var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    var written = Encoding.UTF8.GetBytes(message, buffer);
                    await ws.SendAsync(buffer.AsMemory(0, written),
                        WebSocketMessageType.Text, true, _cts.Token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                // Shutdown/reconnect canceled an in-flight send.
            }
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            catch (ObjectDisposedException)
            {
                // WebSocket was disposed between state check and send.
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
            {
                _logger.Warn($"WebSocket send failed (state changed): {ex.Message}");
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends only when the captured socket generation still owns the transport immediately before
    /// the write. Used for credential-bearing handshake frames.
    /// </summary>
    protected virtual async Task<bool> SendRawAsync(
        string message,
        long expectedConnectionGeneration,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token,
            cancellationToken);
        try
        {
            await _sendLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            var ws = _webSocket;
            if (ws?.State != WebSocketState.Open ||
                !IsCurrentConnection(ws, expectedConnectionGeneration))
            {
                return false;
            }

            var byteCount = Encoding.UTF8.GetByteCount(message);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.UTF8.GetBytes(message, buffer);
                await ws.SendAsync(
                        buffer.AsMemory(0, written),
                        WebSocketMessageType.Text,
                        true,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                return IsCurrentConnection(ws, expectedConnectionGeneration);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
            {
                _logger.Warn($"WebSocket send failed (state changed): {ex.Message}");
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Gracefully close the WebSocket connection.</summary>
    protected async Task CloseWebSocketAsync()
    {
        var ws = _webSocket;
        if (ws?.State != WebSocketState.Open)
            return;

        try
        {
            await _sendLock.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown canceled the wait; no close ownership was acquired.
            return;
        }
        catch (ObjectDisposedException)
        {
            // Send lock or lifetime token was disposed during shutdown.
            return;
        }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                // Preserve normal graceful close; concurrent Dispose aborts the socket and callers contain that failure.
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Disconnecting",
                    CancellationToken.None);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Fire the StatusChanged event. Use this instead of directly invoking the event.</summary>
    protected void RaiseStatusChanged(ConnectionStatus status)
        => StatusChanged?.Invoke(this, status);

    public void Dispose()
    {
        lock (_connectionStateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        OnDisposing();

        ClientWebSocket? ws;
        lock (_connectionStateLock)
        {
            Interlocked.Increment(ref _connectionGeneration);
            ws = _webSocket;
            _webSocket = null;
        }

        try { _cts.Cancel(); }
        catch (Exception ex) { _logger.Debug($"{ClientRole} cts.Cancel during Dispose threw: {ex.Message}"); }

        try { ws?.Dispose(); }
        catch (Exception ex) { _logger.Debug($"{ClientRole} WebSocket Dispose threw: {ex.Message}"); }

        // Don't dispose _cts immediately — listen loop or reconnect may still reference it.
        // It will be GC'd after all pending tasks complete.
        try { Disposed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Debug($"{ClientRole} Disposed handler threw: {ex.Message}"); }
    }
}
