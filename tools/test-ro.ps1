<#
.SYNOPSIS
  Test pe aparat real pentru driverul ro.dt.x.isl (Datecs RO, inclusiv modelele MX).

.DESCRIPTION
  Trimite către serviciul ErpNet.FP pornit local un set mic de bonuri cu sume mici (0.10 - 0.50 lei)
  și verifică răspunsurile. ATENȚIE: pe un aparat fiscalizat bonurile sunt vânzări reale.

  Rezultatul se scrie în consolă și în test-ro-<data>.log, lângă script.
  Trimite fișierul .log împreună cu logul serviciului (folderul ErpNet.FP\logs) dacă un pas pică.

.EXAMPLE
  .\test-ro.ps1 -Printer casa1
  .\test-ro.ps1 -Printer casa1 -BaseUrl http://localhost:8001 -WithXReport
#>
param(
    [Parameter(Mandatory = $true)][string]$Printer,
    [string]$BaseUrl = "http://localhost:8001",
    [switch]$WithXReport
)

$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = Join-Path $PSScriptRoot "test-ro-$stamp.log"
$runId = Get-Date -Format "HHmmss"
$results = @()

function Log([string]$text) {
    Write-Host $text
    Add-Content -Path $logFile -Value $text -Encoding UTF8
}

function Call([string]$method, [string]$path, $body = $null) {
    $uri = "$BaseUrl/printers/$Printer$path"
    $json = if ($body -ne $null) { $body | ConvertTo-Json -Depth 10 } else { $null }
    Log ">>> $method $uri"
    if ($json) { Log $json }
    try {
        if ($json) {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
            $r = Invoke-RestMethod -Method $method -Uri $uri -Body $bytes -ContentType "application/json; charset=utf-8" -TimeoutSec 120
        } else {
            $r = Invoke-RestMethod -Method $method -Uri $uri -TimeoutSec 120
        }
    } catch {
        Log "<<< HTTP ERROR: $($_.Exception.Message)"
        return $null
    }
    Log ("<<< " + ($r | ConvertTo-Json -Depth 10))
    return $r
}

function Check([string]$name, [bool]$ok, [string]$details = "") {
    $script:results += [pscustomobject]@{ Test = $name; Result = $(if ($ok) { "OK" } else { "FAIL" }); Details = $details }
    Log ("=== {0}: {1} {2}" -f $name, $(if ($ok) { "OK" } else { "FAIL" }), $details)
    Log ""
}

function IsOk($r) { return ($r -ne $null) -and ($r.ok -eq $true) }

Log "ErpNet.FP RO test, printer '$Printer', $BaseUrl, $(Get-Date)"
Log ""

# 1. Device info
$info = $null
try { $info = Invoke-RestMethod -Uri "$BaseUrl/printers/$Printer" -TimeoutSec 30 } catch { }
if ($info -eq $null) {
    Log "Printer '$Printer' nu exista. Imprimante vazute de serviciu:"
    try { Log ((Invoke-RestMethod -Uri "$BaseUrl/printers") | ConvertTo-Json -Depth 5) } catch { Log $_.Exception.Message }
    exit 1
}
Log ($info | ConvertTo-Json -Depth 5)
Check "1. Info aparat" ($info.serialNumber -ne $null -and $info.serialNumber -ne "") "model=$($info.model) serie=$($info.serialNumber) fw=$($info.firmwareVersion)"

# 2. Status (date/time)
$r = Call "GET" "/status"
Check "2. Status" (IsOk $r) "ora aparat=$($r.deviceDateTime)"

# 3. Simple cash receipt, no payments -> full cash
$r = Call "POST" "/receipt" @{
    uniqueSaleNumber = "TEST-$runId-03"
    items = @(@{ text = "Test bon simplu"; quantity = 1; unitPrice = 0.10; taxGroup = 1 })
}
Check "3. Bon simplu cash" ((IsOk $r) -and $r.receiptNumber) "nr=$($r.receiptNumber) suma=$($r.receiptAmount)"

# 4. Card + cash with change (issue #97)
$r = Call "POST" "/receipt" @{
    uniqueSaleNumber = "TEST-$runId-04"
    items = @(@{ text = "Test plata mixta"; quantity = 1; unitPrice = 0.20; taxGroup = 1 })
    payments = @(
        @{ amount = 0.50; paymentType = "cash" },
        @{ amount = 0.10; paymentType = "card" },
        @{ amount = -0.40; paymentType = "change" }
    )
}
Check "4. Card + cash + rest" ((IsOk $r) -and $r.receiptNumber) "nr=$($r.receiptNumber) suma=$($r.receiptAmount)"

# 5. Comments, unit of measure, item discount, subtotal discount
$r = Call "POST" "/receipt" @{
    uniqueSaleNumber = "TEST-$runId-05"
    items = @(
        @{ type = "comment"; text = "Comentariu inceput bon" },
        @{ text = "Test kg"; quantity = 0.5; unitPrice = 0.40; taxGroup = 1; unit = "kg" },
        @{ text = "Test discount"; quantity = 1; unitPrice = 0.30; taxGroup = 1; priceModifierType = "discount-percent"; priceModifierValue = 10 },
        @{ type = "discount-amount"; amount = 0.02 },
        @{ type = "footer-comment"; text = "Comentariu final" }
    )
}
# 0.20 + 0.27 - 0.02 = 0.45
Check "5. Comentarii, UM kg, discounturi" ((IsOk $r) -and $r.receiptNumber -and [decimal]$r.receiptAmount -eq 0.45) "nr=$($r.receiptNumber) suma=$($r.receiptAmount) (asteptat 0.45)"

# 6. Same USN twice -> printed once
$body = @{
    uniqueSaleNumber = "TEST-$runId-06"
    items = @(@{ text = "Test USN dublu"; quantity = 1; unitPrice = 0.10; taxGroup = 1 })
}
$first = Call "POST" "/receipt" $body
$second = Call "POST" "/receipt" $body
Check "6. Acelasi USN nu se tipareste de doua ori" ((IsOk $first) -and (IsOk $second) -and $first.receiptNumber -eq $second.receiptNumber) "nr1=$($first.receiptNumber) nr2=$($second.receiptNumber) (pe hartie trebuie sa iasa UN singur bon)"

# 7. Cash amount in drawer
$r = Call "GET" "/cash"
Check "7. Numerar in sertar" (IsOk $r) "suma=$($r.amount)"

# 8. Optional X report
if ($WithXReport) {
    $r = Call "POST" "/xreport" @{}
    Check "8. Raport X" (IsOk $r)
}

Log ""
Log "REZUMAT"
$results | Format-Table -AutoSize | Out-String | ForEach-Object { Log $_ }
Log "Log complet: $logFile"
if ($results | Where-Object { $_.Result -eq "FAIL" }) { exit 1 }
