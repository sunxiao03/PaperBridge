using PaperBridge.Infrastructure.Security;

namespace PaperBridge.Infrastructure.Tests.Security;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public async Task CredentialRoundTrip_SavesReadsAndDeletesTestValue()
    {
        var store = new WindowsCredentialStore();
        var account = $"integration-test-{Guid.NewGuid():N}";
        var secret = $"test-value-{Guid.NewGuid():N}";

        try
        {
            await store.SaveAsync(account, secret);

            Assert.Equal(secret, await store.GetAsync(account));
        }
        finally
        {
            await store.DeleteAsync(account);
        }

        Assert.Null(await store.GetAsync(account));
    }

    [Fact]
    public async Task SaveAsync_RejectsSecretLargerThanWindowsCredentialLimit()
    {
        var store = new WindowsCredentialStore();
        var account = $"integration-test-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(account, new string('x', 1281)));
        Assert.Null(await store.GetAsync(account));
    }
}
