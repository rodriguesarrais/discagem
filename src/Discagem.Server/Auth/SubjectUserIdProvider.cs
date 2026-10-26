using Microsoft.AspNetCore.SignalR;

namespace Discagem.Server.Auth;

/// <summary>Uses the token subject (the numeric user id) as the SignalR user identifier.</summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(DiscagemClaims.Subject)?.Value;
}
