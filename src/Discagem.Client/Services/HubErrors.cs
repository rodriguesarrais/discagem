using Microsoft.AspNetCore.SignalR;

namespace Discagem.Client.Services;

/// <summary>Turns errors from the server (hub or REST) into sentences for the user.</summary>
public static class HubErrors
{
    public static string Describe(Exception ex)
    {
        var message = ex switch
        {
            HubException hub => StripHubPrefix(hub.Message),
            ApiException api => api.Message,
            InvalidOperationException op => op.Message,
            _ => ex.Message,
        };
        return message.TrimEnd('.') + ".";
    }

    /// <summary>The server's own message, without the "An unexpected error occurred invoking..." prefix.</summary>
    private static string StripHubPrefix(string text)
    {
        const string marker = "HubException: ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? text[(index + marker.Length)..] : text;
    }
}
