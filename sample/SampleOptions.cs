namespace Sample;

public sealed class SampleOptions
{
	public const string SectionKey = "Sample";

	public string Text { get; set; } = string.Empty;
	public string? PersistedTextFilePath { get; set; }
}
