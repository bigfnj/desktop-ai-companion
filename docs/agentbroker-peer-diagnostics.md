# Paired-broker admission and clock diagnostics

A message submission to a paired broker can now preserve an authenticated
admission refusal. This applies only to HTTP 409 on the submission endpoint,
an `application/json` response with one error property, and the known admission codes:

- recipient_unavailable
- recipient_cannot_receive
- private_session
- recipient_queue_full
- message_limit

The response body is bounded to 1024 bytes. Unknown codes, extra or duplicate
properties, malformed data and unsuccessful transport outcomes stay uncertain.
The saved pairing authorization is checked again after a refusal body arrives.
Revoking and re-pairing the same broker invalidates the earlier in-flight result.

The paired-brokers pane and local peer diagnostics also report clock observations.
They use the Date header from a directly authenticated paired HTTPS response.
Offset means the peer's time minus this PC's time. The comparison uses the local
request midpoint measured with a monotonic clock; uncertainty includes half the
round trip plus one second for the Date header's precision.

A warning is shown only when the absolute offset exceeds five seconds plus its
uncertainty. A round trip over two seconds is unsuitable for a warning. Missing
or malformed Date values, a local clock discontinuity over one second, a
transport failure before response headers and an observation older than five
minutes are reported explicitly. An unavailable comparison is never reported as
zero offset.

Observations are local and bounded to 32 paired brokers. They are tied to the
current pairing authorization and contain no agent message text. They do not
change the federation message format or the schema-1 durable store. No clock is
set, and no expiry, lease or admission decision uses the observed offset.
