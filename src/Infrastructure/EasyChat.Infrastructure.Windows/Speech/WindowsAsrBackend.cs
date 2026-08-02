using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyChat.Infrastructure.Windows.Speech;

internal delegate void WindowsAsrCallback(
    int type,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string result);

internal interface IWindowsAsrBackend
{
    bool Initialize(string modelPath);
    void SetCallback(WindowsAsrCallback callback);
    void StartLoopbackCapture(int[] processIds);
    void StartRecognition();
    void Cleanup();
}

[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsAsrBackend : IWindowsAsrBackend
{
    private const string DllName = "ASRNative.dll";

    static NativeWindowsAsrBackend()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Lib", DllName));
        NativeLibrary.SetDllImportResolver(typeof(NativeWindowsAsrBackend).Assembly, (name, _, _) =>
            string.Equals(name, DllName, StringComparison.OrdinalIgnoreCase)
                && NativeLibrary.TryLoad(path, out var handle)
                    ? handle
                    : IntPtr.Zero);
    }

    public bool Initialize(string modelPath) => InitializeNative(modelPath);
    public void SetCallback(WindowsAsrCallback callback) => SetCallbackNative(callback);
    public void StartLoopbackCapture(int[] processIds) =>
        StartLoopbackCaptureNative(processIds, processIds.Length);
    public void StartRecognition() => StartRecognitionNative();
    public void Cleanup() => CleanupNative();

    [DllImport(DllName, EntryPoint = "Initialize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeNative([MarshalAs(UnmanagedType.LPStr)] string modelPath);

    [DllImport(DllName, EntryPoint = "SetCallback", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetCallbackNative(WindowsAsrCallback callback);

    [DllImport(DllName, EntryPoint = "StartRecognition", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StartRecognitionNative();

    [DllImport(DllName, EntryPoint = "StartLoopbackCapture", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StartLoopbackCaptureNative(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] processIds,
        int count);

    [DllImport(DllName, EntryPoint = "Cleanup", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CleanupNative();
}
