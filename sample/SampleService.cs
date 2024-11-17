
namespace Sample;

public sealed class SampleService : ISampleService
{
	public async Task<string> FetchResource()
	{
		await Task.Delay(TimeSpan.FromSeconds(1));
		return "original";
	}
}
