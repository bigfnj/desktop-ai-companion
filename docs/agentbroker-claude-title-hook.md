# Optional Claude custom titles

Claude documents `session_title` on `SessionStart` and `UserPromptSubmit` as a custom
title. Generated titles are excluded. See the [Claude hook input reference](https://code.claude.com/docs/en/hooks#sessionstart-input).

The opt-in worker command reads these events from stdin and publishes the custom
title as untrusted local metadata. It verifies the hook's canonical session ID
against `whoami` before setting the exact current Claude row. Native labels and
routing, manual declarations and availability remain separate. Missing custom
titles clear the hint. It never opens a transcript or derives a title from a prompt.

Add command hooks manually in your chosen Claude settings. Replace both example
paths with the installed worker executable and its existing broker data directory:

```json
{
  "hooks": {
    "SessionStart": [{
      "hooks": [{
        "type": "command",
        "command": "C:/Example/AgentBroker.Worker.exe",
        "args": ["--claude-title-hook", "--data", "C:/Example/BrokerData"],
        "timeout": 5
      }]
    }],
    "UserPromptSubmit": [{
      "hooks": [{
        "type": "command",
        "command": "C:/Example/AgentBroker.Worker.exe",
        "args": ["--claude-title-hook", "--data", "C:/Example/BrokerData"],
        "timeout": 5
      }]
    }]
  }
}
```

The bridge makes one identity lookup and at most one title update, with a two-second
observation deadline and up to 250 milliseconds for output cleanup. Failure exits
zero, emits only fixed status text to stderr and requests suppressed hook output.
The synchronous hook can delay prompt submission but never rejects it or edits Claude settings. A session
not yet discovered can update on its next prompt. The broker requires fresh verified
native receive eligibility; a generic or observation-only row cannot set the hint.

Titles are bounded to 160 characters and 256 exact addresses per worker, have no
availability TTL, and are lost on worker restart. An undiscovered address is hidden;
a new incarnation inherits nothing. Paired brokers receive no local titles.
