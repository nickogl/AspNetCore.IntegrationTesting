namespace Nickogl.AspNetCore.IntegrationTesting;

/// <summary>
/// Represents errors that occur during web application bootstrapping.
/// </summary>
public sealed class WebApplicationBootstrappingException : Exception
{
	public WebApplicationBootstrappingException(string? message) : base(message)
	{
	}

	public WebApplicationBootstrappingException(string? message, Exception? innerException) : base(message, innerException)
	{
	}
}
