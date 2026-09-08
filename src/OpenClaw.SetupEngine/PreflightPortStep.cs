using System.Net;
using System.Net.Sockets;
using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

public sealed class PreflightPortStep : SetupStep
{
    public override string Id => "preflight-port";
    public override string DisplayName => "Check gateway port available";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var port = ctx.Config.GatewayPort;
        var addresses = ctx.Config.Gateway.Bind.Equals("lan", StringComparison.OrdinalIgnoreCase)
            ? new[] { IPAddress.Any, IPAddress.IPv6Any }
            : [IPAddress.Loopback];

        // Poll briefly in case WSL port forwarding proxy hasn't fully released the
        // port yet after targeted distro termination in a prior cleanup step.
        await WaitForPortFreeAsync(port, ctx.Config.Gateway.Bind, ctx.Logger, ct, maxWaitSeconds: 10);

        foreach (var address in addresses)
        {
            if (!CanBind(address, port, out var error))
            {
                var owners = string.Join(", ", WindowsTcpListenerSnapshot.Capture().Listeners
                    .Where(listener => listener.Port == port && listener.Address.AddressFamily == address.AddressFamily)
                    .Select(listener => listener.ProcessName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());
                return StepResult.Fail($"Port {port} is already in use for {DescribeBind(address)} ({error.SocketErrorCode})" +
                    (owners.Length > 0 ? $". Owning process: {owners}." : ""));
            }
        }

        return StepResult.Ok($"Port {port} is available");
    }

    /// <summary>
    /// Polls until all required addresses for <paramref name="port"/> can be bound,
    /// or until <paramref name="maxWaitSeconds"/> elapses.  Silently returns if the
    /// port never frees — <see cref="ExecuteAsync"/> will still hard-fail in that case.
    /// </summary>
    internal static async Task WaitForPortFreeAsync(
        int port, string bind, SetupLogger logger, CancellationToken ct,
        int maxWaitSeconds = 20)
    {
        var addresses = bind.Equals("lan", StringComparison.OrdinalIgnoreCase)
            ? new[] { IPAddress.Any, IPAddress.IPv6Any }
            : [IPAddress.Loopback];

        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (addresses.All(a => CanBind(a, port, out _)))
            {
                if (attempt > 0)
                    logger.Info($"Port {port} became free after {attempt * 500}ms");
                return;
            }

            attempt++;
            await Task.Delay(500, ct);
        }

        logger.Warn($"Port {port} still in use after {maxWaitSeconds}s poll — proceeding to hard check");
    }

    internal static bool CanBind(IPAddress address, int port, out SocketException error)
    {
        var listener = new TcpListener(address, port)
        {
            ExclusiveAddressUse = true
        };

        try
        {
            listener.Start();
            error = null!;
            return true;
        }
        catch (SocketException ex)
        {
            error = ex;
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string DescribeBind(IPAddress address)
        => address.Equals(IPAddress.Any) ? "LAN IPv4 bind" :
           address.Equals(IPAddress.IPv6Any) ? "LAN IPv6 bind" :
           "loopback bind";
}
