using Discagem.Shared;

namespace Discagem.Client.Services;

/// <summary>Holds local ICE candidates until the server has assigned a session id.</summary>
internal sealed class CandidateRelay(ChatConnection hub)
{
    private readonly List<IceCandidateDto> _pending = [];
    private readonly object _sync = new();
    private string? _sessionId;

    public void OnLocalCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        IceCandidateDto dto;
        lock (_sync)
        {
            if (_sessionId is null)
            {
                _pending.Add(new IceCandidateDto("", candidate, sdpMid, sdpMLineIndex));
                return;
            }

            dto = new IceCandidateDto(_sessionId, candidate, sdpMid, sdpMLineIndex);
        }

        Send(dto);
    }

    public void SetSession(string sessionId)
    {
        List<IceCandidateDto> pending;
        lock (_sync)
        {
            _sessionId = sessionId;
            pending = _pending.Select(c => c with { SessionId = sessionId }).ToList();
            _pending.Clear();
        }

        foreach (var candidate in pending)
        {
            Send(candidate);
        }
    }

    private void Send(IceCandidateDto candidate) => _ = Task.Run(async () =>
    {
        try
        {
            await hub.AddIceCandidateAsync(candidate);
        }
        catch (Exception)
        {
            // Host candidates are already part of the offer; losing a trickled one is not fatal.
        }
    });
}
