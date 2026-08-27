using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.Core.ViewModels;

public interface IProjectDialogService
{
    void ShowInformation(string message, string caption);

    void ShowWarning(string message, string caption);

    void ShowError(string message, string caption);

    bool Confirm(string message, string caption);

    ExtractConflictAction ResolveExtractConflict(YmmxMeta? existing, YmmxMeta? incoming);

    string? PickDownloadDestination(string suggestedFileName);

    void ReportException(Exception exception);
}
