#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, 2147483647)]
    [int] $GameProcessId
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) {
    throw 'The PartyPulse runtime status bridge requires Windows.'
}

if (-not ('PartyPulse.RuntimeStatusPipePeer' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PartyPulse
{
    public static class RuntimeStatusPipePeer
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    }
}
'@
}

function Read-StatusBytes {
    param(
        [System.IO.Pipes.NamedPipeClientStream] $Pipe,
        [byte[]] $Buffer,
        [System.Threading.CancellationToken] $CancellationToken
    )

    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $count = $Pipe.ReadAsync($Buffer, $offset, $Buffer.Length - $offset, $CancellationToken).GetAwaiter().GetResult()
        if ($count -eq 0) {
            throw 'PartyPulse closed the connection before sending a complete status response.'
        }

        $offset += $count
    }
}

$processIdText = $GameProcessId.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$pipeName = "PartyPulse.RuntimeStatus.v1.$processIdText"
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous, [System.Security.Principal.TokenImpersonationLevel]::Identification)
$deadline = [System.Threading.CancellationTokenSource]::new(5000)

try {
    [void] $pipe.ConnectAsync($deadline.Token).GetAwaiter().GetResult()

    [uint32] $serverProcessId = 0
    if (![PartyPulse.RuntimeStatusPipePeer]::GetNamedPipeServerProcessId($pipe.SafePipeHandle, [ref] $serverProcessId) -or
        $serverProcessId -ne $GameProcessId) {
        throw 'The status pipe does not belong to the selected game process.'
    }

    $request = [byte[]] @(0x01)
    [void] $pipe.WriteAsync($request, 0, $request.Length, $deadline.Token).GetAwaiter().GetResult()

    $header = [byte[]]::new(4)
    Read-StatusBytes -Pipe $pipe -Buffer $header -CancellationToken $deadline.Token
    $length = [BitConverter]::ToUInt32($header, 0)
    if ($length -lt 1 -or $length -gt 2048) {
        throw 'PartyPulse returned an invalid status response length.'
    }

    $payload = [byte[]]::new($length)
    Read-StatusBytes -Pipe $pipe -Buffer $payload -CancellationToken $deadline.Token
} catch [System.OperationCanceledException] {
    throw "Timed out waiting for runtime status from game process $GameProcessId. Check that the development bridge is loaded."
} finally {
    $pipe.Dispose()
    $deadline.Dispose()
}

try {
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $document = [System.Text.Json.JsonDocument]::Parse($utf8.GetString($payload))
    try {
        $status = $document.RootElement
        $protocolVersion = $status.GetProperty('protocolVersion').GetInt32()
        $reportedProcessId = $status.GetProperty('processId').GetInt32()
        $sessionId = $status.GetProperty('sessionId').GetGuid()
        $buildId = $status.GetProperty('buildId').GetGuid()
        $version = $status.GetProperty('version').GetString()
        $sampledAtUtc = $status.GetProperty('sampledAtUtc').GetDateTimeOffset()
        $loggedIn = $status.GetProperty('loggedIn').GetBoolean()
    } finally {
        $document.Dispose()
    }
} catch {
    throw 'PartyPulse returned a malformed status response.'
}

if ($protocolVersion -ne 1 -or $reportedProcessId -ne $GameProcessId -or
    $sessionId -eq [guid]::Empty -or $buildId -eq [guid]::Empty -or
    [string]::IsNullOrWhiteSpace($version) -or $sampledAtUtc.Offset -ne [TimeSpan]::Zero) {
    throw 'PartyPulse returned unexpected status metadata.'
}

[ordered] @{
    protocolVersion = $protocolVersion
    processId = $reportedProcessId
    sessionId = $sessionId
    buildId = $buildId
    version = $version
    sampledAtUtc = $sampledAtUtc
    loggedIn = $loggedIn
} | ConvertTo-Json -Compress
