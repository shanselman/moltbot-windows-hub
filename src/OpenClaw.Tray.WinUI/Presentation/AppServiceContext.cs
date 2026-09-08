using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Services;
using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClawTray.Presentation;

/// <summary>
/// Carries the already-constructed, App-owned singletons that the composition root
/// registers as pre-built instances. Registering them as instances (rather than
/// letting the container construct them) means the DI container never disposes them,
/// so App keeps sole ownership of their lifetime and there is no double-dispose.
/// </summary>
internal sealed class AppServiceContext
{
    public AppServiceContext(
        IUiDispatcher dispatcher,
        IAppCommands appCommands,
        SettingsManager settings,
        IExecApprovalsPresentationStore execApprovalsStore,
        IPermissionsPageRuntimeHost permissionsRuntimeHost,
        ILocalAiRuntime? localAiRuntime = null,
        Func<IOperatorGatewayClient?>? gatewayClientAccessor = null,
        Func<IReadOnlyList<string>>? agentIdsAccessor = null,
        Func<string, string>? resourceAccessor = null,
        Func<string, object?[], string>? resourceFormatter = null,
        IGatewayConnectionManager? gatewayConnectionManager = null)
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        AppCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ExecApprovalsStore = execApprovalsStore ?? throw new ArgumentNullException(nameof(execApprovalsStore));
        PermissionsRuntimeHost = permissionsRuntimeHost ?? throw new ArgumentNullException(nameof(permissionsRuntimeHost));
        LocalAiRuntime = localAiRuntime;
        GatewayClientAccessor = gatewayClientAccessor;
        AgentIdsAccessor = agentIdsAccessor;
        ResourceAccessor = resourceAccessor;
        ResourceFormatter = resourceFormatter;
        GatewayConnectionManager = gatewayConnectionManager;
    }

    public IUiDispatcher Dispatcher { get; }
    public IAppCommands AppCommands { get; }
    public SettingsManager Settings { get; }
    public IExecApprovalsPresentationStore ExecApprovalsStore { get; }
    public IPermissionsPageRuntimeHost PermissionsRuntimeHost { get; }
    public ILocalAiRuntime? LocalAiRuntime { get; }
    public Func<IOperatorGatewayClient?>? GatewayClientAccessor { get; }
    public Func<IReadOnlyList<string>>? AgentIdsAccessor { get; }
    public Func<string, string>? ResourceAccessor { get; }
    public Func<string, object?[], string>? ResourceFormatter { get; }
    public IGatewayConnectionManager? GatewayConnectionManager { get; }
}
