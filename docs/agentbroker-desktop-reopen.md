# Reopen an existing Codex desktop chat

AgentBroker's options include a **Reopen a Codex desktop chat** card. Select the
chat's original app and enter the lowercase UUID from its desktop chat link, then
choose **Open existing desktop chat**. These two inputs are temporary and are not
saved in broker settings.

The action requires an explicit **Desktop app** selection. A Codex session ID or
the kind of router serving a session does not establish which GUI originally held
that chat. For a VS Code chat, reopen the original conversation in VS Code.

The helper uses the documented `codex://threads/<thread-id>` desktop deep link.
It requests URI activation through Windows and reports whether Windows accepted
that request. It does not confirm that the chat loaded, that AgentBroker can
reach it, or that a message was received.

Opening a chat and sending a broker message are separate actions. Delivery still
requires normal fresh owner discovery, adapter trust checks, native permissions
and idle admission. This action supplies no prompt, does not request a new chat,
and starts no independent Codex engine. The UUID's existence is not checked by
AgentBroker; the desktop app decides how to handle a valid but unknown ID.

An activation already requested by the user may finish after AgentBroker shuts
down. It does not start another task or change agent permissions. Invocations
begun after module shutdown are refused.

The [official OpenAI desktop commands documentation](https://learn.chatgpt.com/docs/reference/commands#deep-links)
defines the existing-chat deep link. Its composer-prefill parameters apply to new
chat links, not this existing-chat action.

Synthetic checks use an injected URI launcher and run with AgentBroker's module
self-test. They do not open a GUI or establish live wake acceptance.

Live acceptance must also try an unknown but syntactically valid thread ID and
record the desktop app's visible response. A successful Windows activation alone
does not establish that the requested chat exists or loaded.
