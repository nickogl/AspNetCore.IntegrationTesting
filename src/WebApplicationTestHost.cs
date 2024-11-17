using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace Nickogl.AspNetCore.IntegrationTesting;

/// <summary>
/// Host ASP.NET Core web applications in the test process.
/// </summary>
/// <remarks>
/// This class is thread-safe so long the <see cref="StartAsync"/> and <see cref="StopAsync"/>
/// methods are not used. If a test needs to use these methods to test startup
/// or shutdown behavior, it should create its separate instance of the class.
/// </remarks>
/// <typeparam name="TEntryPoint">Entrypoint of the web application under test.</typeparam>
public class WebApplicationTestHost<TEntryPoint> : IDisposable, IAsyncDisposable where TEntryPoint : class
{
	/// <summary>Maximum number of times to retry restarting the web application.</summary>
	/// <remarks>Set this to a higher value if you have a massive amount of concurrent tests.</remarks>
	public static int MaximumRestartRetries { get; set; } = 100;

	/// <summary>Safety timeout when shutting down the application.</summary>
	/// <remarks>Set this to a higher value if your hosted services have lengthy stopping tasks.</remarks>
	public static TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

	private readonly object _syncRoot = new();
	private readonly string _environment;
	private Action<IWebHostBuilder>? _configureWebHostBuilder;
	private Action<IApplicationBuilder>? _configureApplicationBuilder;
	private WebApplicationFactoryWrapper? _webApplicationFactoryWrapper;
	private int? _previousPort;
	private bool _disposed;

	/// <summary>
	/// Create a new web application instance. This does not yet bootstrap it.
	/// </summary>
	/// <param name="environment">ASP.NET Core environment to use for this web application.</param>
	public WebApplicationTestHost(string? environment = null)
	{
		_environment = environment ?? Environments.Development;
	}

	/// <summary>Get the base address of this web application on the local machine.</summary>
	/// <remarks>Calling this causes the web application to be bootstrapped, if it was not already.</remarks>
	public virtual Uri BaseAddress
	{
		get
		{
			var server = EnsureWebApplication().Host.Services.GetRequiredService<IServer>();
			var addressesFeature = server.Features.Get<IServerAddressesFeature>()
				?? throw new WebApplicationBootstrappingException("Could not allocate a port");
			var address = addressesFeature.Addresses.FirstOrDefault()
				?? throw new WebApplicationBootstrappingException("Could not allocate a port");
			var uriBuilder = new UriBuilder(address) { Host = System.Net.Dns.GetHostName() };
			return uriBuilder.Uri;
		}
	}

	/// <summary>Get the services configured for this web application.</summary>
	/// <remarks>Calling this causes the web application to be bootstrapped, if it was not already.</remarks>
	public virtual IServiceProvider Services => EnsureWebApplication().Host.Services;

	/// <summary>
	/// Configure the web host prior to bootstrapping the application. This can
	/// be called multiple times and will perform configuration in order of calls.
	/// </summary>
	/// <remarks>
	/// To perform background tasks as soon as the app starts, add instances of
	/// <see cref="IHostedService"/> to the DI container.</remarks>
	/// <param name="configure">Delegate used to configure the web host builder.</param>
	/// <returns>A reference to this object to chain method calls.</returns>
	public virtual WebApplicationTestHost<TEntryPoint> ConfigureWebHost(Action<IWebHostBuilder> configure)
	{
		var previous = _configureWebHostBuilder;
		_configureWebHostBuilder = builder =>
		{
			previous?.Invoke(builder);
			configure(builder);
		};

		return this;
	}

	/// <summary>
	/// Configure the application prior to bootstrapping the application. This can
	/// be called multiple times and will perform configuration in order of calls.
	/// </summary>
	/// <remarks>This allows you to add middleware and endpoints among other things.</remarks>
	/// <param name="configure">Delegate used to configure the application builder.</param>
	/// <returns>A reference to this object to chain method calls.</returns>
	public virtual WebApplicationTestHost<TEntryPoint> ConfigureApplication(Action<IApplicationBuilder> configure)
	{
		var previous = _configureApplicationBuilder;
		_configureApplicationBuilder = app =>
		{
			previous?.Invoke(app);
			configure(app);
		};

		return this;
	}

	/// <summary>
	/// Convenience wrapper for <see cref="ConfigureWebHost"/> to configure the DI
	/// container prior to bootstrapping the application. This can be called multiple
	/// times and will perform configuration in order of calls.
	/// </summary>
	/// <param name="configure">Delegate used to configure the DI container.</param>
	/// <returns>A reference to this object to chain method calls.</returns>
	public virtual WebApplicationTestHost<TEntryPoint> ConfigureServices(Action<IServiceCollection> configure)
	{
		return ConfigureWebHost(builder =>
		{
			builder.ConfigureServices(configure);
		});
	}

