# Minimal mock server: emulates Ollama /api/chat (NDJSON) and OpenAI /chat/completions (SSE).
# Byte-level HTTP parsing so UTF-8 bodies (Chinese prompt text) never desync.
param([int]$Port = 18080)

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
$log = Join-Path $PSScriptRoot 'mock.log'
"listening $Port" | Out-File $log -Encoding utf8

function Read-HeaderBlock($stream) {
    $sb = New-Object System.Text.StringBuilder
    $buf = New-Object byte[] 1
    while ($sb.Length -lt 65536) {
        $n = $stream.Read($buf, 0, 1)
        if ($n -le 0) { return $null }
        [void]$sb.Append([char]$buf[0])
        if ($sb.Length -ge 4) {
            $s = $sb.ToString()
            if ($s.EndsWith("`r`n`r`n")) { return $s }
        }
    }
    return $null
}

while ($true) {
    $client = $listener.AcceptTcpClient()
    try {
        $stream = $client.GetStream()
        $headers = Read-HeaderBlock $stream
        if (-not $headers) { $client.Close(); continue }

        $first = ($headers -split "`r`n")[0]
        $path = ($first -split ' ')[1]
        $len = 0
        if ($headers -match 'Content-Length:\s*(\d+)') { $len = [int]$matches[1] }

        if ($len -gt 0) {
            $body = New-Object byte[] $len
            $read = 0
            while ($read -lt $len) {
                $n = $stream.Read($body, $read, $len - $read)
                if ($n -le 0) { break }
                $read += $n
            }
        }

        if ($path -like '*/api/chat*') {
            $resp = '{"message":{"role":"assistant","content":"收到，"}}' + "`n" +
                    '{"message":{"role":"assistant","content":"这是本地 Ollama 模拟响应。"},"done":true}' + "`n"
            $ctype = 'application/x-ndjson'
        }
        elseif ($path -like '*/chat/completions*') {
            $resp = 'data: {"choices":[{"delta":{"content":"收到，"}}]}' + "`n`n" +
                    'data: {"choices":[{"delta":{"content":"这是在线 API 模拟响应。"}}]}' + "`n`n" +
                    'data: [DONE]' + "`n`n"
            $ctype = 'text/event-stream'
        }
        else {
            $resp = '{"error":{"message":"not found"}}'
            $ctype = 'application/json'
        }

        "REQ $path body=$len" | Out-File $log -Append -Encoding utf8
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($resp)
        $head = "HTTP/1.1 200 OK`r`nContent-Type: $ctype`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($head)
        $stream.Write($hb, 0, $hb.Length)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    catch {
        "ERR $($_.Exception.Message)" | Out-File $log -Append -Encoding utf8
    }
    finally {
        try { $client.Close() } catch { }
    }
}
