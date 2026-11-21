using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.Windows.Platform;

/// <summary>
/// Writes and reads <c>.lnk</c> files with the Windows Shell's own <c>IShellLinkW</c> object (the same one Explorer uses), on a
/// dedicated single-threaded-apartment thread as the Shell expects. No script host and no extra package is involved.
/// </summary>
public sealed class ShellLinkShortcutFiles : IShortcutFiles
{
    private const int MaxPath = 32_768;

    public void Create(string shortcutPath, ShortcutDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentNullException.ThrowIfNull(definition);
        RunOnStaThread(() =>
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(definition.TargetPath);
                link.SetWorkingDirectory(definition.WorkingDirectory);
                link.SetDescription(definition.Description);
                link.SetIconLocation(definition.IconPath, definition.IconIndex);
                ((IPersistFile)link).Save(shortcutPath, true);
            }
            finally
            {
                _ = Marshal.FinalReleaseComObject(link);
            }

            return true;
        });
    }

    public ShortcutDefinition Read(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        if (!File.Exists(shortcutPath))
        {
            throw new FileNotFoundException("The shortcut does not exist.", shortcutPath);
        }

        return RunOnStaThread(() =>
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(shortcutPath, 0);
                var target = new StringBuilder(MaxPath);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                var directory = new StringBuilder(MaxPath);
                link.GetWorkingDirectory(directory, directory.Capacity);
                var description = new StringBuilder(1024);
                link.GetDescription(description, description.Capacity);
                var icon = new StringBuilder(MaxPath);
                link.GetIconLocation(icon, icon.Capacity, out int index);
                return new ShortcutDefinition(target.ToString(), directory.ToString(), description.ToString(), icon.ToString(), index);
            }
            finally
            {
                _ = Marshal.FinalReleaseComObject(link);
            }
        });
    }

    private static T RunOnStaThread<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or ArgumentException or IOException)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "GlyphLens shortcut",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new IOException("The Windows Shell did not answer in time.");
        }

        return failure switch
        {
            null => result!,
            IOException io => throw io,
            _ => throw new IOException($"The shortcut could not be written or read ({failure.GetType().Name}).", failure),
        };
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
