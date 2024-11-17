using Microsoft.Extensions.Options;

namespace Sample;

public sealed class PersistentStorage : IHostedService, IDisposable
{
	private readonly IOptions<SampleOptions> _options;
	private int _disposed;

	public string Text { get; set; }
	public int DisposedCount;
	public int StoppedCount;

	public PersistentStorage(IOptions<SampleOptions> options)
	{
		_options = options;
		Text = options.Value.Text;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		if (_options.Value.PersistedTextFilePath != null)
		{
			try
			{
				Text = await File.ReadAllTextAsync(_options.Value.PersistedTextFilePath, cancellationToken);
			}
			catch (Exception)
			{
			}
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		if (_options.Value.PersistedTextFilePath != null)
		{
			await File.WriteAllTextAsync(_options.Value.PersistedTextFilePath, Text, cancellationToken);
		}

		Interlocked.Increment(ref StoppedCount);
	}

	public void Dispose()
	{
		// We added this service as a singleton and also a hosted service, so the host
		// counts it as two IDisposable instances. We therefore have to ensure ourselves
		// that we dispose of this service only once.
		if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
		{
			Interlocked.Increment(ref DisposedCount);
		}
	}
}
