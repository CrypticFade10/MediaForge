using System.Runtime.InteropServices;

namespace SimilarVideoFinder.Core;

public sealed class ShellDeletionBackend : IDeletionBackend
{
    public DeleteOutcome TryRecycle(FileRecord file)
    {
        if (file.IsReference) return new(file.Path, DeleteState.Skipped, "Protected reference file.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recycling requires Windows.");
        if (!file.IsCurrent()) return new(file.Path, DeleteState.Skipped, "File changed or is unavailable.");
        if (DeletionPolicy.IsNetworkPath(file.Path))
            return new(file.Path, DeleteState.RequiresPermanentConfirmation, "Network files cannot use the Windows Recycle Bin.");
        IFileOperation? operation = null; IShellItem? item = null;
        var sink = new RecycleGuard(file);
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
            // The application has already confirmed this selection. Suppress the redundant
            // shell confirmation; the callback aborts before ANY non-recyclable deletion.
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00000400 | 0x00000004 | 0x00000010);
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file.Path, IntPtr.Zero, ref iid, out item));
            operation.DeleteItem(item, sink);
            int hr = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (sink.Changed) return new(file.Path, DeleteState.Skipped, "File changed before the shell operation.");
            if (sink.NonRecyclable) return new(file.Path, DeleteState.RequiresPermanentConfirmation, "Windows cannot recycle this file.");
            if (hr < 0 || aborted || sink.Result < 0)
                return new(file.Path, DeleteState.Failed, $"Recycle failed or was cancelled (0x{(hr < 0 ? hr : sink.Result):X8}).");
            return !File.Exists(file.Path) && sink.Completed
                ? new(file.Path, DeleteState.Recycled, "Moved to the Windows Recycle Bin.")
                : new(file.Path, DeleteState.Failed, "Windows did not confirm recycling.");
        }
        catch (COMException ex) { return new(file.Path, DeleteState.Failed, ex.Message); }
        finally
        {
            if (item != null) Marshal.FinalReleaseComObject(item);
            if (operation != null) Marshal.FinalReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }
    public DeleteOutcome DeletePermanently(FileRecord file)
    {
        if (file.IsReference) return new(file.Path, DeleteState.Skipped, "Protected reference file.");
        if (!file.IsCurrent()) return new(file.Path, DeleteState.Skipped, "File changed or is unavailable.");
        File.Delete(file.Path);
        return new(file.Path, DeleteState.DeletedPermanently, "Permanently deleted after explicit confirmation.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink? sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IProgressSink? sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IProgressSink? sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IProgressSink? sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IProgressSink? sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleGuard(FileRecord file) : IProgressSink
    {
        public bool NonRecyclable;
        public bool Changed;
        public bool Completed;
        public int Result;
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if (!file.IsCurrent()) { Changed = true; return unchecked((int)0x80004004); }
            if ((flags & 0x80) == 0) { NonRecyclable = true; return unchecked((int)0x80004004); }
            return 0;
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem) { Result = result; Completed = result >= 0; return 0; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string? template, uint attributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
