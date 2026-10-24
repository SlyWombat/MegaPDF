using Microsoft.UI.Xaml.Controls;
using MegaPDF.Core.Engine;

namespace MegaPDF.App;

internal static partial class Screenshot
{
    /// <summary>
    /// The sentences a restricted document says, where they land (#800). The document is
    /// owner-only.pdf (tools/gen_security_fixtures.sh: every permission withheld; the test
    /// fixtures copy puts it in the fixtures directory), which opens restricted with no
    /// password.
    ///
    ///   * the banner is open on arrival and is the real InfoBar, not a string compared in
    ///     the abstract. In the unpackaged build its x:Uid text follows Windows' display
    ///     language rather than <c>--language</c> (see <see cref="AppLanguage"/>), so the
    ///     bar is given the catalogue's text for the run's language before the PNG is cut;
    ///     in the packaged app that is the text it already has;
    ///   * the real extract command is refused with the extraction-class sentence, in the
    ///     page-tool notice bar, and writes no file; that frame is cut to
    ///     <c>&lt;png&gt;-extract.png</c>;
    ///   * the assembly refusal and the Password command's refusal are raised through the
    ///     typed reasons the apps map to them, and the last one is left on screen.
    ///
    /// The unlock prompt and the redaction refusal are ContentDialogs, which
    /// RenderTargetBitmap does not draw (#144), so they are not in the PNG; they have no
    /// fixed-width layout to clip, both wrap, and the redaction refusal is a backstop no
    /// gesture reaches on a document that withholds modify. The exit code is the test.
    /// </summary>
    private static async Task<bool> CheckRestrictedAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } tab)
        {
            Console.Error.WriteLine("--screenshot-state restricted needs a document (owner-only.pdf).");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        Check("the document opened restricted", tab.Capabilities.IsRestricted);
        if (!tab.Capabilities.IsRestricted)
        {
            Console.Error.WriteLine("restricted: open owner-only.pdf, not an ordinary document.");
            return false;
        }
        await Task.Delay(500);   // let the bindings settle before reading the control

        var banner = tab.View is { } view ? FindByAutomationId<InfoBar>(view, "RestrictedNoticeBar") : null;
        Check("RestrictedNoticeBar is in the page and open", banner?.IsOpen == true);
        if (banner is not null)
        {
            // MRT reads "Uid.Property" as the path Uid/Property.
            banner.Message = AppLanguage.GetString("RestrictedNoticeBar/Message");
            if (banner.ActionButton is Button unlock)
                unlock.Content = AppLanguage.GetString("UnlockButton/Content");
        }
        Check("  and says something that is not its own key", !string.IsNullOrWhiteSpace(banner?.Message) && banner?.Message != "RestrictedNoticeBar/Message");

        // The extract command is refused for the copy bit, not for assembly, and says so.
        var extracted = Path.Combine(Path.GetTempPath(), $"megapdf-restricted-{Guid.NewGuid():N}.pdf");
        try
        {
            var wrote = await tab.ExtractPagesToPathAsync(extracted);
            Check("Extract pages is refused", !wrote);
            Check("  with the extraction sentence", tab.IsPageToolNoticeOpen && tab.PageToolNotice == Strings.ExtractRestricted);
            Check("  and writes no file", !File.Exists(extracted));
        }
        finally
        {
            try { File.Delete(extracted); } catch { /* it should not exist */ }
        }
        await Task.Delay(300);
        if (ArgumentAfter("--screenshot") is { Length: > 0 } png)
            await CaptureAsync(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(png))!,
                                                    Path.GetFileNameWithoutExtension(png) + "-extract.png"));

        // The typed reasons each map to their own sentence, and neither is the generic one.
        var assembly = DocumentViewModel.DescribePageToolFailure(new PageToolException(PageToolFailure.Restricted, "restricted"));
        Check("a refused page operation says the assembly sentence", assembly == Strings.PageToolsRestricted);
        Check("  which is not the extraction sentence", Strings.PageToolsRestricted != Strings.ExtractRestricted);
        Check("the Password command's refusal says what is missing, not who asked",
              UserFacing.Describe(new DocumentRestrictedException()) == Strings.ErrorRestricted
              && Strings.ErrorRestricted != Strings.PageToolsRestricted);

        // Left on screen for the PNG: the assembly refusal under the banner.
        tab.ShowPageToolRefusal(new PageToolException(PageToolFailure.Restricted, "restricted"));
        await Task.Delay(300);
        Check("  and it shows in the notice bar", tab.IsPageToolNoticeOpen && tab.PageToolNotice == Strings.PageToolsRestricted);

        Console.Error.WriteLine($"restricted: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }
}
