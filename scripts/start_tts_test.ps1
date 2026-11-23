$env:HF_HUB_OFFLINE = "1"
$env:TRANSFORMERS_OFFLINE = "1"

$proc = Start-Process -FilePath "python" -ArgumentList @(
    "C:\Users\lumin\src\baihua\scripts\kokoro_tts_server.py", "--port", "8001"
) -PassThru

Write-Host "PID: $($proc.Id)"
Start-Sleep 12

$conn = Test-NetConnection -ComputerName localhost -Port 8001 -WarningAction SilentlyContinue
Write-Host "Port 8001: $($conn.TcpTestSucceeded)"

if ($conn.TcpTestSucceeded) {
    $body = '{"model":"kokoro","input":"你好世界，今天天气真不错。","voice":"zf_xiaoxiao"}'
    $resp = Invoke-WebRequest -Uri "http://localhost:8001/v1/audio/speech" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 30
    Write-Host "Chinese TTS: $($resp.RawContentLength) bytes"

    $body2 = '{"model":"kokoro","input":"Hello world","voice":"af_heart"}'
    $resp2 = Invoke-WebRequest -Uri "http://localhost:8001/v1/audio/speech" -Method Post -Body $body2 -ContentType "application/json" -TimeoutSec 30
    Write-Host "English TTS: $($resp2.RawContentLength) bytes"
} else {
    Write-Host "Server not started"
    $alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    Write-Host "Process alive: $($null -ne $alive)"
}