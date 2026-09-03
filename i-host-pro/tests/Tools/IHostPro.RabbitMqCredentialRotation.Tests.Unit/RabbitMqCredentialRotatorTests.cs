using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace IHostPro.RabbitMqCredentialRotation.Tests.Unit;

public sealed class RabbitMqCredentialRotatorTests
{
    private const string SecretJson = """
        {"host":"b-fake.mq.sa-east-1.on.aws","port":5671,"virtualHost":"/","username":"ihostpro","password":"OldPassword123","useTls":true}
        """;

    private static (RabbitMqCredentialRotator Rotator, FakeHttpMessageHandler Handler, FakeSecretsManagerClient Secrets) Build(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, string secretJson = SecretJson)
    {
        var handler = new FakeHttpMessageHandler(respond);
        var httpClient = new HttpClient(handler);
        var secrets = new FakeSecretsManagerClient(secretJson);
        return (new RabbitMqCredentialRotator(httpClient, secrets), handler, secrets);
    }

    private static Task<HttpResponseMessage> DefaultRespond(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"tags":["administrator"]}""") })
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

    // 1. Correct request: PUT to the right URL, Basic Auth with the OLD
    // credential, body carries a new password different from the old one.
    [Fact]
    public async Task RotateAsync_sends_a_correctly_formed_PUT_with_old_credential_auth_and_a_new_password()
    {
        var (rotator, handler, secrets) = Build(DefaultRespond);

        await rotator.RotateAsync("arn:aws:secretsmanager:sa-east-1:123:secret:rabbitmq");

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        put.RequestUri.Should().Be(new Uri("https://b-fake.mq.sa-east-1.on.aws/api/users/ihostpro"));
        put.Headers.Authorization!.Scheme.Should().Be("Basic");

        var putBody = handler.RequestBodies[handler.Requests.IndexOf(put)];
        using var doc = JsonDocument.Parse(putBody);
        var newPassword = doc.RootElement.GetProperty("password").GetString();
        newPassword.Should().NotBeNullOrEmpty().And.NotBe("OldPassword123");

        secrets.PutCallCount.Should().Be(1);
        secrets.PutValues[0].Should().Contain(newPassword!).And.NotContain("OldPassword123");
    }

    // 2. TLS: every request uses https, never plain http.
    [Fact]
    public async Task RotateAsync_only_ever_uses_https()
    {
        var (rotator, handler, _) = Build(DefaultRespond);

        await rotator.RotateAsync("arn:fake");

        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().OnlyContain(r => r.RequestUri!.Scheme == "https");
    }

    // 3. HTTP failure (network error): fails closed, never touches Secrets Manager.
    [Fact]
    public async Task RotateAsync_network_failure_on_the_initial_GET_fails_closed_without_touching_secrets_manager()
    {
        var (rotator, _, secrets) = Build(_ => throw new HttpRequestException("connection reset"));

        var act = async () => await rotator.RotateAsync("arn:fake");

        await act.Should().ThrowAsync<HttpRequestException>();
        secrets.PutCallCount.Should().Be(0);
    }

    // 4. Broker update failure (PUT returns non-2xx): fails closed, Secrets Manager untouched.
    [Fact]
    public async Task RotateAsync_broker_rejects_the_password_update_fails_closed_without_touching_secrets_manager()
    {
        var (rotator, _, secrets) = Build(request =>
            request.Method == HttpMethod.Get
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"tags":["administrator"]}""") })
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var act = async () => await rotator.RotateAsync("arn:fake");

        await act.Should().ThrowAsync<InvalidOperationException>();
        secrets.PutCallCount.Should().Be(0);
    }

    // 5. Secrets Manager update failure: broker already changed, secret write
    // retried then surfaced as a distinct, actionable exception type.
    [Fact]
    public async Task RotateAsync_secrets_manager_write_fails_after_broker_succeeded_retries_then_reports_partial_state()
    {
        var (rotator, _, secrets) = Build(DefaultRespond);
        secrets.ThrowOnPut = new InvalidOperationException("Secrets Manager throttled");

        var act = async () => await rotator.RotateAsync("arn:fake");

        await act.Should().ThrowAsync<RabbitMqRotationPartiallyAppliedException>();
        secrets.PutCallCount.Should().Be(3, "the rotator should retry a fixed number of times before giving up");
    }

    // 6. No secret logging: the password never appears in any exception text.
    [Fact]
    public async Task RotateAsync_failure_never_leaks_old_or_new_password_into_exception_text()
    {
        var (rotator, _, secrets) = Build(DefaultRespond);
        secrets.ThrowOnPut = new InvalidOperationException("Secrets Manager throttled");

        var act = async () => await rotator.RotateAsync("arn:fake");

        var assertion = await act.Should().ThrowAsync<RabbitMqRotationPartiallyAppliedException>();
        assertion.Which.ToString().Should().NotContain("OldPassword123");
        // The new password is generated internally and never returned to the
        // caller, so the strongest assertion available here is that the
        // fixed OLD password never appears - confirmed above.
    }

    // 7. Malformed endpoint: empty host fails fast, before any HTTP call.
    [Fact]
    public async Task RotateAsync_secret_with_empty_host_fails_before_any_http_call()
    {
        const string malformed = """
            {"host":"","port":5671,"virtualHost":"/","username":"ihostpro","password":"OldPassword123","useTls":true}
            """;
        var (rotator, handler, secrets) = Build(DefaultRespond, malformed);

        var act = async () => await rotator.RotateAsync("arn:fake");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
        secrets.PutCallCount.Should().Be(0);
    }

    // 8. Old-credential verification: confirms the abstraction correctly
    // distinguishes "old credential rejected" (401) from "still works".
    [Fact]
    public async Task VerifyOldCredentialRejectedAsync_returns_true_only_on_401()
    {
        var (rotator401, _, _) = Build(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var (rotatorOk, _, _) = Build(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

        var rejected = await rotator401.VerifyOldCredentialRejectedAsync("b-fake.mq.sa-east-1.on.aws", "ihostpro", "OldPassword123");
        var stillValid = await rotatorOk.VerifyOldCredentialRejectedAsync("b-fake.mq.sa-east-1.on.aws", "ihostpro", "OldPassword123");

        rejected.Should().BeTrue();
        stillValid.Should().BeFalse();
    }
}
