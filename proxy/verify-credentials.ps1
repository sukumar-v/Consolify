# Checks that IGDB and SteamGridDB credentials actually work, before you spend time wiring them
# into a deployment and wondering which half is broken.
#
# Reads from environment variables so the values never land in your shell history:
#
#   $env:IGDB_CLIENT_ID     = "..."
#   $env:IGDB_CLIENT_SECRET = "..."
#   $env:SGDB_KEY           = "..."
#   .\verify-credentials.ps1
#
# Nothing is printed but pass/fail and the game titles that came back. It writes nothing to disk
# and sends the credentials only to Twitch, IGDB and SteamGridDB.

$ErrorActionPreference = "Stop"
$fail = 0
$checked = 0

function Ok($m)   { Write-Host "  PASS  $m" -ForegroundColor Green; $script:checked++ }
function Bad($m)  { Write-Host "  FAIL  $m" -ForegroundColor Red; $script:fail++; $script:checked++ }
function Skip($m) { Write-Host "  SKIP  $m" -ForegroundColor Yellow }

Write-Host "`nIGDB (via Twitch)" -ForegroundColor Cyan
if (-not $env:IGDB_CLIENT_ID -or -not $env:IGDB_CLIENT_SECRET) {
    Skip "IGDB_CLIENT_ID / IGDB_CLIENT_SECRET not set"
} else {
    $token = $null
    try {
        $res = Invoke-RestMethod -Method Post -Uri "https://id.twitch.tv/oauth2/token" -Body @{
            client_id     = $env:IGDB_CLIENT_ID
            client_secret = $env:IGDB_CLIENT_SECRET
            grant_type    = "client_credentials"
        }
        $token = $res.access_token
        if ($token) {
            $days = [math]::Round($res.expires_in / 86400)
            Ok "Twitch issued a token (valid about $days days)"
        } else {
            Bad "Twitch replied but gave no access_token"
        }
    } catch {
        # 400 here is almost always a typo in the id or secret; 403 means 2FA is not enabled yet.
        Bad "Twitch rejected the credentials: $($_.Exception.Message)"
    }

    if ($token) {
        try {
            $body = 'search "Hollow Knight"; fields name, aggregated_rating; limit 3;'
            $games = Invoke-RestMethod -Method Post -Uri "https://api.igdb.com/v4/games" -Headers @{
                "Client-ID"     = $env:IGDB_CLIENT_ID
                "Authorization" = "Bearer $token"
            } -ContentType "text/plain" -Body $body
            if ($games.Count -gt 0) {
                Ok "IGDB answered: $(($games | ForEach-Object { $_.name }) -join ', ')"
            } else {
                Bad "IGDB accepted the token but returned nothing"
            }
        } catch {
            Bad "IGDB query failed: $($_.Exception.Message)"
        }
    }
}

Write-Host "`nSteamGridDB" -ForegroundColor Cyan
if (-not $env:SGDB_KEY) {
    Skip "SGDB_KEY not set"
} else {
    try {
        $r = Invoke-RestMethod -Uri "https://www.steamgriddb.com/api/v2/search/autocomplete/Hollow%20Knight" `
            -Headers @{ Authorization = "Bearer $env:SGDB_KEY" }
        if ($r.success -and $r.data.Count -gt 0) {
            Ok "SteamGridDB answered: $(($r.data | Select-Object -First 3 | ForEach-Object { $_.name }) -join ', ')"
        } else {
            Bad "SteamGridDB accepted the key but returned nothing"
        }
    } catch {
        Bad "SteamGridDB rejected the key: $($_.Exception.Message)"
    }
}

Write-Host ""
if ($checked -eq 0) {
    Write-Host "Nothing to check -- no credentials were set. See the comment at the top." -ForegroundColor Yellow
    exit 1
} elseif ($fail -eq 0) {
    Write-Host "All configured credentials work." -ForegroundColor Green
} else {
    Write-Host "$fail check(s) failed." -ForegroundColor Red
    exit 1
}
