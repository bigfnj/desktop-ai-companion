[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Process metadata only. Never request command lines, environment, session records or credentials.
$processes = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'claude.exe' OR Name = 'codex.exe' OR Name = 'Code.exe' OR Name = 'ChatGPT.exe'" -Property ProcessId, ParentProcessId, ExecutablePath, Name
$result = @($processes | Select-Object Name, ProcessId, ParentProcessId, ExecutablePath)
ConvertTo-Json -InputObject $result -Depth 3 -Compress
