using System.IO;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Inference;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral guard for the composition root. Locks build-time validation, singleton
/// identity of App-owned instances, transient page-view-model lifetime, and that the
/// container never disposes App-owned pre-built instances while it does dispose what it
/// created.
/// </summary>
public sealed class AppServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(
        out RecordingUiDispatcher dispatcher,
        out FakeAppCommands commands,
        out SettingsManager settings,
        out ExecApprovalsStore execApprovalsStore,
        out FakePermissionsPageRuntimeHost runtimeHost,
        out TempDir temp)
    {
        temp = new TempDir();
        dispatcher = new RecordingUiDispatcher();
        commands = new FakeAppCommands();
        settings = new SettingsManager(temp.Path);
        execApprovalsStore = new ExecApprovalsStore(temp.Path, NullLogger.Instance);
        runtimeHost = new FakePermissionsPageRuntimeHost();

        var services = new ServiceCollection();
        services.AddOpenClawTrayCore(new AppServiceContext(
            dispatcher,
            commands,
            settings,
            execApprovalsStore,
            runtimeHost));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void Build_ValidatesOnBuild_WithoutThrowing()
    {
        var provider = BuildProvider(out _, out _, out _, out _, out _, out var temp);
        using (provider)
        using (temp)
        {
            Assert.NotNull(provider);
        }
    }

    [Fact]
    public void AppOwnedSingletons_ResolveToTheProvidedInstances()
    {
        var provider = BuildProvider(out var dispatcher, out var commands, out var settings, out var execApprovalsStore, out var runtimeHost, out var temp);
        using (provider)
        using (temp)
        {
            Assert.Same(dispatcher, provider.GetRequiredService<IUiDispatcher>());
            Assert.Same(commands, provider.GetRequiredService<IAppCommands>());
            Assert.Same(settings, provider.GetRequiredService<SettingsManager>());
            Assert.Same(execApprovalsStore, provider.GetRequiredService<IExecApprovalsPresentationStore>());
            Assert.Same(runtimeHost, provider.GetRequiredService<IPermissionsPageRuntimeHost>());
            Assert.IsType<CudaHostHardwareProbe>(provider.GetRequiredService<IHostHardwareProbe>());
            Assert.Same(
                provider.GetRequiredService<IHostHardwareProbe>(),
                provider.GetRequiredService<IHostHardwareProbe>());
            Assert.Same(provider.GetRequiredService<ISettingsStore>(), provider.GetRequiredService<ISettingsStore>());
            Assert.Same(provider.GetRequiredService<IPermissionsPageRuntimeSource>(), provider.GetRequiredService<IPermissionsPageRuntimeSource>());
        }
    }

    [Fact]
    public void ChatComposerFactory_IsRegisteredAsAStatelessSingleton()
    {
        var provider = BuildProvider(out _, out _, out _, out _, out _, out var temp);
        using (provider)
        using (temp)
        {
            var first = provider.GetRequiredService<IChatComposerFactory>();
            var second = provider.GetRequiredService<IChatComposerFactory>();

            Assert.Same(first, second);
            Assert.IsType<ChatComposerFactory>(first);
        }
    }

    [Fact]
    public void ChatComposerFactory_MissingRegistration_GetRequiredServiceThrowsInsteadOfReturningNull()
    {
        // Proves the fail-fast property the host-composition call sites rely on:
        // if IChatComposerFactory were ever NOT registered (a genuine composition
        // bug), GetRequiredService throws rather than silently yielding null, which
        // is what lets ChatPage/ChatWindow surface the failure through the app's
        // existing unhandled-exception/crash-log path instead of conflating it with
        // the "disconnected, no provider yet" placeholder.
        var temp = new TempDir();
        using (temp)
        {
            var dispatcher = new RecordingUiDispatcher();
            var commands = new FakeAppCommands();
            var settings = new SettingsManager(temp.Path);
            var execApprovalsStore = new ExecApprovalsStore(temp.Path, NullLogger.Instance);
            var runtimeHost = new FakePermissionsPageRuntimeHost();
            var services = new ServiceCollection();
            services.AddOpenClawTrayCore(new AppServiceContext(
                dispatcher,
                commands,
                settings,
                execApprovalsStore,
                runtimeHost));

            // Simulate the registration being absent by building a container that
            // only removes this one registration, keeping everything else intact.
            IServiceCollection withoutFactory = new ServiceCollection();
            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType != typeof(IChatComposerFactory))
                    withoutFactory.Add(descriptor);
            }

            using var provider = withoutFactory.BuildServiceProvider();

            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChatComposerFactory>());
        }
    }

    [Fact]
    public void PageViewModels_AreTransient_AndReceiveInjectedServices()
    {
        var provider = BuildProvider(out var dispatcher, out var commands, out _, out var execApprovalsStore, out _, out var temp);
        using (provider)
        using (temp)
        using (var scope = provider.CreateScope())
        {
            var first = scope.ServiceProvider.GetRequiredService<PermissionsPageViewModel>();
            var second = scope.ServiceProvider.GetRequiredService<PermissionsPageViewModel>();

            Assert.NotSame(first, second);
            Assert.Same(dispatcher, first.Dispatcher);
            Assert.Same(commands, first.AppCommands);
            Assert.Same(scope.ServiceProvider.GetRequiredService<ISettingsStore>(), first.SettingsStore);
            Assert.Same(execApprovalsStore, first.ExecApprovalsStore);
            Assert.Same(scope.ServiceProvider.GetRequiredService<IPermissionsPageRuntimeSource>(), first.RuntimeSource);
        }
    }

    [Fact]
    public void PageViewModel_ResolvedFromScope_IsDisposedWithScope()
    {
        var provider = BuildProvider(out _, out _, out _, out _, out _, out var temp);
        using (provider)
        using (temp)
        {
            PermissionsPageViewModel vm;
            using (var scope = provider.CreateScope())
            {
                vm = scope.ServiceProvider.GetRequiredService<PermissionsPageViewModel>();
                Assert.False(vm.IsDisposed);
            }

            Assert.True(vm.IsDisposed);
        }
    }

    [Fact]
    public void Dispose_DoesNotDisposeAppOwnedInstanceSingletons()
    {
        var provider = BuildProvider(out var dispatcher, out var commands, out _, out _, out _, out var temp);
        using (temp)
        {
            var manager = provider.GetRequiredService<NavigationScopeManager>();

            provider.Dispose();

            Assert.False(dispatcher.Disposed);
            Assert.False(commands.Disposed);
            Assert.True(manager.IsDisposed);
        }
    }

    [Fact]
    public void ResolvingExecApprovalsStore_IsPure_AndSingleton()
    {
        var provider = BuildProvider(out _, out _, out _, out var execApprovalsStore, out _, out var temp);
        using (provider)
        using (temp)
        {
            var first = provider.GetRequiredService<IExecApprovalsPresentationStore>();
            var second = provider.GetRequiredService<IExecApprovalsPresentationStore>();

            Assert.Same(execApprovalsStore, first);
            Assert.Same(first, second);
            Assert.False(File.Exists(ExecApprovalsStore.ResolveFilePath(temp.Path)));
        }
    }
}
