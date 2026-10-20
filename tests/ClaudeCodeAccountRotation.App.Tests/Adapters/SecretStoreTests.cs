using System.Runtime.Versioning;
using System.Text;
using ClaudeCodeAccountRotation.App.Adapters.SecretStore;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The OS store adapters. Expected values come from wincred.h as the
/// CREDENTIALW page documents it (<c>CRED_TYPE_GENERIC</c> is 1,
/// <c>CRED_PERSIST_LOCAL_MACHINE</c> is 2, <c>ERROR_NO_SUCH_LOGON_SESSION</c>
/// is 1312), the target format the design names, and the two causes the
/// acceptance names for a Linux host with no usable Secret Service.
/// </summary>
public sealed class SecretStoreTests
{
    private const string LibraryMissing = "libsecret is not installed";
    private const string NoProvider = "no Secret Service provider";

    public static bool OnWindows => OperatingSystem.IsWindows();

    public static bool OnLinux => OperatingSystem.IsLinux();

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static LibsecretStore Libsecret(string library, Dictionary<string, string>? environment = null, string? existingPath = null) =>
        new(
            library,
            name => environment is not null && environment.TryGetValue(name, out string? value) ? value : null,
            path => path == existingPath,
            TimeSpan.FromSeconds(5));

    [Fact]
    public void ACredentialIsAGenericOneKeptOnThisComputerOnlyUnderTheToolsTarget()
    {
        WindowsCredentialStore.CredentialArguments console = WindowsCredentialStore.ArgumentsFor(SecretKind.ConsoleApiKey, Email("a@example.com"));
        WindowsCredentialStore.CredentialArguments admin = WindowsCredentialStore.ArgumentsFor(SecretKind.AdminKey, Email("a@example.com"));

        console.Persist.ShouldBe(2u);
        console.Type.ShouldBe(1u);
        console.TargetName.ShouldBe("claude-code-account-rotation:console-api-key:a@example.com");
        admin.Persist.ShouldBe(2u);
        admin.TargetName.ShouldBe("claude-code-account-rotation:admin-key:a@example.com");
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Credential Manager is Windows only")]
    [SupportedOSPlatform("windows")]
    public async Task CredentialManagerWritesReadsAndDeletesAKeyPersistedOnThisComputer()
    {
        AccountEmail email = Email("store-test-" + Guid.NewGuid().ToString("N") + "@example.com");
        WindowsCredentialStore.CredentialArguments arguments = WindowsCredentialStore.ArgumentsFor(SecretKind.ConsoleApiKey, email);
        WindowsCredentialStore store = new();
        try
        {
            int written = WindowsCredentialStore.WriteCredential(arguments, Encoding.UTF8.GetBytes("sk-ant-api03-store-test"));
            if (written == 1312)
            {
                Assert.Skip("CredWriteW returned ERROR_NO_SUCH_LOGON_SESSION (1312): this session has no credential set");
            }

            written.ShouldBe(0);
            WindowsCredentialStore.ReadCredential(arguments.TargetName, out byte[]? blob, out uint persist).ShouldBe(0);
            persist.ShouldBe(2u);
            Encoding.UTF8.GetString(blob!).ShouldBe("sk-ant-api03-store-test");

            (await store.WriteAsync(SecretKind.ConsoleApiKey, email, "sk-ant-api03-replaced", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            (await store.ReadAsync(SecretKind.ConsoleApiKey, email, TestContext.Current.CancellationToken)).Value.ShouldBe("sk-ant-api03-replaced");
            WindowsCredentialStore.ReadCredential(arguments.TargetName, out _, out uint replacedPersist).ShouldBe(0);
            replacedPersist.ShouldBe(2u);

            (await store.DeleteAsync(SecretKind.ConsoleApiKey, email, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            Result<string?, string> gone = await store.ReadAsync(SecretKind.ConsoleApiKey, email, TestContext.Current.CancellationToken);
            gone.IsSuccess.ShouldBeTrue();
            gone.Value.ShouldBeNull();
            (await store.DeleteAsync(SecretKind.ConsoleApiKey, email, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        }
        finally
        {
            WindowsCredentialStore.DeleteCredential(arguments.TargetName);
        }
    }

    [Fact]
    public async Task AMissingLibsecretIsNamedAsNotInstalledAndHasNothingToDelete()
    {
        LibsecretStore store = Libsecret("libsecret-absent-" + Guid.NewGuid().ToString("N") + ".so.0");

        Result<Unit, string> probe = await store.ProbeAsync(TestContext.Current.CancellationToken);
        Result<Unit, string> write = await store.WriteAsync(SecretKind.ConsoleApiKey, Email("a@example.com"), "sk-ant-api03-x", TestContext.Current.CancellationToken);
        Result<Unit, string> delete = await store.DeleteAsync(SecretKind.ConsoleApiKey, Email("a@example.com"), TestContext.Current.CancellationToken);

        probe.Error.ShouldStartWith(LibraryMissing);
        write.Error.ShouldStartWith(LibraryMissing);
        delete.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("file")]
    [InlineData("anything-else")]
    public async Task ASecretBackendOtherThanTheServiceIsRefusedBeforeLibsecretIsCalled(string backend)
    {
        LibsecretStore store = Libsecret("libsecret-absent.so.0", new Dictionary<string, string> { ["SECRET_BACKEND"] = backend });

        Result<Unit, string> probe = await store.ProbeAsync(TestContext.Current.CancellationToken);

        probe.Error.ShouldContain("SECRET_BACKEND=" + backend);
    }

    [Fact]
    public async Task InsideAFlatpakLibsecretWouldUseAFileSoTheKeyIsRefused()
    {
        LibsecretStore store = Libsecret("libsecret-absent.so.0", existingPath: "/.flatpak-info");

        Result<Unit, string> write = await store.WriteAsync(SecretKind.ConsoleApiKey, Email("a@example.com"), "sk-ant-api03-x", TestContext.Current.CancellationToken);

        write.IsFailure.ShouldBeTrue();
        write.Error.ShouldContain("flatpak");
    }

    [Fact(SkipUnless = nameof(OnLinux), Skip = "The Secret Service is the Linux store")]
    public async Task ThisLinuxHostsProbeEitherSucceedsOrNamesOneOfTheTwoCauses()
    {
        Result<Unit, string> probe = await LibsecretStore.ForCurrentProcess().ProbeAsync(TestContext.Current.CancellationToken);
        if (probe.IsSuccess)
        {
            Assert.Skip("this host runs a Secret Service provider, so there is no cause to name");
        }

        (probe.Error.StartsWith(LibraryMissing, StringComparison.Ordinal) || probe.Error.StartsWith(NoProvider, StringComparison.Ordinal))
            .ShouldBeTrue(probe.Error);
    }

    [Fact]
    public async Task AnUnsupportedPlatformRefusesAKeyWithItsReasonAndHasNothingToDelete()
    {
        UnavailableSecretStore store = new("no store here");

        (await store.ProbeAsync(TestContext.Current.CancellationToken)).Error.ShouldBe("no store here");
        (await store.WriteAsync(SecretKind.ConsoleApiKey, Email("a@example.com"), "k", TestContext.Current.CancellationToken)).Error.ShouldBe("no store here");
        (await store.DeleteAsync(SecretKind.AdminKey, Email("a@example.com"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }
}
