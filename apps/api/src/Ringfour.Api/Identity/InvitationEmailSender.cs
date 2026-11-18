using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using System.Text.Json.Serialization;

namespace Ringfour.Api.Identity;

public sealed class InvitationEmailSender(IConfiguration configuration, ILogger<InvitationEmailSender> logger)
{
    public async Task SendAsync(
        string address,
        string organizationName,
        OrgRole role,
        string token,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var host = configuration["Email:SmtpHost"];
        var fromAddress = configuration["Email:FromAddress"];
        var origin = configuration["Email:ConsoleOrigin"];
        var port = configuration.GetValue("Email:SmtpPort", 587);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress) ||
            string.IsNullOrWhiteSpace(origin) || port is < 1 or > 65535 ||
            !MailboxAddress.TryParse(fromAddress, out var from) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var consoleUri) ||
            consoleUri.Scheme is not ("http" or "https"))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "email_unavailable", "Invitation email settings are missing or invalid on this Platform.");
        }

        var uri = new UriBuilder(consoleUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/")
        {
            Fragment = "invite=" + Uri.EscapeDataString(token),
        };
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(configuration["Email:FromName"] ?? "Ringfour", from.Address));
        message.To.Add(MailboxAddress.Parse(address));
        message.Subject = $"Join {organizationName} on Ringfour";
        message.Body = new TextPart("plain")
        {
            Text = $"You have been invited to join {organizationName} on Ringfour as {role.ToString().ToLowerInvariant()}.\n\n" +
                   $"Open this link to sign in or create an account and accept the invitation:\n{uri.Uri}\n\n" +
                   $"This invitation expires {expiresAt:yyyy-MM-dd HH:mm} UTC. If you were not expecting this email, you can ignore it.",
        };

        using var client = new SmtpClient();
        client.Timeout = 15_000;
        try
        {
            var security = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(host, port, security, cancellationToken);
            var username = configuration["Email:SmtpUsername"];
            var password = configuration["Email:SmtpPassword"];
            if (!string.IsNullOrWhiteSpace(username))
            {
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("SMTP password is missing.");
                }
                await client.AuthenticateAsync(username, password, cancellationToken);
            }
            await client.SendAsync(new SmtpSendRequest(message), cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not send organization invitation email");
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "email_delivery_failed", "The invitation could not be delivered. Retry from the organization view.");
        }
    }
}

public sealed class Auth0UserInfoClient(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<Auth0UserProfile?> GetProfileAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var domain = configuration["Auth0:Domain"];
        if (string.IsNullOrWhiteSpace(domain)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{domain.TrimEnd('/')}/userinfo");
        request.Headers.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(authorization);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            Auth0UserInfo? profile;
            try
            {
                profile = await response.Content.ReadFromJsonAsync<Auth0UserInfo>(cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
            if (profile is null) return null;
            return new Auth0UserProfile(
                profile.EmailVerified ? profile.Email : null,
                profile.EmailVerified,
                profile.Name,
                profile.Picture,
                profile.PhoneNumber);
        }
    }

    public async Task<VerifiedEmail> GetVerifiedEmailAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.Unauthenticated("A valid access token is required.");
        }

        var domain = configuration["Auth0:Domain"];
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "identity_unavailable", "Identity profile lookup is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{domain.TrimEnd('/')}/userinfo");
        request.Headers.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(authorization);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw ApiException.Unauthenticated("The access token could not read the account email.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "identity_unavailable", "The identity provider could not verify the account email.");
        }

        var profile = await response.Content.ReadFromJsonAsync<Auth0UserInfo>(cancellationToken);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Email) || !profile.EmailVerified)
        {
            throw ApiException.Forbidden("Accepting an organization invitation requires a verified email address.");
        }
        return new VerifiedEmail(profile.Email, profile.Email.Trim().ToUpperInvariant());
    }

    private sealed record Auth0UserInfo(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool EmailVerified,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("picture")] string? Picture,
        [property: JsonPropertyName("phone_number")] string? PhoneNumber);
}

public sealed record VerifiedEmail(string Address, string NormalizedAddress);
public sealed record Auth0UserProfile(string? Email, bool EmailVerified, string? Name, string? Picture, string? PhoneNumber);
