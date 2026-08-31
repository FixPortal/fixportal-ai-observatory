using System.Security.Cryptography;
using AiObservatory.Data.Security;
using AwesomeAssertions;

namespace AiObservatory.Data.Tests.Security;

public class SlackWebhookProtectorTests
{
    private const string WebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz";
    private readonly SlackWebhookProtector _protector = new("test-passphrase");

    [Fact]
    public void Protect_Unprotect_round_trips_the_original_url()
    {
        _protector.Unprotect(_protector.Protect(WebhookUrl)).Should().Be(WebhookUrl);
    }

    [Fact]
    public void Protect_never_stores_the_recognisable_url()
    {
        var stored = _protector.Protect(WebhookUrl);

        stored.Should().StartWith(SlackWebhookProtector.EncryptedPrefix);
        stored.Should().NotContain("hooks.slack.com");
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_per_call()
    {
        _protector.Protect(WebhookUrl).Should().NotBe(_protector.Protect(WebhookUrl));
    }

    [Fact]
    public void Unprotect_returns_legacy_plaintext_unchanged()
    {
        // Rows written before SLACK_WEBHOOK_PROTECTION_KEY was set carry no prefix.
        _protector.Unprotect(WebhookUrl).Should().Be(WebhookUrl);
    }

    [Fact]
    public void Unprotect_throws_on_tampered_ciphertext()
    {
        var stored = _protector.Protect(WebhookUrl).ToCharArray();
        // Flip a character inside the base64 payload (never the trailing padding), so the
        // blob still decodes but the GCM tag check must reject it.
        var middle = stored.Length / 2;
        stored[middle] = stored[middle] == 'A' ? 'B' : 'A';

        var act = () => _protector.Unprotect(new string(stored));

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_throws_under_a_different_key()
    {
        var stored = _protector.Protect(WebhookUrl);

        var act = () => new SlackWebhookProtector("another-passphrase").Unprotect(stored);

        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Ctor_rejects_a_missing_passphrase(string? passphrase)
    {
        var act = () => new SlackWebhookProtector(passphrase!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnprotectValue_throws_for_encrypted_value_when_no_key_is_configured()
    {
        // The test host never sets SLACK_WEBHOOK_PROTECTION_KEY (integration tests rely on
        // the no-key pass-through), so the facade must refuse an encrypted value loudly
        // rather than hand a corrupt URL to the notifier.
        Environment.GetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable).Should().BeNull();

        var act = () => SlackWebhookProtector.UnprotectValue(SlackWebhookProtector.EncryptedPrefix + "AAAA");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SLACK_WEBHOOK_PROTECTION_KEY*");
    }
}
