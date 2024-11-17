# ASP.NET Core Integration Testing

This library hosts ASP.NET Core web applications in the test process, making tests
and the application under test easy to debug and allowing each of the tests to
configure the web application differently.

Unlike WebApplicationFactory from the `Microsoft.AspNetCore.Mvc.Testing` package,
this library hosts the web applications with Kestrel, allowing one to make real
HTTP and websocket requests against the application. It is thus ideal for orchestrating
integration tests of various components.

## Installation

```
dotnet add package Nickogl.AspNetCore.IntegrationTesting
```

## Features

- Stop and restart application, which can be useful for testing persistence features, for example
- Hook into each stage of the entry point
    - Before `WebApplicationBuilder.Build()`:
        - Configure options
        - Configure services
        - Everything else supported by `IWebHostBuilder`
    - After `WebApplicationBuilder.Build()` and before `WebApplication.Run()`:
        - Add middleware
        - Add endpoints
        - Everything else supported by `IApplicationBuilder`

## Sample

Full code can be found [here](sample-test/SampleTests.cs).

### Configure options

```csharp
using var app = new WebApplicationTestHost<Program>();
app.ConfigureOptions<SampleOptions>(options => options.Text = "bar");
using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };

var response = await httpClient.GetAsync("/text");

Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
Assert.AreEqual("bar", await response.Content.ReadAsStringAsync());
```

### Stop and restart application

```csharp
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
```

### Configure services

```csharp
using var app = new WebApplicationTestHost<Program>();
app.ConfigureServices(services =>
{
    services.RemoveAll<ISampleService>();
    services.AddSingleton<ISampleService, SampleServiceMock>();
});
using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };

var response = await httpClient.GetAsync("/sample");

Assert.AreEqual("mocked", await response.Content.ReadAsStringAsync());
```

### Add middleware

```csharp
int observedRequests = 0;
using var app = new WebApplicationTestHost<Program>();
app.ConfigureApplication(app =>
{
    app.Use((next) =>
    {
        return async httpContext =>
        {
            await next(httpContext);
            Interlocked.Increment(ref observedRequests);
        };
    });
});
using var httpClient = new HttpClient() { BaseAddress = app.BaseAddress };

await httpClient.GetAsync("/text");

Assert.AreEqual(1, observedRequests);
```
