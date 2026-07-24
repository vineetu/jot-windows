# Jot Sie — live PFB AI Gateway test.
# Run on a Sony-network Windows machine (Okta sign-in required). Verifies the gateway answers:
# gets a token via the Sony CLI, POSTs one small completion, prints PASS/FAIL + the reply.
# Copy everything it prints and paste it back to Vineet.

$ErrorActionPreference = 'Stop'
$log = New-Object System.Collections.Generic.List[string]
function Say($m) { $log.Add($m); Write-Host $m }

Say "== Jot Sie gateway test =="
try {
    $cli = Join-Path $env:LOCALAPPDATA 'gimme-ai-creds.exe'
    if (-not (Test-Path $cli)) {
        Say "Downloading sign-in helper..."
        Invoke-WebRequest -UseBasicParsing `
            -Uri 'https://download.ai.studios.playstation.com/ai-gateway-cli/binaries/gimme-ai-creds-windows-amd64.exe' `
            -OutFile $cli
    }

    Say "Getting token (an Okta browser window may open — complete sign-in)..."
    $token = (& $cli --org pfb --quiet --no-clipboard | Out-String).Trim()
    if (-not $token) { throw "CLI returned no token" }
    Say ("Token acquired (length {0})." -f $token.Length)

    $body = '{"model":"gpt-5.6-luna","messages":[{"role":"user","content":"Say hello in exactly 5 words."}],"stream":false,"reasoning_effort":"none"}'
    Say "Calling gateway (gpt-5.6-luna)..."
    $resp = Invoke-RestMethod -Method Post `
        -Uri 'https://ai-gateway.dspprod.bis.sie.sony.com/pfb/common/v1/chat/completions' `
        -Headers @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' } `
        -Body $body
    $reply = $resp.choices[0].message.content
    Say ("GATEWAY REPLY: {0}" -f $reply)
    Say "RESULT: PASS"
}
catch {
    Say ("RESULT: FAIL -- {0}" -f $_.Exception.Message)
    if ($_.Exception.Response) {
        try {
            $sr = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            Say ("HTTP body: {0}" -f $sr.ReadToEnd())
        } catch {}
    }
}

$out = Join-Path $env:TEMP 'jot-sie-gateway-test.txt'
$log -join "`r`n" | Set-Content -Encoding utf8 $out
Say ""
Say "Saved to $out — copy everything above and paste it back to Vineet."
