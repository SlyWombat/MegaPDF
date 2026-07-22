// MegaPDF test-build installer: double-click friendly, no visible PowerShell.
// Compiled with the in-box .NET Framework 4.x csc.exe (C# 5 syntax only!) so the
// resulting Setup.exe runs on any Windows 10/11 machine with no prerequisites.
//
// Flow: confirm -> trust the signing certificate (one UAC prompt, skipped if
// already trusted) -> install the .msix for the current user -> offer to launch.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

internal static class Setup
{
    private const string Title = "MegaPDF Setup";
    private const string AppUserModelId = @"shell:AppsFolder\ElectricRV.MegaPDF_fba94j4nmgb9y!App";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string cer = Directory.GetFiles(dir, "*.cer").FirstOrDefault();
            string msix = Directory.GetFiles(dir, "*.msix").FirstOrDefault();

            // Elevated helper mode: just trust the certificate, then exit.
            if (args.Contains("--cert"))
            {
                if (cer == null) return 1;
                TrustCertificate(cer);
                return 0;
            }

            if (cer == null || msix == null)
            {
                MessageBox.Show(
                    "Setup files are missing.\n\nKeep Setup.exe in the same folder as the .msix and .cer files from the zip.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            DialogResult go = MessageBox.Show(
                "This will install MegaPDF on this computer.\n\n" +
                "You may see one Windows security prompt — choose Yes.\n\nInstall now?",
                Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (go != DialogResult.OK)
                return 0;

            if (!IsCertificateTrusted(cer))
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, "--cert");
                psi.Verb = "runas"; // UAC
                psi.UseShellExecute = true;
                try
                {
                    Process elevated = Process.Start(psi);
                    elevated.WaitForExit();
                    if (elevated.ExitCode != 0)
                        throw new InvalidOperationException("The security certificate could not be installed.");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show("Installation was cancelled.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
            }

            // Install the package for the current user. Runs hidden; nothing flashes.
            var install = new ProcessStartInfo(
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -Command " +
                "\"Add-AppxPackage -Path '" + msix.Replace("'", "''") + "'\"");
            install.UseShellExecute = false;
            install.CreateNoWindow = true;
            install.RedirectStandardError = true;

            Process proc = Process.Start(install);
            string errors = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                MessageBox.Show(
                    "MegaPDF could not be installed.\n\n" +
                    (errors.Length > 600 ? errors.Substring(0, 600) : errors) +
                    "\n\nIf MegaPDF is currently open, close it and run Setup again.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            DialogResult open = MessageBox.Show(
                "MegaPDF is installed!\n\nYou'll find it in the Start menu. Open it now?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (open == DialogResult.Yes)
            {
                var launch = new ProcessStartInfo(AppUserModelId);
                launch.UseShellExecute = true;
                Process.Start(launch);
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Setup failed:\n\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static bool IsCertificateTrusted(string cerPath)
    {
        var cert = new X509Certificate2(cerPath);
        var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
        }
        finally
        {
            store.Close();
        }
    }

    private static void TrustCertificate(string cerPath)
    {
        var cert = new X509Certificate2(cerPath);
        var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
        }
        finally
        {
            store.Close();
        }
    }
}
