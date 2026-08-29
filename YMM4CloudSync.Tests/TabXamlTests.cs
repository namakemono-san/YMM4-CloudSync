using System.Windows.Controls;
using Xunit;
using Xunit.Sdk;
using YMM4CloudSync.Core.Views.Tabs;

namespace YMM4CloudSync.Tests;

public class TabXamlTests
{
    private static void OnStaThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new XunitException(failure.ToString());
    }

    private static void AssertLoads<T>() where T : UserControl, new()
        => OnStaThread(() => Assert.NotNull(new T()));

    [Fact]
    public void AssetTab_LoadsItsXaml()
    {
        AssertLoads<AssetTab>();
    }

    [Fact]
    public void ProjectTab_LoadsItsXaml()
    {
        AssertLoads<ProjectTab>();
    }

    [Fact]
    public void SettingsTab_LoadsItsXaml()
    {
        AssertLoads<SettingsTab>();
    }
}
