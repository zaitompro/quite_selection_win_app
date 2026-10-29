using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using QuietSelection.App.Platform;
using QuietSelection.App.Speech.Downloads;

namespace QuietSelection.Setup;

internal static class Program
{
    private const string Publisher = "CN=QuietSelection.Local";

    [DllImport("QuietSelection.Native.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qs_install_package(string uri);
    [DllImport("QuietSelection.Native.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qs_activate_installed(string arguments, out uint processId);

    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["install", var root, "--confirm-current-user-trust"])
                Install(Path.GetFullPath(root));
            else if (args is ["trust-machine-public", var certificateRoot, "--confirm-machine-public-trust"])
                TrustMachinePublic(Path.GetFullPath(certificateRoot));
            else if (args is ["launch", var arguments])
            {
                Marshal.ThrowExceptionForHR(qs_activate_installed(arguments, out uint processId));
                Console.WriteLine($"QS_PACKAGE_ACTIVATED {processId}");
            }
            else if (args.Length >= 3 && args[0] == "voices" && args[^1] == "--confirm-download")
            {
                foreach (string language in args[1..^1]) VoiceInstallation.Validate(language);
                foreach (string language in args[1..^1])
                {
                    int code = VoiceInstallation.InstallAsync(language, (stage) => Console.WriteLine($"QS_VOICE {language} {stage}")).GetAwaiter().GetResult();
                    Console.WriteLine($"QS_VOICE_RESULT {language} {code}");
                    if (code != 0 && code != 3010) return code;
                }
            }
            else if (args is ["rhvoice", var voiceLanguage, var cache, var action] && action is "--download" or "--install")
            {
                var package = RhVoiceInstallation.Packages.Single(x => x.Language == voiceLanguage);
                string path = RhVoiceInstallation.DownloadAsync(package, Path.GetFullPath(cache),
                    new Progress<string>(Console.WriteLine), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"QS_RHVOICE_VERIFIED {voiceLanguage} {package.Bytes} {package.Sha256}");
                if (action == "--install")
                {
                    int code = RhVoiceInstallation.InstallAsync(path, package).GetAwaiter().GetResult();
                    Console.WriteLine($"QS_RHVOICE_INSTALL_RESULT {voiceLanguage} {code}");
                    if (code is not (0 or 3010)) return code;
                }
            }
            else { Console.Error.WriteLine("QS_SETUP_ARGUMENTS_INVALID"); return 2; }
            return 0;
        }
        catch (Exception ex)
        {
            // Never emit unfiltered Windows diagnostic text, paths or account details.
            Console.Error.WriteLine($"QS_SETUP_FAILED {ex.GetType().Name} 0x{ex.HResult:X8}");
            return 1;
        }
    }

    private static void TrustMachinePublic(string root)
    {
        string signing = Path.Combine(root, ".tools/signing");
        string thumbprint = File.ReadAllText(Path.Combine(signing, "thumbprint.txt")).Trim();
        string publicPath = Path.Combine(signing, "QuietSelection.Local.cer");
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(publicPath);
        if (certificate.Subject != Publisher || certificate.Thumbprint != thumbprint || certificate.HasPrivateKey)
            throw new InvalidDataException("PUBLIC_CERTIFICATE_VALIDATION_FAILED");
        // Only the public .cer is passed to the signed OS tool; no private-key export or machine-key creation.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "certutil.exe"))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-addstore", "-f", "TrustedPeople", publicPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new ExternalException("MACHINE_PUBLIC_TRUST_FAILED", process.ExitCode);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        using var installed = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).OfType<X509Certificate2>().Single();
        if (installed.HasPrivateKey) throw new InvalidOperationException("MACHINE_CERTIFICATE_MUST_BE_PUBLIC_ONLY");
        Console.WriteLine("QS_PUBLIC_CERTIFICATE_TRUSTED_LOCAL_MACHINE_TRUSTEDPEOPLE");
    }

    private static void Install(string root)
    {
        var manifest = XDocument.Load(Path.Combine(root, "packaging/AppxManifest.xml"));
        var identity = manifest.Root!.Elements().Single(x => x.Name.LocalName == "Identity");
        string version = Version.Parse(identity.Attribute("Version")!.Value).ToString();
        string unsigned = Path.Combine(root, $"artifacts/packages/QuietSelection_{version}_x64_unsigned.msix");
        string signed = Path.Combine(root, $"artifacts/packages/QuietSelection_{version}_x64.msix");
        if (!File.Exists(unsigned)) throw new FileNotFoundException();
        string signing = Path.Combine(root, ".tools/signing");
        Directory.CreateDirectory(signing);
        string thumbprintPath = Path.Combine(signing, "thumbprint.txt");
        using var personal = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        personal.Open(OpenFlags.ReadWrite);
        X509Certificate2? certificate = null;
        if (File.Exists(thumbprintPath))
        {
            string thumbprint = File.ReadAllText(thumbprintPath).Trim();
            if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit)) throw new InvalidDataException();
            certificate = personal.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).OfType<X509Certificate2>().SingleOrDefault();
            // Never silently replace a missing/expired publisher key: installed packages depend on this identity.
            if (certificate is null || !certificate.HasPrivateKey || certificate.Subject != Publisher || certificate.NotAfter <= DateTime.Now)
                throw new InvalidOperationException();
        }
        if (certificate is null)
        {
            var creation = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing };
            creation.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
            using var key = CngKey.Create(CngAlgorithm.Rsa, "QuietSelection.Local." + Guid.NewGuid().ToString("N"), creation);
            using var rsa = new RSACng(key);
            var request = new CertificateRequest(Publisher, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddYears(2));
            personal.Add(certificate);
            File.WriteAllText(thumbprintPath, certificate.Thumbprint);
            Console.WriteLine("QS_CERTIFICATE_CREATED_CURRENT_USER_NONEXPORTABLE");
        }
        using (certificate)
        {
            byte[] publicBytes = certificate.Export(X509ContentType.Cert);
            File.WriteAllBytes(Path.Combine(signing, "QuietSelection.Local.cer"), publicBytes);
            using var publicCertificate = X509CertificateLoader.LoadCertificate(publicBytes);
            using var trust = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
            trust.Open(OpenFlags.ReadWrite);
            trust.Add(publicCertificate);
            Console.WriteLine("QS_CERTIFICATE_TRUSTED_CURRENT_USER_TRUSTEDPEOPLE");
            File.Copy(unsigned, signed, true);
            string signTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10/bin/10.0.26100.0/x64/signtool.exe");
            RunTool(signTool, "sign", "/fd", "SHA256", "/s", "My", "/sha1", certificate.Thumbprint, signed);
            Console.WriteLine("QS_PACKAGE_SIGNED");
            // Windows deployment validates the MSIX signature and its applicable trust policy.
            // Generic Authenticode /pa verification uses a different certificate-chain policy.
            Marshal.ThrowExceptionForHR(qs_install_package(new Uri(signed).AbsoluteUri));
            using var rsa = certificate.GetRSAPrivateKey() as RSACng ?? throw new InvalidOperationException();
            if (rsa.Key.IsMachineKey || rsa.Key.ExportPolicy != CngExportPolicies.None) throw new InvalidOperationException("PRIVATE_KEY_SCOPE_INVALID");
            using var machineTrust = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            machineTrust.Open(OpenFlags.ReadOnly);
            var matching = machineTrust.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            bool machinePublicTrust = matching.Count == 1 && !matching[0].HasPrivateKey;
            File.WriteAllText(Path.Combine(root, "artifacts/packages/installation.json"), JsonSerializer.Serialize(new
            {
                installedUtc = DateTimeOffset.UtcNow,
                identity = "QuietSelection.Windows", publisher = Publisher,
                version,
                thumbprint = certificate.Thumbprint, machinePublicTrust,
                privateKeyScope = "CurrentUser", privateKeyExportable = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("QS_PACKAGE_INSTALLED_CURRENT_USER");
        }
    }

    private static void RunTool(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(stdout, stderr);
        // Tool output is discarded; only success or a numeric exit code is reported.
        if (process.ExitCode != 0) throw new ExternalException("QS_SIGNTOOL_FAILURE", process.ExitCode);
    }
}
