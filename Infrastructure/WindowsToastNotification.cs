using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Desktop.Infrastructure;

internal static class WindowsToastNotification
{
    private const string AppUserModelId = AppPaths.DisplayName;
    private const string AppUserModelIdProperty = "9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3";
    private const uint AppUserModelIdPropertyId = 5;
    private const ushort VariantTypeString = 31;

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        $"{AppPaths.DisplayName}.lnk");

    public static void RegisterShortcut()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定应用程序路径。");
        var shortcutPath = ShortcutPath;
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellLink = (IShellLinkW)(object)new ShellLink();
        try
        {
            shellLink.SetPath(executablePath);
            shellLink.SetWorkingDirectory(AppContext.BaseDirectory);
            shellLink.SetDescription(AppPaths.DisplayName);
            shellLink.SetIconLocation(executablePath, 0);

            var propertyStore = (IPropertyStore)shellLink;
            var propertyKey = new PropertyKey(Guid.Parse(AppUserModelIdProperty), AppUserModelIdPropertyId);
            var propertyValue = PropVariant.FromString(AppUserModelId);
            try
            {
                ThrowIfFailed(propertyStore.SetValue(ref propertyKey, ref propertyValue));
                ThrowIfFailed(propertyStore.Commit());
            }
            finally
            {
                propertyValue.Dispose();
            }

            var persistFile = (IPersistFile)shellLink;
            ThrowIfFailed(persistFile.Save(shortcutPath, true));
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    public static void UnregisterShortcut()
    {
        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }

    public static void Show(string title, string message)
    {
        var xml = new XmlDocument();
        xml.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{Escape(title)}</text><text>{Escape(message)}</text></binding></visual></toast>");
        var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        notifier.Show(new ToastNotification(xml));
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static void ThrowIfFailed(int hResult)
    {
        if (hResult < 0)
        {
            Marshal.ThrowExceptionForHR(hResult);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, IntPtr fileInfo, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maxDescription);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxDirectory);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxArguments);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig]
        int GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [PreserveSig]
        int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr Value;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = VariantTypeString,
            Value = Marshal.StringToCoTaskMemUni(value)
        };

        public void Dispose()
        {
            if (Value != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(Value);
                Value = IntPtr.Zero;
            }
        }
    }
}