	/// <summary>
	/// Convenience wrapper for <see cref="ConfigureWebHost"/> to configure options
	/// prior to bootstrapping the application. This can be called multiple times
	/// and will perform configuration in order of calls.
	/// </summary>
	/// <param name="configure">Delegate used to configure the options.</param>
	/// <returns>A reference to this object to chain method calls.</returns>
	public virtual WebApplicationTestHost<TEntryPoint> ConfigureOptions<TOptions>(Action<TOptions> configure) where TOptions : class
	{
		return ConfigureServices(services =>
		{
			services.Configure(configure);
		});
	}

	/// <summary>
	/// Start the web application. Usually calling this is not necessary, unless
	/// the web application was stopped previously. This method should not be used
	/// when multiple concurrent tests use the same web application instance.
	/// </summary>
	/// <remarks>You can use this to test application behavior upon restart, e.g. persistence features.</remarks>
	/// <param name="cancellationToken">Cancellation token to cancel startup.</param>
	public virtual async Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_webApplicationFactoryWrapper != null || _previousPort == null)
		{
			throw new InvalidOperationException("Web application is already running");
		}

		// Restart the web application and bind it to the same port as before. This
		// ensures we are not breaking existing clients connecting to the application.
		// Another test could bind to this port during the restart, so we retry until
		// it succeeds. Not a good solution but suffices for now.
		int retries = 0;
		while (true)
		{
			try
			{
				_webApplicationFactoryWrapper = new WebApplicationFactoryWrapper(this) { RequestedPort = _previousPort.Value };
				await _webApplicationFactoryWrapper.Host.StartAsync(cancellationToken).ConfigureAwait(false);
				AssertWebApplicationStarted(_webApplicationFactoryWrapper.Host);
				return;
			}
			catch (Exception)
			{
				if (retries++ <= MaximumRestartRetries)
				{
					throw;
				}
			}
		}
	}

	/// <summary>
	/// Gracefully stop the web application. This method should not be used when
	/// multiple concurrent tests use the same web application instance.
	/// </summary>
	/// <remarks>You can use this to test application behavior upon shutdown, e.g. persistence features.</remarks>
	/// <param name="terminationToken">Cancellation token to trigger a forceful termination of the web application.</param>
	public virtual async Task StopAsync(CancellationToken terminationToken = default)
	{
		if (_webApplicationFactoryWrapper == null)
		{
			throw new InvalidOperationException("Web application is not yet running");
		}

		_previousPort = BaseAddress.Port;

		// We are not using Host.StopAsync because it causes hosted services to be stopped
		// twice for all Microsoft.AspNetCore.Builder.WebApplication-based applications.
		// This is because it triggers a cancellation token that causes WaitForShutdownAsync
		// (inside RunAsync) to unblock, after which it calls StopAsync again.
		await StopApplication(terminationToken).ConfigureAwait(false);

		// HACK: The disposal of the host (and with it the service collection) only
		// happens after ApplicationStopped was triggered. To ensure stable and reproducible
		// tests, we have to return from this method only after all services have been
		// disposed of. There are no cancellation tokens or diagnostic events that
		// trigger upon disposal to my knowledge, so wait until the service collection
		// throws an ObjectDisposedException.
		var cts = CancellationTokenSource.CreateLinkedTokenSource(terminationToken);
		cts.CancelAfter(TimeSpan.FromMilliseconds(500));
		await WaitForDisposal(cts.Token).ConfigureAwait(false);

		await _webApplicationFactoryWrapper.DisposeAsync().ConfigureAwait(false);
		_webApplicationFactoryWrapper = null;
	}

	public virtual void Dispose()
	{
		if (!_disposed)
		{
			DisposeAsync()
				.AsTask()
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();
		}

		GC.SuppressFinalize(this);
	}

	public virtual async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		if (_webApplicationFactoryWrapper != null)
		{
			try
			{
				var cts = new CancellationTokenSource(delay: ShutdownTimeout);
				await StopApplication(cts.Token).ConfigureAwait(false);
				await WaitForDisposal(cts.Token).ConfigureAwait(false);
			}
			catch (Exception)
			{
			}

			await _webApplicationFactoryWrapper.DisposeAsync().ConfigureAwait(false);
		}

		_disposed = true;

		GC.SuppressFinalize(this);
	}

	private WebApplicationFactoryWrapper EnsureWebApplication()
	{
		lock (_syncRoot)
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(nameof(WebApplicationTestHost<TEntryPoint>));
			}

			if (_webApplicationFactoryWrapper != null)
			{
				return _webApplicationFactoryWrapper;
			}

			_webApplicationFactoryWrapper = new WebApplicationFactoryWrapper(this);
			_webApplicationFactoryWrapper.Host.Start();
			AssertWebApplicationStarted(_webApplicationFactoryWrapper.Host);
			return _webApplicationFactoryWrapper;
		}
	}

	private static void AssertWebApplicationStarted(IHost host)
	{
		try
		{
			// NOTE: Accessing the host's service collection will throw an ObjectDisposedException if startup failed
			var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
			if (!lifetime.ApplicationStarted.IsCancellationRequested)
			{
				throw new WebApplicationBootstrappingException("Web application did not start up");
			}
		}
		catch (ObjectDisposedException)
		{
			throw new WebApplicationBootstrappingException("Web application crashed during startup, perhaps your configuration is wrong or you need to run an external dependency");
		}
	}

	private async Task StopApplication(CancellationToken terminationToken = default)
	{
		Debug.Assert(_webApplicationFactoryWrapper != null);

		var tcs = new TaskCompletionSource();
		var lifetime = _webApplicationFactoryWrapper.Host.Services.GetRequiredService<IHostApplicationLifetime>();
		lifetime.ApplicationStopped.Register(() => tcs.TrySetResult());
		lifetime.StopApplication();

		try
		{
			await tcs.Task.WaitAsync(ShutdownTimeout, terminationToken).ConfigureAwait(false);
		}
		catch (Exception)
		{
			try
			{
				// If stopping did not complete in the allotted time, forcefully terminate the application
				var cts = new CancellationTokenSource();
				cts.Cancel();
				await _webApplicationFactoryWrapper.Host.StopAsync(cts.Token).ConfigureAwait(false);
			}
			catch (Exception)
			{
			}
		}
	}

	private async Task WaitForDisposal(CancellationToken cancellationToken = default)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				_ = _webApplicationFactoryWrapper!.Host.Services.GetRequiredService<IHostApplicationLifetime>();
				await Task.Delay(1, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception e) when (e is OperationCanceledException || e is ObjectDisposedException)
		{
		}
	}

	private sealed class WebApplicationFactoryWrapper : WebApplicationFactory<TEntryPoint>
	{
		private readonly WebApplicationTestHost<TEntryPoint> _parent;
		private IHost? _host;

		/// <summary>Which port to use for the web application. By default, chooses any available one.</summary>
		public int RequestedPort { get; init; }

		/// <summary>Get the underlying host, bootstrapping the web application if needed.</summary>
		public IHost Host => EnsureHost();

		public WebApplicationFactoryWrapper(WebApplicationTestHost<TEntryPoint> parent)
		{
			_parent = parent;
		}

		public override ValueTask DisposeAsync()
		{
			// Our Kestrel host is not owned by the underlying WebApplicationFactory,
			// so we need to dispose of it ourselves
			_host?.Dispose();

			return base.DisposeAsync();
		}

		protected override IHost CreateHost(IHostBuilder builder)
		{
			// Allocate real port instead of using the built-in test server, as this allows
			// consumers outside of the test to communicate with it and in general provide
			// more control over the service's lifetime
			builder.ConfigureWebHost(options => options.UseKestrel(server => server.ListenAnyIP(RequestedPort)));
			builder.UseEnvironment(_parent._environment);
			_host = builder.Build();

			// Create dummy test host so internal logic of WebApplicationFactory is satisfied
			builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
			builder.ConfigureWebHost(options =>
			{
				// Suppress the default behavior, otherwise we would start all hosted services
				// twice, which can be quite expensive depending on the application
				options.UseTestServer();
				options.UseStartup<NoopStartup>();
			});
			var testHost = builder.Build();
			testHost.Start();
			return testHost;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			// Hook into the entrypoint before WebApplicationBuilder.Build() is called
			_parent._configureWebHostBuilder?.Invoke(builder);

			// Hook into the entrypoint after WebApplicationBuilder.Build() was called but before WebApplication.Run() is called
			if (_parent._configureApplicationBuilder != null)
			{
				builder.ConfigureServices(services =>
				{
					services.AddTransient<IStartupFilter, StartupFilter>(_ => new(_parent._configureApplicationBuilder));
				});
			}
		}

		private IHost EnsureHost()
		{
			if (_host != null)
			{
				return _host;
			}

			// This will cause the web application to be bootstrapped
			using var _ = CreateDefaultClient();
			if (_host == null)
			{
				throw new WebApplicationBootstrappingException("CreateHost(IHostBuilder builder) was not called");
			}

			return _host;
		}

		private sealed class StartupFilter : IStartupFilter
		{
			private readonly Action<IApplicationBuilder> _configureApplication;

			public StartupFilter(Action<IApplicationBuilder> configureApplication)
			{
				_configureApplication = configureApplication;
			}

			public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
			{
				return builder =>
				{
					_configureApplication(builder);
					next(builder);
				};
			}
		}

		private sealed class NoopStartup
		{
#pragma warning disable CA1822
			public void Configure()
			{
			}
#pragma warning restore CA1822
		}
	}
}
