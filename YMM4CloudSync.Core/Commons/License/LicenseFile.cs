namespace YMM4CloudSync.Core.Commons.License;

public sealed class LicenseFile(string name, string text)
{
    public string Name { get; } = name;
    public string Text { get; } = text;
}