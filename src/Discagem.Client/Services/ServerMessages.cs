using System.Text.RegularExpressions;
using Discagem.Client.Resources;

namespace Discagem.Client.Services;

/// <summary>
/// Translates the English text the server sends (API errors, hub errors and the reasons media
/// sessions are closed) into the interface language. Messages it does not know, from older or
/// newer servers, are returned unchanged.
/// </summary>
internal static partial class ServerMessages
{
    private static readonly Dictionary<string, Func<string>> Messages = new(StringComparer.Ordinal)
    {
        // Accounts (Auth/AccountRules.cs, Endpoints/AuthEndpoints.cs)
        ["Username is required."] = () => Strings.Server_UsernameRequired,
        ["Username may only contain letters, digits, '_' and '.'."] = () => Strings.Server_UsernameCharacters,
        ["Password is too long."] = () => Strings.Server_PasswordTooLong,
        ["Invalid invite code."] = () => Strings.Server_InvalidInviteCode,
        ["Registration is closed on this server."] = () => Strings.Server_RegistrationClosed,
        ["That username is already taken."] = () => Strings.Server_UsernameTaken,
        ["Username and password are required."] = () => Strings.Server_CredentialsRequired,
        ["Invalid username or password."] = () => Strings.Server_InvalidCredentials,
        ["Display name cannot be empty."] = () => Strings.Server_DisplayNameEmpty,
        ["Avatar colour must be in #RRGGBB format."] = () => Strings.Server_AvatarColorFormat,
        ["The current password is incorrect."] = () => Strings.Server_CurrentPasswordIncorrect,

        // Uploads (Endpoints/AttachmentEndpoints.cs, Endpoints/AvatarEndpoints.cs)
        ["Expected a multipart/form-data request."] = () => Strings.Server_ExpectedMultipart,
        ["No file was uploaded."] = () => Strings.Server_NoFileUploaded,
        ["No photo was uploaded."] = () => Strings.Server_NoPhotoUploaded,
        ["Profile photos must be PNG or JPEG images."] = () => Strings.Server_AvatarFormat,

        // Chat (Hubs/ChatHub.cs)
        ["Your account no longer exists."] = () => Strings.Server_AccountGone,
        ["Messages cannot be empty."] = () => Strings.Server_MessageEmpty,
        ["That text channel does not exist."] = () => Strings.Server_TextChannelNotFound,
        ["One or more attachments are invalid."] = () => Strings.Server_AttachmentsInvalid,
        ["That message does not exist."] = () => Strings.Server_MessageNotFound,
        ["You can only edit your own messages."] = () => Strings.Server_EditOwnMessagesOnly,
        ["You can only delete your own messages."] = () => Strings.Server_DeleteOwnMessagesOnly,
        ["Unknown channel type."] = () => Strings.Server_UnknownChannelType,
        ["That channel does not exist."] = () => Strings.Server_ChannelNotFound,
        ["The last text channel cannot be deleted."] = () => Strings.Server_LastTextChannel,
        ["That voice channel does not exist."] = () => Strings.Server_VoiceChannelNotFound,
        ["Only administrators can manage channels."] = () => Strings.Server_AdminsOnly,
        ["Channel names cannot be empty."] = () => Strings.Server_ChannelNameEmpty,
        ["Channel names must contain letters or digits."] = () => Strings.Server_ChannelNameCharacters,

        // Screen shares and calls (Media/SfuService.cs, Media/VoiceService.cs)
        ["Unsupported stream format."] = () => Strings.Server_UnsupportedStreamFormat,
        ["Missing session description."] = () => Strings.Server_MissingSessionDescription,
        ["That stream is no longer live."] = () => Strings.Server_StreamNotLive,
        ["This stream has reached its viewer limit."] = () => Strings.Server_StreamFull,
        ["The session description could not be parsed."] = () => Strings.Server_SessionDescriptionInvalid,
        ["This voice channel is full."] = () => Strings.Server_VoiceChannelFull,
    };

    /// <summary>Messages carrying a number or a detail, as (pattern, format with {0}, {1}...).</summary>
    private static readonly (Regex Pattern, Func<string> Format)[] Patterns =
    [
        (UsernameLength(), () => Strings.Server_UsernameLength),
        (PasswordLength(), () => Strings.Server_PasswordLength),
        (FileTooLarge(), () => Strings.Server_FileTooLarge),
        (AvatarTooLarge(), () => Strings.Server_AvatarTooLarge),
        (TooManyAttachments(), () => Strings.Server_TooManyAttachments),
        (MessageTooLong(), () => Strings.Server_MessageTooLong),
        (SessionDescriptionRejected(), () => Strings.Server_SessionDescriptionRejected),
    ];

    /// <summary>Why the server closed a call or a screen share session (lower case, used inside a sentence).</summary>
    private static readonly Dictionary<string, Func<string>> Reasons = new(StringComparer.Ordinal)
    {
        ["joined from elsewhere"] = () => Strings.Server_ReasonJoinedElsewhere,
        ["channel deleted"] = () => Strings.Server_ReasonChannelDeleted,
        ["connection failed"] = () => Strings.Server_ReasonConnectionFailed,
        ["closed"] = () => Strings.Server_ReasonClosed,
        ["replaced"] = () => Strings.Server_ReasonReplaced,
        ["stream ended"] = () => Strings.Server_ReasonStreamEnded,
        ["stopped by publisher"] = () => Strings.Server_ReasonStoppedByPublisher,
        ["publisher disconnected"] = () => Strings.Server_ReasonPublisherDisconnected,
        ["server shutting down"] = () => Strings.Server_ReasonServerShuttingDown,
    };

    /// <summary>Translates an error message from the server (an API error or a hub error).</summary>
    public static string Translate(string message)
    {
        var text = message.Trim();
        if (Messages.TryGetValue(text, out var known))
        {
            return known();
        }

        foreach (var (pattern, format) in Patterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                var values = Enumerable.Range(1, match.Groups.Count - 1).Select(i => (object)match.Groups[i].Value).ToArray();
                return string.Format(format(), values);
            }
        }

        return message;
    }

    /// <summary>Translates the reason the server gives when it closes a media session.</summary>
    public static string TranslateReason(string reason) =>
        Reasons.TryGetValue(reason.Trim(), out var known) ? known() : reason;

    [GeneratedRegex(@"^Username must be between (\d+) and (\d+) characters\.$")]
    private static partial Regex UsernameLength();

    [GeneratedRegex(@"^Password must be at least (\d+) characters\.$")]
    private static partial Regex PasswordLength();

    [GeneratedRegex(@"^Files may be at most (\d+) MB\.$")]
    private static partial Regex FileTooLarge();

    [GeneratedRegex(@"^Profile photos may be at most (\d+) MB\.$")]
    private static partial Regex AvatarTooLarge();

    [GeneratedRegex(@"^At most (\d+) attachments per message\.$")]
    private static partial Regex TooManyAttachments();

    [GeneratedRegex(@"^Messages can be at most (\d+) characters long\.$")]
    private static partial Regex MessageTooLong();

    [GeneratedRegex(@"^The session description was rejected \((.*)\)\.$")]
    private static partial Regex SessionDescriptionRejected();
}
