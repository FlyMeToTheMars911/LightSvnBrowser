using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LightSvnBrowser
{
    /// <summary>Opens the Windows shell folder picker with multiple selection enabled.</summary>
    /// <remarks>Author: Codex, date: 2026-11-09.</remarks>
    internal static class FolderSelectionDialog
    {
        private const uint PickFolders = 0x00000020;
        private const uint ForceFileSystem = 0x00000040;
        private const uint AllowMultiSelect = 0x00000200;
        private const int Cancelled = unchecked((int)0x800704C7);
        private const uint FileSystemPath = 0x80058000;

        /// <summary>Lets the user select multiple local folders in one picker.</summary>
        /// <param name="owner">Handle of the owning window.</param>
        /// <returns>The selected filesystem paths, or an empty list on cancellation.</returns>
        internal static List<string> SelectFolders(IntPtr owner)
        {
            List<string> paths = new List<string>();
            IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | AllowMultiSelect);
                dialog.SetTitle("选择要提交到 SVN 的本地文件夹");
                int result = dialog.Show(owner);
                if (result == Cancelled) return paths;
                Marshal.ThrowExceptionForHR(result);
                IShellItemArray items;
                dialog.GetResults(out items);
                try
                {
                    uint count;
                    items.GetCount(out count);
                    for (uint index = 0; index < count; index++)
                    {
                        IShellItem item;
                        items.GetItemAt(index, out item);
                        try
                        {
                            string path;
                            item.GetDisplayName(FileSystemPath, out path);
                            paths.Add(path);
                        }
                        finally { Marshal.ReleaseComObject(item); }
                    }
                }
                finally { Marshal.ReleaseComObject(items); }
                return paths;
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        // The method order in these COM declarations follows the Windows shell interfaces.
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filterSpecs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem folder, uint placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid clientGuid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr value);
            void GetPropertyStore(uint flags, ref Guid interfaceId, out IntPtr store);
            void GetPropertyDescriptionList(IntPtr key, ref Guid interfaceId, out IntPtr list);
            void GetAttributes(uint flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr value);
            void GetParent(out IShellItem item);
            void GetDisplayName(uint displayNameType, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }
    }
}
