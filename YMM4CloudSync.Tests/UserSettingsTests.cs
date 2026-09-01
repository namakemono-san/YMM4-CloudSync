using System.ComponentModel;
using Xunit;
using YMM4CloudSync.Core.Models;

namespace YMM4CloudSync.Tests;

public class UserSettingsTests
{
    [Fact]
    public void PromptForFileAssociation_DefaultsToTrue()
    {
        Assert.True(new UserSettings().PromptForFileAssociation);
    }

    [Fact]
    public void PromptForFileAssociation_RaisesPropertyChangedOnChange()
    {
        var settings = new UserSettings();
        var raised = new List<string?>();

        ((INotifyPropertyChanged)settings).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.PromptForFileAssociation = false;

        Assert.Contains(nameof(UserSettings.PromptForFileAssociation), raised);
    }

    [Fact]
    public void PromptForFileAssociation_DoesNotRaiseWhenUnchanged()
    {
        var settings = new UserSettings();
        var raised = new List<string?>();

        ((INotifyPropertyChanged)settings).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.PromptForFileAssociation = true;

        Assert.DoesNotContain(nameof(UserSettings.PromptForFileAssociation), raised);
    }
}
