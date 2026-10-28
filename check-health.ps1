# check-health.ps1 — Probes /health/ready across all LocalStay services
$services = @(
    @{ Name = "CatalogService";     Url = "http://localhost:5101/health/ready" },
    @{ Name = "BookingService";     Url = "http://localhost:5102/health/ready" },
    @{ Name = "NotificationService"; Url = "http://localhost:5103/health/ready" },
    @{ Name = "ConciergeService";   Url = "http://localhost:5104/health" },
    @{ Name = "ApiGateway";         Url = "http://localhost:5000/health" }
)

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "   LocalStay Services Health Check       " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

$allPass = $true

foreach ($svc in $services) {
    try {
        $response = Invoke-RestMethod -Uri $svc.Url -Method Get -TimeoutSec 3 -ErrorAction Stop
        Write-Host "PASS: $($svc.Name) -> $($svc.Url)" -ForegroundColor Green
        Write-Host "      Details: $($response | ConvertTo-Json -Compress)" -ForegroundColor Gray
    }
    catch {
        $allPass = $false
        Write-Host "FAIL: $($svc.Name) -> $($svc.Url)" -ForegroundColor Red
        if ($_.Exception.Response) {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $body = $reader.ReadToEnd()
            Write-Host "      Error: $($_.Exception.Message) - $body" -ForegroundColor Yellow
        } else {
            Write-Host "      Error: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

Write-Host "=========================================" -ForegroundColor Cyan
if ($allPass) {
    Write-Host "ALL SERVICES HEALTHY" -ForegroundColor Green
    exit 0
} else {
    Write-Host "ONE OR MORE SERVICES FAILED HEALTH CHECK" -ForegroundColor Red
    exit 1
}
