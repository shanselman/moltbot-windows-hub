using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Inference;
using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// The WinUI-free core of the App composition root. Registers the presentation-layer
/// infrastructure and the App-owned singletons that view models depend on.
/// </summary>
/// <remarks>
/// Ownership rules encoded here:
/// <list type="bullet">
/// <item>App-owned singletons (<see cref="IUiDispatcher"/>, <see cref="IAppCommands"/>,
/// <see cref="SettingsManager"/>) are registered as <b>pre-built instances</b>, so the
/// container never disposes them — App remains their sole owner (no double-dispose).</item>
/// <item><see cref="NavigationScopeManager"/> is a container-created singleton, so the
/// container disposes it when the root provider is disposed.</item>
/// <item>Page view models are transient and are resolved from a per-navigation scope,
/// so they are disposed when navigation moves away.</item>
/// </list>
/// WinUI-bound registrations (the dispatcher/page-activator/navigation adapters) are
/// added separately by App so this method stays unit-testable in a pure net10 project.
/// </remarks>
internal static class AppServiceRegistration
{
    public static IServiceCollection AddOpenClawTrayCore(this IServiceCollection services, AppServiceContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        // App-owned singletons: pre-built instances → container does not dispose them.
        services.AddSingleton(context.Dispatcher);
        services.AddSingleton(context.AppCommands);
        services.AddSingleton(context.Settings);
        services.AddSingleton(context.ExecApprovalsStore);
        services.AddSingleton(context.PermissionsRuntimeHost);
        if (context.LocalAiRuntime is not null)
            services.AddSingleton(context.LocalAiRuntime);
        // Settings facade over the App-owned SettingsManager. Container-owned so it can dispose
        // its Saved-event subscription during shutdown.
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IPermissionsPageRuntimeSource, PermissionsPageRuntimeSource>();
        services.AddSingleton<IHostHardwareProbe, CudaHostHardwareProbe>();

        // Container-owned navigation lifetime manager (disposed with the root provider).
        services.AddSingleton<NavigationScopeManager>();

        // Stateless per-host-mount composer session factory. Depends only on the
        // App-owned dispatcher instance above; starts no background work.
        services.AddSingleton<IChatComposerFactory, ChatComposerFactory>();

        // Transient page view models resolved per navigation scope.
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<PermissionsPageViewModel>();
        if (context.LocalAiRuntime is not null)
            services.AddTransient<LocalAiPageViewModel>();

        return services;
    }
}
