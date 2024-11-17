using Microsoft.AspNetCore.Mvc.IntegrationTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;

namespace Sample.Test;

[TestClass]
public class SampleTests
{
	[TestMethod]
	public async Task ShouldOverrideOption()
	{
		using var app = new WebApplicationTestHost<Program>();
		app.ConfigureOptions<SampleOptions>(options => options.Text = "bar");
		using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };

		var response = await httpClient.GetAsync("/text");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual("bar", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task ShouldPersistText()
	{
		using var textFile = new TemporaryFile();
		using var app = new WebApplicationTestHost<Program>();
		app.ConfigureOptions<SampleOptions>(options => options.PersistedTextFilePath = textFile.Path);
		using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };

		await httpClient.PostAsync("/text", new StringContent("bar"));
		await app.StopAsync();
		await app.StartAsync();
		var response = await httpClient.GetAsync("/text");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual("bar", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task ShouldReplaceService()
	{
		using var app = new WebApplicationTestHost<Program>();
		app.ConfigureServices(services =>
		{
			services.RemoveAll<ISampleService>();
			services.AddSingleton<ISampleService, SampleServiceMock>();
		});
		using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };

		var response = await httpClient.GetAsync("/sample");

		Assert.AreEqual("mocked", await response.Content.ReadAsStringAsync());
	}

	// Assert that we fix https://github.com/dotnet/aspnetcore/issues/40271
	[TestMethod]
	public async Task ShouldStopAndDisposeOnlyOnce()
	{
		using var app = new WebApplicationTestHost<Program>();
		app.ConfigureServices(services =>
		{
			services.RemoveAll<ISampleService>();
			services.AddSingleton<ISampleService, SampleServiceMock>();
		});
		using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };
		var hostedService = app.Services.GetRequiredService<PersistentStorage>();

		await app.StopAsync();

		Assert.AreEqual(1, hostedService.StoppedCount);
		Assert.AreEqual(1, hostedService.DisposedCount);
	}

	private sealed class SampleServiceMock : ISampleService
	{
		public Task<string> FetchResource()
		{
			return Task.FromResult("mocked");
		}
	}

	private sealed class TemporaryFile : IDisposable
	{
		public string Path { get; }

		public TemporaryFile()
		{
			Path = System.IO.Path.GetTempFileName();
		}

		public void Dispose()
		{
			File.Delete(Path);
		}
	}
}
