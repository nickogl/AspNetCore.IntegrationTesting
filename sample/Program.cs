using Sample;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<SampleOptions>().Bind(builder.Configuration.GetSection(SampleOptions.SectionKey));
builder.Services.AddSingleton<ISampleService, SampleService>();
builder.Services.AddSingleton<PersistentStorage>();
builder.Services.AddHostedService(services => services.GetRequiredService<PersistentStorage>());

var app = builder.Build();
var persistentStorage = app.Services.GetRequiredService<PersistentStorage>();
var sampleService = app.Services.GetRequiredService<ISampleService>();
app.MapGet("/text", async httpContext =>
{
	await httpContext.Response.WriteAsync(persistentStorage.Text, httpContext.RequestAborted);
});
app.MapPost("/text", async httpContext =>
{
	using var reader = new StreamReader(httpContext.Request.Body);
	persistentStorage.Text = await reader.ReadToEndAsync();
});
app.MapGet("/sample", async httpContext =>
{
	await httpContext.Response.WriteAsync(await sampleService.FetchResource());
});
app.Run();

// Expose entry point to test project
namespace Sample
{
	public partial class Program { }
}
