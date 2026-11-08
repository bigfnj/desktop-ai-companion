namespace AgentBroker.Worker.Federation;

// Only the transport's failed socket-connect boundary creates this proof. A generic HTTP failure,
// a TLS failure after connect, or an interrupted response cannot establish non-submission.
internal sealed class PeerSubmissionNotStartedException(Guid brokerId, Exception inner)
    : IOException("The paired connection failed before an HTTP request could start.", inner)
{
    internal Guid BrokerId { get; } = brokerId;
}
