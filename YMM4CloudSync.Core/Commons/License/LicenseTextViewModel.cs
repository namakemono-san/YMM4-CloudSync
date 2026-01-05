namespace YMM4CloudSync.Core.Commons.License;

public sealed class LicenseTextViewModel(LicenseFile source)
{
    public string Name { get; } = source.Name;
    public string Text { get; } = source.Text;
}