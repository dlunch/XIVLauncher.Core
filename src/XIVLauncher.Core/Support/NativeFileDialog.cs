using System.Runtime.InteropServices;

using Hexa.NET.SDL3;

namespace XIVLauncher.Core.Support;

internal static unsafe class NativeFileDialog
{
    private sealed record DialogState(Action<string?, string?> Callback);
    private delegate void ShowDialogAction(void* statePointer);

    private static readonly SDLDialogFileCallback FileDialogCallback = OnDialogComplete;

    public static void ShowOpen(string initialLocation, Action<string?, string?> callback)
    {
        ShowDialog(
            (statePointer) => SDL.ShowOpenFileDialog(
                FileDialogCallback,
                statePointer,
                Program.Window,
                null,
                0,
                initialLocation,
                false),
            callback);
    }

    public static void ShowSave(string initialLocation, Action<string?, string?> callback)
    {
        ShowDialog(
            (statePointer) => SDL.ShowSaveFileDialog(
                FileDialogCallback,
                statePointer,
                Program.Window,
                null,
                0,
                initialLocation),
            callback);
    }

    private static void ShowDialog(
        ShowDialogAction showDialog,
        Action<string?, string?> callback)
    {
        var stateHandle = GCHandle.Alloc(new DialogState(callback));
        try
        {
            showDialog((void*)GCHandle.ToIntPtr(stateHandle));
        }
        catch (Exception ex)
        {
            stateHandle.Free();
            callback(null, ex.Message);
        }
    }

    private static void OnDialogComplete(void* userdata, byte** filelist, int filter)
    {
        var stateHandle = GCHandle.FromIntPtr((IntPtr)userdata);
        try
        {
            var state = (DialogState)stateHandle.Target!;
            if (filelist == null)
            {
                state.Callback(null, SDL.GetErrorS());
                return;
            }

            var selectedPath = filelist[0] == null
                ? null
                : Marshal.PtrToStringUTF8((IntPtr)filelist[0]);
            state.Callback(selectedPath, null);
        }
        finally
        {
            stateHandle.Free();
        }
    }
}
