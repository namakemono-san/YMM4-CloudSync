using System.Runtime.InteropServices;

namespace YMM4CloudSync.YMMX.Core.Commons;

internal static class HardLink
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string linkPath, string existingPath, IntPtr securityAttributes);

    public static bool TryCreate(string linkPath, string existingPath)
    {
        try
        {
            return CreateHardLinkNative(linkPath, existingPath, IntPtr.Zero);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
