namespace Discagem.Server.Options;

/// <summary>Root configuration section ("Discagem").</summary>
public sealed class DiscagemOptions
{
    public const string SectionName = "Discagem";

    /// <summary>Name shown to clients.</summary>
    public string ServerName { get; set; } = "Discagem";

    /// <summary>Directory for the SQLite database, uploaded files and generated keys.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Invite code required to create an account. When empty, only the very first account
    /// (which becomes the administrator) can be registered.
    /// </summary>
    public string InviteCode { get; set; } = "";

    /// <summary>Maximum size of a single uploaded file, in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;

    public JwtOptions Jwt { get; set; } = new();

    public MediaOptions Media { get; set; } = new();
}

public sealed class JwtOptions
{
    /// <summary>
    /// HMAC signing key (at least 32 characters). When empty, a random key is generated
    /// and stored in the data directory.
    /// </summary>
    public string Key { get; set; } = "";

    public string Issuer { get; set; } = "discagem";

    public int LifetimeDays { get; set; } = 30;
}

public sealed class MediaOptions
{
    /// <summary>
    /// Public IP address announced in ICE candidates. Needed when the server sits behind 1:1 NAT
    /// (e.g. cloud instances whose interface only has a private address). Leave empty otherwise.
    /// </summary>
    public string PublicIpAddress { get; set; } = "";

    /// <summary>Optional local address to bind media sockets to.</summary>
    public string BindAddress { get; set; } = "";

    /// <summary>First UDP port used for WebRTC media (must be even).</summary>
    public int RtpPortStart { get; set; } = 50000;

    /// <summary>Last UDP port used for WebRTC media. Each media session uses one port.</summary>
    public int RtpPortEnd { get; set; } = 50999;

    /// <summary>UDP port of the built-in STUN responder (0 disables it).</summary>
    public int StunPort { get; set; } = 3478;

    /// <summary>Additional ICE servers handed to clients (e.g. a TURN server).</summary>
    public List<IceServerOptions> IceServers { get; set; } = [];

    /// <summary>Maximum number of viewers per stream.</summary>
    public int MaxViewersPerStream { get; set; } = 25;
}

public sealed class IceServerOptions
{
    public string Urls { get; set; } = "";

    public string? Username { get; set; }

    public string? Credential { get; set; }
}
