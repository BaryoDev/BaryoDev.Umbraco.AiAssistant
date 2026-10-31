using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using BaryoDev.Umbraco.AiAssistant.Api;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Polish;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Usage;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class SettingsTests
{
    private const string StoredKey = "test-stored-NOT-A-REAL-KEY-111";
    private const string ConfigKey = "test-config-NOT-A-REAL-KEY-222";

    private static (SettingsController Controller, MemoryKeyValueStore Store, IEffectiveSettings Settings, ApiKeyStore Keys) Make(
        AiAssistantOptions? options = null, IClaudeKeyTester? tester = null, IDataProtectionProvider? protection = null)
    {
        var store = new MemoryKeyValueStore();
        var prot = protection ?? new EphemeralDataProtectionProvider();
        var keys = new ApiKeyStore(store, prot, TimeProvider.System);
        var settings = new EffectiveSettings(Opts.Monitor(options ?? new AiAssistantOptions()), new SettingsStore(store), keys);
        var controller = new SettingsController(settings, keys, new SettingsStore(store), tester ?? new FixedTester(), new UserRateLimiter(TimeProvider.System), new UsageStore(store, TimeProvider.System))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "test")) } },
        };
        return (controller, store, settings, keys);
    }

    private sealed class FixedTester : IClaudeKeyTester
    {
        public Task<KeyTestResult> TestAsync(string? key, CancellationToken cancellationToken) => Task.FromResult(new KeyTestResult(true, "The key works."));
    }

    [Fact]
    public void A_saved_key_is_stored_encrypted_and_no_response_carries_it()
    {
        var (c, store, settings, _) = Make();

        var saved = (SettingsModel)((OkObjectResult)c.SaveKey(new SaveKeyRequest(StoredKey))).Value!;

        Assert.DoesNotContain(StoredKey, string.Concat(store.Values.Values));
        Assert.DoesNotContain(StoredKey, JsonSerializer.Serialize(saved));
        Assert.DoesNotContain(StoredKey, JsonSerializer.Serialize(((OkObjectResult)c.Get()).Value));
        Assert.Equal("dashboard", saved.KeySource);
        Assert.Equal(StoredKey, settings.Key);
    }

    [Fact]
    public async Task A_key_set_in_configuration_refuses_a_saved_key_and_is_the_one_tested()
    {
        var tester = new RecordingTester();
        var (c, _, settings, _) = Make(new AiAssistantOptions { Key = ConfigKey }, tester);

        var save = Assert.IsType<ObjectResult>(c.SaveKey(new SaveKeyRequest(StoredKey)));
        await c.Test(new TestKeyRequest(StoredKey), CancellationToken.None);

        Assert.Equal(400, save.StatusCode);
        Assert.Equal(ConfigKey, settings.Key);
        Assert.Null(tester.Tried);
    }

    [Fact]
    public void A_key_set_in_configuration_refuses_remove_and_keeps_the_saved_key()
    {
        var store = new MemoryKeyValueStore();
        var prot = new EphemeralDataProtectionProvider();
        var keys = new ApiKeyStore(store, prot, TimeProvider.System);
        keys.Save(StoredKey, "owner");
        var settings = new EffectiveSettings(Opts.Monitor(new AiAssistantOptions { Key = ConfigKey }), new SettingsStore(store), keys);
        var c = new SettingsController(settings, keys, new SettingsStore(store), new FixedTester(), new UserRateLimiter(TimeProvider.System), new UsageStore(store, TimeProvider.System));

        var result = Assert.IsType<ObjectResult>(c.RemoveKey());

        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(keys.Info());
    }

    [Theory]
    [InlineData("test-NOT-A-REAL\r\nKEY-333")]
    [InlineData("short")]
    [InlineData("has a space inside it")]
    public async Task Testing_a_malformed_key_is_a_400_and_never_reaches_the_tester(string typed)
    {
        var tester = new RecordingTester();
        var (c, _, _, _) = Make(tester: tester);

        var result = Assert.IsType<ObjectResult>(await c.Test(new TestKeyRequest(typed), CancellationToken.None));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("unset", tester.Tried);
    }

    private sealed class RecordingTester : IClaudeKeyTester
    {
        public string? Tried = "unset";
        public Task<KeyTestResult> TestAsync(string? key, CancellationToken cancellationToken)
        {
            Tried = key;
            return Task.FromResult(new KeyTestResult(true, "The key works."));
        }
    }

    [Fact]
    public void No_settings_model_has_a_field_that_can_hold_the_key()
    {
        foreach (Type t in new[] { typeof(SettingsModel), typeof(AiAssistantStatusModel) })
            Assert.DoesNotContain(t.GetProperties(), p => p.Name is "Key" or "ApiKey" or "Secret" or "Token");
    }

    [Fact]
    public void Configuration_wins_over_the_saved_key_and_the_saved_model()
    {
        var (c, _, settings, _) = Make(new AiAssistantOptions { Key = ConfigKey, Model = "claude-config-model" });
        c.SaveKey(new SaveKeyRequest(StoredKey));
        c.Save(new SaveSettingsRequest("claude-saved-model"));

        Assert.Equal(ConfigKey, settings.Key);
        Assert.Equal(KeySource.Configuration, settings.KeySource);
        Assert.Equal("claude-config-model", settings.Model);
        Assert.Equal("configuration", settings.ModelSource);
    }

    [Fact]
    public void A_saved_key_and_model_apply_at_once_and_removing_the_key_clears_it()
    {
        var (c, _, settings, _) = Make();
        Assert.False(settings.IsConfigured);

        c.SaveKey(new SaveKeyRequest(StoredKey));
        c.Save(new SaveSettingsRequest("claude-saved-model"));
        Assert.True(settings.IsConfigured);
        Assert.Equal("claude-saved-model", settings.Model);

        c.RemoveKey();
        Assert.False(settings.IsConfigured);
        Assert.Equal(KeySource.None, settings.KeySource);
    }

    [Fact]
    public void A_lost_key_ring_warns_without_a_value_and_asks_to_enter_it_again()
    {
        var (c, store, _, _) = Make();
        c.SaveKey(new SaveKeyRequest(StoredKey));

        // Same database, new key ring: the old ciphertext cannot be read.
        var keys2 = new ApiKeyStore(store, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var settings2 = new EffectiveSettings(Opts.Monitor(new AiAssistantOptions()), new SettingsStore(store), keys2);
        var c2 = new SettingsController(settings2, keys2, new SettingsStore(store), new FixedTester(), new UserRateLimiter(TimeProvider.System), new UsageStore(store, TimeProvider.System))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var model = (SettingsModel)((OkObjectResult)c2.Get()).Value!;

        Assert.True(model.KeyLost);
        Assert.False(model.Configured);
        Assert.Contains("Enter it again", model.KeyMessage);
        Assert.DoesNotContain(StoredKey, JsonSerializer.Serialize(model));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a space in it 12345")]
    public void A_malformed_key_is_refused_and_nothing_is_stored(string key)
    {
        var (c, store, settings, _) = Make();

        IActionResult result = c.SaveKey(new SaveKeyRequest(key));

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, ((ObjectResult)result).StatusCode);
        Assert.Empty(store.Values);
        Assert.False(settings.IsConfigured);
    }

    [Theory]
    [InlineData(typeof(SettingsController))]
    [InlineData(typeof(VoiceController))]
    public void Only_the_settings_section_can_reach_the_key_and_rule_controllers(Type controller)
    {
        var policies = controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Select(a => a.Policy).ToList();
        Assert.Contains(AuthorizationPolicies.SectionAccessSettings, policies);
        Assert.DoesNotContain(AuthorizationPolicies.SectionAccessContent, policies);
        Assert.Empty(controller.GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
    }

    [Fact]
    public void Every_controller_in_the_package_requires_a_section_policy()
    {
        var controllers = typeof(SettingsController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(controllers.Count >= 4);
        foreach (Type c in controllers)
        {
            var own = c.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Select(a => a.Policy).ToList();
            Assert.Contains(own, p => p == AuthorizationPolicies.SectionAccessSettings || p == AuthorizationPolicies.SectionAccessContent);
        }
    }

    [Fact]
    public async Task A_failed_test_gives_a_plain_reason_and_never_echoes_the_key_or_the_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid x-api-key " + StoredKey + "\"}}", Encoding.UTF8, "application/json"),
        });
        var log = new ListLogger<ClaudeClient>();
        var client = new ClaudeClient(new StubFactory(handler), Opts.Effective(new AiAssistantOptions()), log);

        KeyTestResult result = await client.TestAsync(StoredKey, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("rejected the key", result.Message);
        Assert.DoesNotContain(StoredKey, result.Message);
        Assert.DoesNotContain(StoredKey, string.Join('\n', log.Lines));
        Assert.Equal(StoredKey, handler.Seen.Single().Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task The_client_uses_the_saved_key_when_configuration_has_none()
    {
        var store = new MemoryKeyValueStore();
        var prot = new EphemeralDataProtectionProvider();
        var keys = new ApiKeyStore(store, prot, TimeProvider.System);
        keys.Save(StoredKey, "owner");
        var effective = new EffectiveSettings(Opts.Monitor(new AiAssistantOptions()), new SettingsStore(store), keys);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""", Encoding.UTF8, "application/json"),
        });
        var client = new ClaudeClient(new StubFactory(handler), effective, NullLogger<ClaudeClient>.Instance);

        await client.CompleteAsync(new ClaudeRequest("s", "u"), CancellationToken.None);

        Assert.Equal(StoredKey, handler.Seen.Single().Headers.GetValues("x-api-key").Single());
    }
}
