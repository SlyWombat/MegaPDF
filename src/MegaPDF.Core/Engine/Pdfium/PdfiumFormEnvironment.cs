using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Pdfium;

/// <summary>
/// Owns the FPDF_FORMFILLINFO block and the form-fill handle for one document.
/// PDFium retains the struct pointer and may invoke the callbacks at any time,
/// so both the unmanaged memory and the delegates must outlive the handle.
/// Callers must hold <see cref="PdfiumLibrary.Lock"/>.
/// </summary>
internal sealed class PdfiumFormEnvironment : IDisposable
{
    private readonly IntPtr _infoPtr;
    // Referenced only to keep the delegates from being garbage-collected.
    private readonly List<Delegate> _keepAlive = [];
    private bool _disposed;

    public IntPtr Handle { get; }

    public PdfiumFormEnvironment(IntPtr document)
    {
        var info = new PdfiumNative.FPDF_FORMFILLINFO
        {
            Version = 1,
            Release = Keep(new PdfiumNative.FfiVoidDelegate(_ => { })),
            FFI_Invalidate = Keep(new PdfiumNative.FfiInvalidateDelegate((_, _, _, _, _, _) => { })),
            FFI_OutputSelectedRect = Keep(new PdfiumNative.FfiInvalidateDelegate((_, _, _, _, _, _) => { })),
            FFI_SetCursor = Keep(new PdfiumNative.FfiSetCursorDelegate((_, _) => { })),
            FFI_SetTimer = Keep(new PdfiumNative.FfiSetTimerDelegate((_, _, _) => 0)),
            FFI_KillTimer = Keep(new PdfiumNative.FfiKillTimerDelegate((_, _) => { })),
            FFI_GetLocalTime = Keep(new PdfiumNative.FfiGetLocalTimeDelegate(_ => default)),
            FFI_OnChange = Keep(new PdfiumNative.FfiVoidDelegate(_ => { })),
            FFI_GetPage = Keep(new PdfiumNative.FfiGetPageDelegate((_, _, _) => IntPtr.Zero)),
            FFI_GetCurrentPage = Keep(new PdfiumNative.FfiGetCurrentPageDelegate((_, _) => IntPtr.Zero)),
            FFI_GetRotation = Keep(new PdfiumNative.FfiGetRotationDelegate((_, _) => 0)),
            FFI_ExecuteNamedAction = Keep(new PdfiumNative.FfiExecuteNamedActionDelegate((_, _) => { })),
            FFI_SetTextFieldFocus = Keep(new PdfiumNative.FfiSetTextFieldFocusDelegate((_, _, _, _) => { })),
            FFI_DoURIAction = Keep(new PdfiumNative.FfiDoUriActionDelegate((_, _) => { })),
            FFI_DoGoToAction = Keep(new PdfiumNative.FfiDoGoToActionDelegate((_, _, _, _, _) => { })),
            JsPlatform = IntPtr.Zero,
        };

        _infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PdfiumNative.FPDF_FORMFILLINFO>());
        Marshal.StructureToPtr(info, _infoPtr, fDeleteOld: false);

        Handle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document, _infoPtr);
        if (Handle == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_infoPtr);
            throw new InvalidOperationException("PDFium could not initialize the form-fill environment.");
        }
    }

    private IntPtr Keep(Delegate callback)
    {
        _keepAlive.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        PdfiumNative.FPDFDOC_ExitFormFillEnvironment(Handle);
        Marshal.FreeHGlobal(_infoPtr);
        _keepAlive.Clear();
    }
}
